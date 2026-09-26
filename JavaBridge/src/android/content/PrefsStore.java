package android.content;

import java.io.File;
import java.nio.charset.StandardCharsets;
import java.nio.file.Files;
import java.util.ArrayList;
import java.util.LinkedHashMap;
import java.util.HashSet;
import java.util.List;
import java.util.Map;
import java.util.Set;
import java.util.concurrent.ConcurrentHashMap;
import java.util.regex.Matcher;
import java.util.regex.Pattern;

/**
 * SharedPreferences 的桩后端：<b>按 prefs 文件名分存储</b>并<b>落盘</b>成 Android 同款 XML。
 *
 * <p>真机实测（2026-09-24，{@code adb shell run-as <pkg> cat shared_prefs/*.xml}）：
 * Guard 系网盘 jar 同时用两个独立文件 —— {@code spUtils.xml}（{@code quark_ck} 等 cookie）
 * 与 {@code myDrive_useState.xml}（{@code quark_useState} 等启用开关）。
 * 早先桥里所有 name 共用一张进程内全局 Map：两个存储互相串数据，进程一退全丢，
 * 所以「扫码登录成功 → 重启又要重扫」。</p>
 *
 * <p>落盘位置 {@code <data.dir>/shared_prefs/<name>.xml}，{@code data.dir} 由
 * {@link bridge.Server} 设为桥工作目录（安装版在用户数据区，可写）。</p>
 */
public final class PrefsStore {

    private static final Map<String, Map<String, Object>> STORES = new ConcurrentHashMap<>();

    /**
     * 本进程**真正动过**的键。落盘时只覆盖这些键，其余以磁盘上的值为准。
     * <p>为什么必须有：桌面可能同时跑多个桥 JVM（实测 28 秒内起 3 个，2026-09-25），
     * 每个都在启动时读走整份 spUtils 快照。若 flush 直接整表覆盖，
     * 任何一个 JVM 的 apply() 都会把它那份<b>旧快照</b>写回去，
     * 把别的 JVM 刚存进去的网盘 cookie 抹成空 —— 表现就是「扫码当场已登录、重启又未登录」。</p>
     */
    private static final Map<String, Set<String>> DIRTY = new ConcurrentHashMap<>();

    private PrefsStore() { }

    /** 取（必要时从磁盘读入）某个 prefs 文件对应的键值表。 */
    public static Map<String, Object> store(String name) {
        String n = normalize(name);
        return STORES.computeIfAbsent(n, PrefsStore::loadFromDisk);
    }

    /** 写一个键（并登记为"本进程动过"）。 */
    public static void put(String name, String key, Object value) {
        store(name).put(key, value);
        dirty(name).add(key);
    }

    /** 删一个键（同样登记，否则 flush 不会把它从盘上抹掉）。 */
    public static void remove(String name, String key) {
        store(name).remove(key);
        dirty(name).add(key);
    }

    /** 清空整个 prefs：把当前所有键都登记为动过。 */
    public static void clearAll(String name) {
        String n = normalize(name);
        Set<String> d = dirty(n);
        synchronized (store(n)) { d.addAll(store(n).keySet()); store(n).clear(); }
    }

    private static Set<String> dirty(String name) {
        return DIRTY.computeIfAbsent(normalize(name), k -> java.util.concurrent.ConcurrentHashMap.newKeySet());
    }

    /** 所有 prefs 合并成一张扁平表（供 QEMU guest 快照 / 宿主读登录态）。 */
    public static Map<String, Object> snapshot() {
        Map<String, Object> all = new LinkedHashMap<>();
        for (Map<String, Object> m : STORES.values()) all.putAll(m);
        return all;
    }

    /** 删除某个 prefs（文件 + 内存）。 */
    public static void drop(String name) {
        String n = normalize(name);
        STORES.remove(n);
        DIRTY.remove(n);
        File f = file(n);
        if (f.isFile()) {
            try { f.delete(); } catch (Throwable ignored) { }
        }
    }

    /**
     * 把<b>本进程动过的键</b>合进磁盘现状后写回（读-改-写）。
     * <p>没动过任何键就直接返回，绝不写文件 —— 否则空手一次 apply 就能把别的 JVM 的成果抹掉。</p>
     */
    public static boolean flush(String name) {
        String n = normalize(name);
        Map<String, Object> m = STORES.get(n);
        if (m == null) return true;
        Set<String> d = DIRTY.get(n);
        if (d == null || d.isEmpty()) return true;
        // 1) 读磁盘现状 2) 只把自己动过的键盖上去 3) 内存与磁盘对齐 4) 写文件
        Map<String, Object> merged = loadFromDisk(n);
        Map<String, Object> out;
        synchronized (m) {
            for (String k : d) {
                Object v = m.get(k);
                if (v == null) merged.remove(k); else merged.put(k, v);
            }
            out = new LinkedHashMap<>(merged);
            m.clear();
            m.putAll(merged);
        }
        d.clear();
        try {
            File f = file(n);
            File parent = f.getParentFile();
            if (parent != null && !parent.isDirectory()) parent.mkdirs();
            StringBuilder sb = new StringBuilder();
            sb.append("<?xml version='1.0' encoding='utf-8' standalone='yes' ?>\n<map>\n");
            for (Map.Entry<String, Object> e : out.entrySet()) {
                String k = esc(e.getKey());
                Object v = e.getValue();
                if (v instanceof Integer) sb.append("    <int name=\"").append(k).append("\" value=\"").append(v).append("\" />\n");
                else if (v instanceof Long) sb.append("    <long name=\"").append(k).append("\" value=\"").append(v).append("\" />\n");
                else if (v instanceof Float) sb.append("    <float name=\"").append(k).append("\" value=\"").append(v).append("\" />\n");
                else if (v instanceof Boolean) sb.append("    <boolean name=\"").append(k).append("\" value=\"").append(v).append("\" />\n");
                else if (v instanceof Set) {
                    sb.append("    <string-set name=\"").append(k).append("\">\n");
                    for (Object s : (Set<?>) v) sb.append("        <item>").append(esc(String.valueOf(s))).append("</item>\n");
                    sb.append("    </string-set>\n");
                } else sb.append("    <string name=\"").append(k).append("\">").append(esc(String.valueOf(v))).append("</string>\n");
            }
            sb.append("</map>\n");
            Files.write(f.toPath(), sb.toString().getBytes(StandardCharsets.UTF_8));
            StringBuilder have = new StringBuilder();
            synchronized (m) {
                for (Map.Entry<String, Object> e : m.entrySet())
                    if (e.getValue() instanceof String && !((String) e.getValue()).isEmpty())
                        have.append(e.getKey()).append('=').append(((String) e.getValue()).length()).append(' ');
            }
            System.err.println("[prefs] 落盘 " + n + " " + m.size() + " 键 非空: " + (have.length() == 0 ? "(无)" : have.toString().trim()));
            // guest（真 ART）的 /data 是 tmpfs，VM 冷启即清：把落盘内容上行宿主持久化，
            // 下次 guest 起来由宿主经 op=prefsput 回灌（JRE 桥 data.dir 本就在宿主盘，无需同步）
            if ("Dalvik".equals(System.getProperty("java.vm.name"))) {
                try {
                    System.out.println(new org.json.JSONObject()
                            .put("ev", "prefs-sync").put("name", n).put("xml", sb.toString()));
                    System.out.flush();
                } catch (Throwable ignored) { }
            }
            return true;
        } catch (Throwable t) {
            System.err.println("[prefs] 写入失败 " + name + ": " + t);
            return false;
        }
    }

    /** Android 用 {@code getSharedPreferences("x")} 与默认 prefs 两种写法，统一去掉 .xml 后缀。 */
    static String normalize(String name) {
        if (name == null || name.isEmpty()) return "default";
        String n = name.replace('\\', '/');
        int slash = n.lastIndexOf('/');
        if (slash >= 0) n = n.substring(slash + 1);       // 允许传文件路径
        if (n.endsWith(".xml")) n = n.substring(0, n.length() - 4);
        return n.isEmpty() ? "default" : n;
    }

    private static File file(String name) {
        String base = System.getProperty("data.dir", "data");
        return new File(new File(base, "shared_prefs"), name + ".xml");
    }

    private static final Pattern P_STRING = Pattern.compile("<string name=\"(.*?)\">(.*?)</string>", Pattern.DOTALL);
    private static final Pattern P_INT = Pattern.compile("<int name=\"(.*?)\" value=\"(.*?)\"");
    private static final Pattern P_LONG = Pattern.compile("<long name=\"(.*?)\" value=\"(.*?)\"");
    private static final Pattern P_FLOAT = Pattern.compile("<float name=\"(.*?)\" value=\"(.*?)\"");
    private static final Pattern P_BOOL = Pattern.compile("<boolean name=\"(.*?)\" value=\"(.*?)\"");
    private static final Pattern P_SET = Pattern.compile("<string-set name=\"(.*?)\">(.*?)</string-set>", Pattern.DOTALL);
    private static final Pattern P_ITEM = Pattern.compile("<item>(.*?)</item>", Pattern.DOTALL);

    private static Map<String, Object> loadFromDisk(String name) {
        Map<String, Object> m = new LinkedHashMap<>();
        File f = file(name);
        if (!f.isFile()) return m;
        String text;
        try {
            text = new String(Files.readAllBytes(f.toPath()), StandardCharsets.UTF_8);
        } catch (Throwable t) {
            System.err.println("[prefs] 读取失败 " + name + ": " + t);
            return m;
        }
        Matcher mt;
        mt = P_STRING.matcher(text);
        while (mt.find()) m.put(unesc(mt.group(1)), unesc(mt.group(2)));
        mt = P_INT.matcher(text);
        while (mt.find()) m.put(unesc(mt.group(1)), parseInt(mt.group(2)));
        mt = P_LONG.matcher(text);
        while (mt.find()) m.put(unesc(mt.group(1)), parseLong(mt.group(2)));
        mt = P_FLOAT.matcher(text);
        while (mt.find()) m.put(unesc(mt.group(1)), parseFloat(mt.group(2)));
        mt = P_BOOL.matcher(text);
        while (mt.find()) m.put(unesc(mt.group(1)), "true".equalsIgnoreCase(mt.group(2).trim()));
        mt = P_SET.matcher(text);
        while (mt.find()) {
            Set<String> vals = new HashSet<>();
            Matcher mi = P_ITEM.matcher(mt.group(2));
            while (mi.find()) vals.add(unesc(mi.group(1)));
            m.put(unesc(mt.group(1)), vals);
        }
        System.err.println("[prefs] 已载入 " + name + "（" + m.size() + " 键）");
        return m;
    }

    private static int parseInt(String s) { try { return Integer.parseInt(s.trim()); } catch (Exception e) { return 0; } }
    private static long parseLong(String s) { try { return Long.parseLong(s.trim()); } catch (Exception e) { return 0L; } }
    private static float parseFloat(String s) { try { return Float.parseFloat(s.trim()); } catch (Exception e) { return 0f; } }

    private static String esc(String s) {
        if (s == null) return "";
        StringBuilder sb = new StringBuilder(s.length() + 8);
        for (int i = 0; i < s.length(); i++) {
            char c = s.charAt(i);
            switch (c) {
                case '&': sb.append("&amp;"); break;
                case '<': sb.append("&lt;"); break;
                case '>': sb.append("&gt;"); break;
                case '"': sb.append("&quot;"); break;
                case '\'': sb.append("&apos;"); break;
                default: sb.append(c);
            }
        }
        return sb.toString();
    }

    private static String unesc(String s) {
        if (s == null) return "";
        return s.replace("&lt;", "<").replace("&gt;", ">").replace("&quot;", "\"")
                .replace("&apos;", "'").replace("&amp;", "&");   // &amp; 必须最后
    }

    /** 供诊断输出：当前有哪些 prefs 文件在内存里。 */
    public static List<String> names() { return new ArrayList<>(STORES.keySet()); }
}
