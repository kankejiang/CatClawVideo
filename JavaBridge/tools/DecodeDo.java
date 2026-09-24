// 一次性逆向工具：把 Guard 壳里运行时解密的字符串（do 值等）批量解出来。
// 用法: java -Dstubjar=<JavaBridge/bridge.jar 提供 android 桩> DecodeDo.java \
//           <dex2jar 产物 jar> <该类 javap -p -c 的输出文本> <类全名>
// 原理：目标类有 private static final short[] 字符串表 + com.github.catvod.parser 下的
//       public static String decode(short[], int off, int len, int key)；从 javap 反汇编文本里
//       抽出每个 invokestatic ([SIII) 之前的三个整数实参，逐个解码。
import java.lang.reflect.*;
import java.net.*;
import java.nio.file.*;
import java.util.*;
import java.util.jar.*;
import java.util.regex.*;

public class DecodeDo {
    public static void main(String[] a) throws Exception {
        String jar = a[0], disasm = a[1], target = a[2];
        List<URL> ul = new ArrayList<>();
        ul.add(Paths.get(jar).toUri().toURL());
        String stub = System.getProperty("stubjar", "");
        if (!stub.isEmpty()) ul.add(Paths.get(stub).toUri().toURL());
        // 目标类字段引用 okhttp3 等第三方类型，getDeclaredFields 会触发解析 → 把桥的依赖 jar 全挂上
        String deps = System.getProperty("depsdir", "");
        if (!deps.isEmpty()) {
            try (var s = Files.list(Paths.get(deps))) {
                for (Path p : s.filter(f -> f.toString().endsWith(".jar")).toList()) ul.add(p.toUri().toURL());
            }
        }
        var cl = new URLClassLoader(ul.toArray(new URL[0]), DecodeDo.class.getClassLoader());

        Class<?> tc = Class.forName(target, false, cl);
        short[] table = null;
        for (Field f : tc.getDeclaredFields()) {
            if (f.getType() == short[].class && Modifier.isStatic(f.getModifiers())) {
                f.setAccessible(true);
                try { table = (short[]) f.get(null); } catch (Throwable t) { System.out.println("字段读取失败 " + f.getName() + ": " + t); }
                if (table != null) break;
            }
        }
        if (table == null) { System.out.println("找不到 short[] 字符串表"); return; }
        System.out.println("table len=" + table.length);

        // 收集解码器
        List<Method> dec = new ArrayList<>();
        try (JarFile jf = new JarFile(jar)) {
            var en = jf.entries();
            while (en.hasMoreElements()) {
                String n = en.nextElement().getName();
                if (!n.startsWith("com/github/catvod/parser/") || !n.endsWith(".class")) continue;
                String cn = n.substring(0, n.length() - 6).replace('/', '.');
                try {
                    Class<?> c = Class.forName(cn, false, cl);
                    for (Method m : c.getDeclaredMethods()) {
                        Class<?>[] p = m.getParameterTypes();
                        if (Modifier.isStatic(m.getModifiers()) && p.length == 4 && p[0] == short[].class
                                && p[1] == int.class && p[2] == int.class && p[3] == int.class
                                && m.getReturnType() == String.class) {
                            m.setAccessible(true);
                            dec.add(m);
                        }
                    }
                } catch (Throwable ignored) { }
            }
        }
        System.out.println("解码器候选 " + dec.size() + " 个");

        // 从反汇编文本抽三元组：invokestatic ... ([SIII)Ljava/lang/String; 之前三条指令
        List<int[]> triples = new ArrayList<>();
        List<String> lines = Files.readAllLines(Paths.get(disasm));
        Pattern num = Pattern.compile("(sipush|bipush|iconst_m1|iconst_[0-9])\\s+(-?\\d+)?|^\\s*\\d+:\\s+iconst_(\\d)\\b");
        for (int i = 0; i < lines.size(); i++) {
            if (!lines.get(i).contains("([SIII)Ljava/lang/String;")) continue;
            int[] st = new int[3];
            int k = 0;
            for (int j = i - 1; j >= 0 && k < 3; j--) {
                String s = lines.get(j).trim();
                Matcher m = Pattern.compile("^(?:\\d+:\\s*)?(sipush|bipush)\\s+(-?\\d+)").matcher(s);
                if (m.find()) { st[2 - k++] = Integer.parseInt(m.group(2)); continue; }
                Matcher m2 = Pattern.compile("^(?:\\d+:\\s*)?iconst_(\\d)").matcher(s);
                if (m2.find()) { st[2 - k++] = Integer.parseInt(m2.group(1)); continue; }
                if (s.startsWith("//") || s.isEmpty()) continue;
                break;
            }
            if (k == 3) triples.add(st);
        }
        System.out.println("三元组 " + triples.size() + " 组");

        for (int[] t : triples) {
            String best = null;
            for (Method m : dec) {
                String r;
                try { r = (String) m.invoke(null, table, t[0], t[1], t[2]); } catch (Throwable e) { continue; }
                if (r == null || r.isEmpty()) continue;
                boolean printable = r.chars().allMatch(c -> c >= 0x20 && c < 0x7f);
                if (printable && (best == null || r.length() > best.length())) best = r;
            }
            System.out.println(t[0] + "," + t[1] + "," + t[2] + " → " + (best == null ? "(不可打印)" : best));
        }
    }
}
