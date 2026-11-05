// SPDX-License-Identifier: MPL-2.0
// Copyright (c) 2026 ChenchenMC <https://github.com/ChenchenMC-cn/EternityCraftLauncher>
package ecl.runtime;

import java.lang.reflect.InvocationTargetException;
import java.lang.reflect.Method;
import java.lang.reflect.Modifier;

/**
 * Entry point that wraps the real game main class.
 *
 * <p>Invoked as:
 * <pre>java -cp ECL.Runtime.jar;libraries... ecl.runtime.Bootstrap &lt;realMainClass&gt; [game args...]</pre>
 *
 * <p>The wrapper earns its place by turning the most common start-up failures —
 * missing class, wrong JVM version, broken native library — into one readable line
 * on stderr that the launcher can surface verbatim, instead of a 40 frame
 * stack trace that starts with {@code java.lang.reflect.InvocationTargetException}.
 */
public final class Bootstrap {

    public static final String TAG = "[ECL] ";
    private static final int EXIT_NO_MAIN_CLASS = 70;
    private static final int EXIT_MAIN_THREW = 71;

    private Bootstrap() {
    }

    public static void main(String[] args) {
        if (args.length < 1) {
            System.err.println(TAG + "bootstrap: no target main class supplied");
            System.exit(EXIT_NO_MAIN_CLASS);
            return;
        }

        final String target = args[0];
        final String[] gameArgs = new String[args.length - 1];
        System.arraycopy(args, 1, gameArgs, 0, gameArgs.length);

        // Minecraft names its render thread "Client thread"; some mods rely on it.
        Thread.currentThread().setName("Client thread");

        Class<?> mainClass;
        try {
            mainClass = Class.forName(target, true, Bootstrap.class.getClassLoader());
        } catch (ClassNotFoundException e) {
            System.err.println(TAG + "Main class not found: " + target);
            System.err.println(TAG + "The classpath is incomplete. Re-run the install step for this version.");
            System.exit(EXIT_NO_MAIN_CLASS);
            return;
        } catch (Throwable t) {
            System.err.println(TAG + "Failed to load " + target + ": " + t);
            System.exit(EXIT_NO_MAIN_CLASS);
            return;
        }

        Method main;
        try {
            main = mainClass.getMethod("main", String[].class);
            if (!Modifier.isStatic(main.getModifiers())) throw new NoSuchMethodException("main is not static");
        } catch (NoSuchMethodException e) {
            System.err.println(TAG + target + " has no public static main(String[])");
            System.exit(EXIT_NO_MAIN_CLASS);
            return;
        }

        try {
            main.invoke(null, (Object) gameArgs);
        } catch (InvocationTargetException e) {
            Throwable cause = e.getCause() == null ? e : e.getCause();
            report("game crashed during start-up", cause);
            System.exit(EXIT_MAIN_THREW);
        } catch (Throwable t) {
            report("game crashed during start-up", t);
            System.exit(EXIT_MAIN_THREW);
        }
    }

    private static void report(String what, Throwable t) {
        System.err.println(TAG + what + ": " + t.getClass().getName()
                + (t.getMessage() == null ? "" : ": " + t.getMessage()));
        StackTraceElement[] st = t.getStackTrace();
        int limit = Math.min(st.length, 12);
        for (int i = 0; i < limit; i++) {
            System.err.println(TAG + "    at " + st[i]);
        }
        if (st.length > limit) {
            System.err.println(TAG + "    ... " + (st.length - limit) + " more");
        }
        Throwable c = t.getCause();
        if (c != null && c != t) {
            System.err.println(TAG + "caused by:");
            report("", c);
        }
        String hint = hintFor(t);
        if (hint != null) System.err.println(TAG + "hint: " + hint);
    }

    private static String hintFor(Throwable t) {
        String msg = String.valueOf(t.getMessage());
        String cls = t.getClass().getName();
        if (cls.contains("UnsupportedClassVersionError")) {
            return "This version needs a newer Java runtime. Open ECL settings and pick Java 17 or 21.";
        }
        if (cls.contains("NoClassDefFoundError") || cls.contains("ClassNotFoundException")) {
            return "A library is missing. Re-install the version so all jars are downloaded.";
        }
        if (msg.contains("UnsatisfiedLinkError") || msg.contains("no lwjgl") || msg.contains("Failed to locate library")) {
            return "Native libraries failed to load. Re-install the version to re-extract them.";
        }
        if (cls.contains("OutOfMemoryError")) {
            return "Not enough memory. Increase the allocated heap in ECL settings.";
        }
        if (msg.contains("Pixel format not accelerated") || msg.contains("GLFW")) {
            return "The graphics driver was rejected. Update your GPU driver.";
        }
        return null;
    }
}
