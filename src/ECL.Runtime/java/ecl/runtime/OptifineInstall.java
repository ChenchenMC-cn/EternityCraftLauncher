// SPDX-License-Identifier: MPL-2.0
// Copyright (c) 2026 ChenchenMC <https://github.com/ChenchenMC-cn/EternityCraftLauncher>
package ecl.runtime;

import java.io.File;
import java.lang.reflect.InvocationTargetException;
import java.lang.reflect.Method;

/**
 * Headless driver for the OptiFine installer.
 *
 * <p>OptiFine ships a GUI installer whose {@code main} ignores its arguments and
 * always targets {@code Utils.getWorkingDirectory()}. The class
 * {@code optifine.Installer} does expose a public static
 * {@code doInstall(File minecraftDir)} though, and that method honours the
 * directory it is given. This entry point reflectively calls it so the launcher
 * can install OptiFine without a Swing window.</p>
 *
 * <p>Invoked as:
 * {@code java -cp ECL.Runtime.jar;OptiFine_<ver>.jar ecl.runtime.OptifineInstall <minecraftDir>}</p>
 *
 * <p>The OptiFine jar must be on the classpath (not loaded through a custom
 * class loader) because the installer locates its own archive via the code
 * source of {@code optifine.Installer}; that only works when the jar sits on the
 * application class path.</p>
 */
public final class OptifineInstall {

    private static final String TAG = "[ECL] ";

    private OptifineInstall() {
    }

    public static void main(String[] args) {
        if (args.length < 1) {
            System.err.println(TAG + "usage: OptifineInstall <minecraftDir>");
            System.exit(2);
            return;
        }

        File dir = new File(args[0]);
        if (!dir.isDirectory()) {
            System.err.println(TAG + "not a directory: " + dir.getAbsolutePath());
            System.exit(2);
            return;
        }

        Class<?> installer;
        try {
            installer = Class.forName("optifine.Installer");
        } catch (ClassNotFoundException e) {
            System.err.println(TAG + "optifine.Installer not found - the OptiFine jar is not on the classpath");
            System.exit(3);
            return;
        }

        Method doInstall;
        try {
            doInstall = installer.getMethod("doInstall", File.class);
        } catch (NoSuchMethodException e) {
            System.err.println(TAG + "this OptiFine build has no doInstall(File) - unsupported layout");
            System.exit(3);
            return;
        }

        System.out.println(TAG + "installing OptiFine into " + dir.getAbsolutePath());
        try {
            doInstall.invoke(null, dir);
        } catch (InvocationTargetException e) {
            Throwable cause = e.getCause() == null ? e : e.getCause();
            System.err.println(TAG + "optifine install failed: " + cause.getClass().getName()
                    + (cause.getMessage() == null ? "" : ": " + cause.getMessage()));
            StackTraceElement[] st = cause.getStackTrace();
            for (int i = 0; i < Math.min(st.length, 8); i++) {
                System.err.println(TAG + "    at " + st[i]);
            }
            System.exit(4);
        } catch (Throwable t) {
            System.err.println(TAG + "optifine install failed: " + t);
            System.exit(4);
        }

        System.out.println(TAG + "optifine install finished");
        System.out.flush();
    }
}
