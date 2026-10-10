import net.minecraft.SharedConstants;
import net.minecraft.core.BlockPos;
import net.minecraft.core.registries.BuiltInRegistries;
import net.minecraft.server.Bootstrap;
import net.minecraft.world.entity.EntityType;
import net.minecraft.world.item.BlockItem;
import net.minecraft.world.item.Item;
import net.minecraft.world.level.EmptyBlockGetter;
import net.minecraft.world.level.block.Block;
import net.minecraft.world.level.block.state.BlockState;
import net.minecraft.world.level.material.FluidState;
import net.minecraft.world.phys.AABB;

import java.io.FileWriter;
import java.util.ArrayList;
import java.util.HashMap;
import java.util.List;
import java.util.Locale;
import java.util.Map;

/**
 * Dumps the per-block-state and per-entity facts the vanilla data reports leave out (hardness, collision boxes,
 * friction, fluids, entity sizes) by bootstrapping the game's own registries from a Mojang-mapped server jar
 * (Paper's versions/<v>/paper-<v>.jar). Run by tools/DataGen; output is JSON:
 *   states:  [ [block, hardness, flags, shape, friction, speedFactor, jumpFactor, fluidAmount], ... ] by state id
 *   blocks:  [ [name, defaultState, explosionResistance], ... ] by block id
 *   shapes:  [ [[x1,y1,z1,x2,y2,z2], ...], ... ]
 *   entities:[ [name, width, height, eyeHeight], ... ] by entity type id
 *   items:   [ [name, placedBlock or ""], ... ] by item id
 * flags: 1 air, 2 requires correct tool, 4 replaceable, 8 water, 16 lava, 32 fluid source, 64 blocks motion
 */
public class VertexDump {
    public static void main(String[] args) throws Exception {
        SharedConstants.tryDetectVersion();
        Bootstrap.bootStrap();

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
            blocks.append("[\"").append(BuiltInRegistries.BLOCK.getKey(b)).append("\",")
                .append(Block.getId(b.defaultBlockState())).append(',').append(n(b.getExplosionResistance())).append(']');
        }

        StringBuilder entities = new StringBuilder();
        for (EntityType<?> e : BuiltInRegistries.ENTITY_TYPE) {
            if (entities.length() > 0) entities.append(",\n");
            entities.append("[\"").append(BuiltInRegistries.ENTITY_TYPE.getKey(e)).append("\",")
                .append(n(e.getWidth())).append(',').append(n(e.getHeight())).append(',').append(n(e.getDimensions().eyeHeight())).append(']');
        }

        StringBuilder items = new StringBuilder();
        for (Item item : BuiltInRegistries.ITEM) {
            if (items.length() > 0) items.append(",\n");
            String placed = item instanceof BlockItem bi ? BuiltInRegistries.BLOCK.getKey(bi.getBlock()).toString() : "";
            items.append("[\"").append(BuiltInRegistries.ITEM.getKey(item)).append("\",\"").append(placed).append("\"]");
        }

        // The player's base attribute values (attack_damage 1, attack_speed 4...): the server does not sync every
        // attribute, and the client computes the rest from these plus its equipment.
        StringBuilder playerAttributes = new StringBuilder();
        var supplier = net.minecraft.world.entity.ai.attributes.DefaultAttributes.getSupplier(EntityType.PLAYER);
        for (net.minecraft.world.entity.ai.attributes.Attribute a : BuiltInRegistries.ATTRIBUTE) {
            var holder = BuiltInRegistries.ATTRIBUTE.wrapAsHolder(a);
            if (!supplier.hasAttribute(holder)) continue;
            if (playerAttributes.length() > 0) playerAttributes.append(",\n");
            playerAttributes.append("[\"").append(BuiltInRegistries.ATTRIBUTE.getKey(a)).append("\",").append(n(supplier.getBaseValue(holder))).append(']');
        }

        try (FileWriter w = new FileWriter(args[0])) {
            w.write("{\"playerAttributes\":[\n" + playerAttributes + "],\n");
            w.write("\"states\":[\n" + states + "],\n\"blocks\":[\n" + blocks + "],\n\"shapes\":[\n" + String.join(",\n", shapes)
                + "],\n\"entities\":[\n" + entities + "],\n\"items\":[\n" + items + "]}\n");
        }
        System.out.println("dumped " + expected + " states, " + shapes.size() + " shapes");
        System.exit(0);
    }

    private static String n(double v) {
        if (v == Math.rint(v) && Math.abs(v) < 1e15) return Long.toString((long) v);
        return String.format(Locale.ROOT, "%s", v);
    }
}
