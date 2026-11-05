// SPDX-License-Identifier: MPL-2.0
// Copyright (c) 2026 ChenchenMC <https://github.com/ChenchenMC-cn/EternityCraftLauncher>
package ecl.runtime;

import java.lang.management.ManagementFactory;
import java.util.LinkedHashMap;
import java.util.Map;

/**
 * Reports the capabilities of the JVM that is currently running.
 *
 * <p>The launcher executes this class as {@code java -cp ECL.Runtime.jar ecl.runtime.Probe}
 * for every candidate runtime on the machine, then parses the single line of JSON
 * printed to stdout. Only Java 8 APIs are used so that legacy runtimes (which are
 * exactly the interesting case) can be probed too.
 */
public final class Probe {

    private Probe() {
    }

    public static void main(String[] args) {
        StringBuilder sb = new StringBuilder(512);
        sb.append('{');
        field(sb, "ok", true);
        sb.append(',');
        field(sb, "version", sys("java.version"));
        sb.append(',');
        field(sb, "specVersion", sys("java.specification.version"));
        sb.append(',');
        field(sb, "vmName", sys("java.vm.name"));
        sb.append(',');
        field(sb, "vendor", sys("java.vendor"));
        sb.append(',');
        field(sb, "home", sys("java.home"));
        sb.append(',');
        field(sb, "osArch", sys("os.arch"));
        sb.append(',');
        field(sb, "osName", sys("os.name"));
        sb.append(',');
        num(sb, "major", majorVersion());
        sb.append(',');
        num(sb, "bitness", "64".equals(sys("sun.arch.data.model")) ? 64 : 32);
        sb.append(',');
        field(sb, "classVersion", classVersion());
        sb.append(',');
        num(sb, "maxHeapMB", Runtime.getRuntime().maxMemory() / (1024L * 1024L));
        sb.append(',');
        num(sb, "cpus", Runtime.getRuntime().availableProcessors());
        sb.append(',');
        field(sb, "uptimeOwner", processOwner());
        sb.append('}');
        System.out.println(sb);
        System.out.flush();
    }

    private static String sys(String key) {
        String v = System.getProperty(key);
        return v == null ? "" : v;
    }

    /** "1.8.0_402" -> 8, "17.0.19" -> 17, "21-ea" -> 21. */
    static int majorVersion() {
        String raw = sys("java.specification.version");
        if (raw != null && !raw.isEmpty()) {
            try {
                if (raw.startsWith("1.")) return Integer.parseInt(raw.substring(2, 3));
                int dot = raw.indexOf('.');
                return Integer.parseInt(dot < 0 ? raw : raw.substring(0, dot));
            } catch (NumberFormatException ignored) {
                // fall through to java.version
            }
        }
        try {
            String v = sys("java.version");
            if (v.startsWith("1.")) return Integer.parseInt(v.substring(2, 3));
            int dot = v.indexOf('.');
            return Integer.parseInt(dot < 0 ? v : v.substring(0, dot));
        } catch (RuntimeException ignored) {
            return 0;
        }
    }

    private static String classVersion() {
        try {
            String v = sys("java.class.version");
            return v.isEmpty() ? "" : v;
        } catch (RuntimeException e) {
            return "";
        }
    }

    private static String processOwner() {
        try {
            String name = ManagementFactory.getRuntimeMXBean().getName();
            int at = name.indexOf('@');
            return at > 0 ? name.substring(0, at) : "";
        } catch (Throwable t) {
            return "";
        }
    }

    private static void field(StringBuilder sb, String key, String value) {
        sb.append('"').append(key).append("\":");
        escape(sb, value);
    }

    private static void field(StringBuilder sb, String key, boolean value) {
        sb.append('"').append(key).append("\":").append(value);
    }

    private static void num(StringBuilder sb, String key, long value) {
        sb.append('"').append(key).append("\":").append(value);
    }

    private static void escape(StringBuilder sb, String s) {
        sb.append('"');
        for (int i = 0; i < s.length(); i++) {
            char c = s.charAt(i);
            switch (c) {
                case '"':
                    sb.append("\\\"");
                    break;
                case '\\':
                    sb.append("\\\\");
                    break;
                case '\n':
                    sb.append("\\n");
                    break;
                case '\r':
                    sb.append("\\r");
                    break;
                case '\t':
                    sb.append("\\t");
                    break;
                default:
                    if (c < 0x20 || c > 0x7e) {
                        sb.append(String.format("\\u%04x", (int) c));
                    } else {
                        sb.append(c);
                    }
            }
        }
        sb.append('"');
    }

    /** Kept for manual inspection: {@code java -cp ECL.Runtime.jar ecl.runtime.Probe --pretty}. */
    static Map<String, String> describe() {
        Map<String, String> m = new LinkedHashMap<String, String>();
        m.put("version", sys("java.version"));
        m.put("vendor", sys("java.vendor"));
        m.put("vm", sys("java.vm.name"));
        m.put("home", sys("java.home"));
        return m;
    }
}
