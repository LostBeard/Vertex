import java.io.BufferedReader;
import java.io.InputStreamReader;
import java.io.Reader;
import java.nio.charset.StandardCharsets;
import java.nio.file.Files;
import java.nio.file.Path;
import java.util.zip.ZipFile;

import net.fabricmc.loader.impl.lib.mappingio.adapter.MappingSourceNsSwitch;
import net.fabricmc.loader.impl.lib.mappingio.format.proguard.ProGuardFileReader;
import net.fabricmc.loader.impl.lib.mappingio.format.tiny.Tiny1FileReader;
import net.fabricmc.loader.impl.lib.mappingio.tree.MappingTree;
import net.fabricmc.loader.impl.lib.mappingio.tree.MemoryMappingTree;
import net.fabricmc.loader.impl.lib.tinyremapper.IMappingProvider;
import net.fabricmc.loader.impl.lib.tinyremapper.NonClassCopyMode;
import net.fabricmc.loader.impl.lib.tinyremapper.OutputConsumerPath;
import net.fabricmc.loader.impl.lib.tinyremapper.TinyRemapper;

/**
 * Remaps a jar compiled against Mojang names to Fabric's runtime (intermediary) names, using only what a Fabric
 * server already has: the loader's own copies of mapping-io and tiny-remapper, the intermediary mappings jar, and
 * Mojang's published server mappings (ProGuard format). The two mapping sets meet on the official (obfuscated) names.
 * Run by tools/ModDump with the fabric-loader and ASM jars on the classpath:
 *   java -cp <loader;asm...> RemapToIntermediary.java <server mappings .txt> <intermediary jar> <in.jar> <out.jar> <classpath jars...>
 */
public class RemapToIntermediary {
    public static void main(String[] args) throws Exception {
        Path proguard = Path.of(args[0]), intermediaryJar = Path.of(args[1]), in = Path.of(args[2]), out = Path.of(args[3]);

        MemoryMappingTree tree = new MemoryMappingTree();
        try (ZipFile zip = new ZipFile(intermediaryJar.toFile());
             Reader r = new BufferedReader(new InputStreamReader(zip.getInputStream(zip.getEntry("mappings/mappings.tiny")), StandardCharsets.UTF_8))) {
            Tiny1FileReader.read(r, tree); // official -> intermediary
        }
        MemoryMappingTree mojang = new MemoryMappingTree();
        try (Reader r = Files.newBufferedReader(proguard)) {
            ProGuardFileReader.read(r, "named", "official", mojang);
        }
        mojang.accept(new MappingSourceNsSwitch(tree, "official", true)); // merge: official -> intermediary, named

        int named = tree.getNamespaceId("named"), inter = tree.getNamespaceId("intermediary");
        if (named < 0 || inter < 0) throw new IllegalStateException("namespaces after merge: " + tree.getDstNamespaces());
        int[] counts = new int[3];
        IMappingProvider provider = acceptor -> {
            for (MappingTree.ClassMapping c : tree.getClasses()) {
                String cn = c.getName(named), ci = c.getName(inter);
                if (cn == null || ci == null) continue;
                acceptor.acceptClass(cn, ci);
                counts[0]++;
                for (MappingTree.MethodMapping m : c.getMethods()) {
                    String mn = m.getName(named), mi = m.getName(inter), md = m.getDesc(named);
                    if (mn == null || mi == null || md == null) continue;
                    acceptor.acceptMethod(new IMappingProvider.Member(cn, mn, md), mi);
                    counts[1]++;
                }
                for (MappingTree.FieldMapping f : c.getFields()) {
                    String fn = f.getName(named), fi = f.getName(inter), fd = f.getDesc(named);
                    if (fn == null || fi == null || fd == null) continue;
                    acceptor.acceptField(new IMappingProvider.Member(cn, fn, fd), fi);
                    counts[2]++;
                }
            }
        };

        TinyRemapper remapper = TinyRemapper.newRemapper((level, message) -> System.out.println("tiny-remapper " + level + ": " + message))
            .withMappings(provider).build();
        try (OutputConsumerPath output = new OutputConsumerPath.Builder(out).build()) {
            output.addNonClassFiles(in, NonClassCopyMode.UNCHANGED, remapper);
            var tag = remapper.createInputTag();
            remapper.readInputsAsync(tag, in).join();
            Path[] classpath = new Path[args.length - 4];
            for (int i = 4; i < args.length; i++) classpath[i - 4] = Path.of(args[i]);
            remapper.readClassPathAsync(classpath).join();
            remapper.apply(output, tag);
        } finally {
            remapper.finish();
        }
        System.out.println("remapped with " + counts[0] + " classes, " + counts[1] + " methods, " + counts[2] + " fields");
    }
}
