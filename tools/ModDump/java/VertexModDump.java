import java.io.FileWriter;
import java.lang.reflect.Method;
import java.lang.reflect.Proxy;
import java.util.ArrayList;
import java.util.HashMap;
import java.util.List;
import java.util.Locale;
import java.util.Map;

import com.google.gson.JsonArray;
import com.google.gson.JsonElement;
import com.google.gson.JsonObject;
import com.mojang.serialization.Codec;
import com.mojang.serialization.JsonOps;

import net.fabricmc.api.DedicatedServerModInitializer;
import net.minecraft.core.BlockPos;
import net.minecraft.core.HolderLookup;
import net.minecraft.core.component.DataComponentType;
import net.minecraft.core.component.TypedDataComponent;
import net.minecraft.core.registries.BuiltInRegistries;
import net.minecraft.resources.RegistryOps;
import net.minecraft.resources.ResourceLocation;
import net.minecraft.server.MinecraftServer;
import net.minecraft.world.entity.EntityDimensions;
import net.minecraft.world.entity.EntityType;
import net.minecraft.world.item.BlockItem;
import net.minecraft.world.item.Item;
import net.minecraft.world.level.EmptyBlockGetter;
import net.minecraft.world.level.block.Block;
import net.minecraft.world.level.block.state.BlockState;
import net.minecraft.world.level.block.state.properties.Property;
import net.minecraft.world.level.material.FluidState;
import net.minecraft.world.phys.AABB;

/**
 * Run by tools/ModDump inside a throwaway copy of a modded (Fabric) server: once the server has started (every mod's
 * blocks registered, state ids final), dumps every block and block state, vanilla and modded, in the same format as
 * tools/DataGen/java/VertexDump.java, then stops the JVM. Written with Mojang names, compiled against the
 * Mojang-mapped Paper jar, and remapped to Fabric's intermediary names by tools/ModDump (tiny-remapper), so the
 * facts come from the mods' own code. Fabric API's SERVER_STARTED is registered through reflection: its signatures
 * use intermediary class names, which this source cannot name.
 *
 * Output (path from the vertex.moddump system property):
 *   blocks: [ [name, firstState, stateCount, [[property, [values...]], ...] (sorted by name, as StateDefinition)], ... ]
 *   states: [ [block, hardness, flags, shape, friction, speedFactor, jumpFactor, fluidAmount], ... ] by state id
 *   shapes: [ [[x1,y1,z1,x2,y2,z2], ...], ... ]
 *   items: [ {name, block?, components: {...}}, ... ] the mods' items only: default components as the game's codecs
 *          write them (the format of the vanilla item report, so one parser reads both)
 *   entities: [ [name, width, height, eyeHeight], ... ] the mods' entity types only
 * flags: 1 air, 2 requires correct tool, 4 replaceable, 8 water, 16 lava, 32 fluid source, 64 blocks motion
 */
public class VertexModDump implements DedicatedServerModInitializer {
    @Override
    public void onInitializeServer() {
        try {
            Class<?> events = Class.forName("net.fabricmc.fabric.api.event.lifecycle.v1.ServerLifecycleEvents");
            Object started = events.getField("SERVER_STARTED").get(null);
            Class<?> listener = Class.forName("net.fabricmc.fabric.api.event.lifecycle.v1.ServerLifecycleEvents$ServerStarted");
            Object proxy = Proxy.newProxyInstance(listener.getClassLoader(), new Class<?>[] { listener }, (p, m, a) -> {
                if (m.getDeclaringClass() == Object.class) return m.invoke(this, a);
                dumpAndExit((MinecraftServer) a[0]);
                return null;
            });
            // Through the public API type: the implementation class (ArrayBackedEvent) is not accessible.
            Class.forName("net.fabricmc.fabric.api.event.Event").getMethod("register", Object.class).invoke(started, proxy);
        } catch (ReflectiveOperationException e) {
            throw new IllegalStateException("Fabric API lifecycle events not found", e);
        }
    }

    private static void dumpAndExit(MinecraftServer server) {
        int code = 0;
        try {
            dump(System.getProperty("vertex.moddump"), server);
        } catch (Throwable t) {
            t.printStackTrace();
            code = 1;
        }
        System.out.println("VERTEX-MODDUMP-DONE " + code);
        Runtime.getRuntime().halt(code); // nothing to save: the server folder is thrown away
    }

    private static void dump(String path, MinecraftServer server) throws Exception {
        Map<String, Integer> shapeIds = new HashMap<>();
        List<String> shapes = new ArrayList<>();
        StringBuilder states = new StringBuilder();
        int expected = 0;
        for (BlockState s : Block.BLOCK_STATE_REGISTRY) {
            int id = Block.getId(s);
            if (id != expected) throw new IllegalStateException("state ids not dense at " + id);
            expected++;
            StringBuilder shape = new StringBuilder("[");
            boolean first = true;
            for (AABB box : s.getCollisionShape(EmptyBlockGetter.INSTANCE, BlockPos.ZERO).toAabbs()) {
                if (!first) shape.append(',');
                first = false;
                shape.append('[').append(n(box.minX)).append(',').append(n(box.minY)).append(',').append(n(box.minZ)).append(',')
                     .append(n(box.maxX)).append(',').append(n(box.maxY)).append(',').append(n(box.maxZ)).append(']');
            }
            shape.append(']');
            Integer shapeId = shapeIds.get(shape.toString());
            if (shapeId == null) {
                shapeId = shapes.size();
                shapeIds.put(shape.toString(), shapeId);
                shapes.add(shape.toString());
            }
            FluidState fluid = s.getFluidState();
            String fluidName = fluid.isEmpty() ? "" : BuiltInRegistries.FLUID.getKey(fluid.getType()).getPath();
            int flags = (s.isAir() ? 1 : 0)
                | (s.requiresCorrectToolForDrops() ? 2 : 0)
                | (s.canBeReplaced() ? 4 : 0)
                | (fluidName.contains("water") ? 8 : 0)
                | (fluidName.contains("lava") ? 16 : 0)
                | (!fluid.isEmpty() && fluid.isSource() ? 32 : 0)
                | (s.blocksMotion() ? 64 : 0);
            Block b = s.getBlock();
            if (states.length() > 0) states.append(",\n");
            states.append('[').append(BuiltInRegistries.BLOCK.getId(b)).append(',')
                .append(n(s.getDestroySpeed(EmptyBlockGetter.INSTANCE, BlockPos.ZERO))).append(',')
                .append(flags).append(',').append(shapeId).append(',')
                .append(n(b.getFriction())).append(',').append(n(b.getSpeedFactor())).append(',').append(n(b.getJumpFactor())).append(',')
                .append(fluid.isEmpty() ? 0 : fluid.getAmount()).append(']');
        }
        StringBuilder blocks = new StringBuilder();
        for (Block b : BuiltInRegistries.BLOCK) {
            if (blocks.length() > 0) blocks.append(",\n");
            var possible = b.getStateDefinition().getPossibleStates();
            blocks.append("[\"").append(BuiltInRegistries.BLOCK.getKey(b)).append("\",")
                .append(Block.getId(possible.get(0))).append(',').append(possible.size()).append(",[");
            boolean firstProp = true;
            for (Property<?> p : b.getStateDefinition().getProperties()) {
                if (!firstProp) blocks.append(',');
                firstProp = false;
                blocks.append("[\"").append(p.getName()).append("\",[");
                boolean firstValue = true;
                for (Object v : p.getPossibleValues()) {
                    if (!firstValue) blocks.append(',');
                    firstValue = false;
                    blocks.append('"').append(valueName(p, v)).append('"');
                }
                blocks.append("]]");
            }
            blocks.append("],").append(Block.getId(b.defaultBlockState())).append(',').append(n(b.getExplosionResistance())).append(']');
        }
        RegistryOps<JsonElement> ops = RegistryOps.create(JsonOps.INSTANCE, registries(server));
        JsonArray items = new JsonArray();
        for (Item it : BuiltInRegistries.ITEM) {
            ResourceLocation key = BuiltInRegistries.ITEM.getKey(it);
            if ("minecraft".equals(key.getNamespace())) continue;
            JsonObject components = new JsonObject();
            for (TypedDataComponent<?> c : it.components()) encode(c, ops, components);
            JsonObject o = new JsonObject();
            o.addProperty("name", key.toString());
            if (it instanceof BlockItem bi) o.addProperty("block", BuiltInRegistries.BLOCK.getKey(bi.getBlock()).toString());
            o.add("components", components);
            items.add(o);
        }
        JsonArray entities = new JsonArray();
        for (EntityType<?> t : BuiltInRegistries.ENTITY_TYPE) {
            ResourceLocation key = BuiltInRegistries.ENTITY_TYPE.getKey(t);
            if ("minecraft".equals(key.getNamespace())) continue;
            EntityDimensions d = t.getDimensions();
            JsonArray e = new JsonArray();
            e.add(key.toString());
            e.add(d.width());
            e.add(d.height());
            e.add(d.eyeHeight());
            entities.add(e);
        }
        try (FileWriter w = new FileWriter(path)) {
            w.write("{\"blocks\":[\n" + blocks + "],\n\"states\":[\n" + states + "],\n\"shapes\":[\n" + String.join(",\n", shapes)
                + "],\n\"items\":" + items + ",\n\"entities\":" + entities + "}\n");
        }
        System.out.println("dumped " + expected + " states, " + shapes.size() + " shapes, " + items.size() + " modded items, "
            + entities.size() + " modded entity types");
    }

    /**
     * The server's registries, for codecs that refer to tags and registry entries (a tool's block tags). Found by
     * type: a direct server.registryAccess() call kept its Mojang name through the remap (NoSuchMethodError at run
     * time), while class names map correctly.
     */
    private static HolderLookup.Provider registries(MinecraftServer server) throws Exception {
        for (Method m : server.getClass().getMethods())
            if (m.getParameterCount() == 0 && HolderLookup.Provider.class.isAssignableFrom(m.getReturnType()))
                return (HolderLookup.Provider) m.invoke(server);
        throw new IllegalStateException("no registry access on " + server.getClass());
    }

    /** One component under its name (a vanilla one without "minecraft:", as the item report has them); ones with no codec are skipped. */
    private static <T> void encode(TypedDataComponent<T> c, RegistryOps<JsonElement> ops, JsonObject out) {
        DataComponentType<T> type = c.type();
        Codec<T> codec = type.codec();
        if (codec == null) return; // transient: never saved or sent
        ResourceLocation key = BuiltInRegistries.DATA_COMPONENT_TYPE.getKey(type);
        if (key == null) return;
        String name = "minecraft".equals(key.getNamespace()) ? key.getPath() : key.toString();
        codec.encodeStart(ops, c.value()).result().ifPresent(j -> out.add(name, j));
    }

    @SuppressWarnings({ "unchecked", "rawtypes" })
    private static String valueName(Property p, Object v) {
        return p.getName((Comparable) v);
    }

    private static String n(double v) {
        if (v == Math.rint(v) && Math.abs(v) < 1e15) return Long.toString((long) v);
        return String.format(Locale.ROOT, "%s", v);
    }
}
