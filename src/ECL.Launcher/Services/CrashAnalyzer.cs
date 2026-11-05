// SPDX-License-Identifier: MPL-2.0
// Copyright (c) 2026 ChenchenMC <https://github.com/ChenchenMC-cn/EternityCraftLauncher>
using System;
using System.Collections.Generic;
using System.Linq;

namespace ECL.Services;

/// <summary>
/// Turns a nonzero game exit into something a player can act on.
///
/// <para>Minecraft fails in a handful of recognisable ways, but the log says it in
/// English and buries it under a thousand lines of mod chatter. This matches the tail
/// of the log plus the exit code against known patterns and produces a Chinese
/// explanation with a concrete next step.</para>
///
/// <para>Deliberately conservative: when nothing matches, it says so instead of
/// inventing a cause. A wrong-but-confident diagnosis wastes more time than
/// "看不懂，把日志发出来".</para>
/// </summary>
public static class CrashAnalyzer
{
    public sealed record Diagnosis(string Title, string Summary, string Advice, bool Recognised)
    {
        public string ToDisplayString() =>
            Recognised
                ? $"检测到崩溃原因：{Title}\n{Summary}\n建议：{Advice}"
                : "游戏异常退出，但没有匹配到已知的崩溃特征。请查看日志末尾，或把日志发出来。";
    }

    private sealed record Rule(
        string[] Needles,
        string Title,
        string Summary,
        string Advice,
        bool CaseSensitive = false);

    // Ordered by specificity: the first match wins, so the narrow patterns sit above
    // the generic ones. A crash log usually contains several of these strings.
    private static readonly Rule[] Rules =
    {
        new(new[] { "java.lang.OutOfMemoryError", "Java heap space", "GC overhead limit exceeded" },
            "内存不足",
            "游戏申请的内存超过了启动时分配的堆上限。",
            "到「设置」把内存调到 4096 MB 以上。1.20 整合包常见需要 6–8 GB；" +
            "如果已经给到 8 GB 还这样，多半是某个模组在漏内存。"),

        new(new[] { "Pixel format not accelerated", "LWJGLException: Pixel format not accelerated",
                    "Failed to create window", "GLFW error 65542" },
            "显卡驱动不支持 OpenGL",
            "显卡驱动太旧，或者用了没有 OpenGL 支持的远程桌面 / 虚拟机。",
            "更新显卡驱动（核显也要更新）。远程桌面和部分虚拟机环境跑不了 Minecraft，换本机试试。"),

        new(new[] { "UnsupportedClassVersionError" },
            "Java 版本不匹配",
            "当前 Java 的版本和这个 Minecraft / 模组加载器要求的不一致。",
            "1.20.1 需要 Java 17，1.16 及更早需要 Java 8。到「设置」手动指定对应的 Java。"),

        new(new[] { "Mixin apply failed", "MixinApplyError", "mixin.MixinTransformerError",
                    "Critical injection failure" },
            "模组 Mixin 冲突",
            "两个或多个模组改写了同一个游戏类，改动撞在一起了。",
            "先只留崩溃里提到的那几个模组，确认能启动，再逐个加回来找冲突的那一对。"),

        new(new[] { "DuplicateModsFoundException", "duplicate mods", "Found a duplicate mod" },
            "有重复安装的模组",
            "同一个模组装了两份（通常是版本不同，或者同一 jar 放了两次）。",
            "到「模组」页按名字排序，删掉重复的那份；也看看 mods 目录里有没有 xxx (1).jar 这种。"),

        new(new[] { "Missing or unsupported mandatory dependencies", "Mod ID:", "requires",
                    "MissingDependencyException" },
            "缺少前置模组",
            "某个模组依赖的另一个模组没装，或者版本不对。",
            "崩溃日志里会写「requires xxx」，把那个模组装上（注意版本要求）。"),

        new(new[] { "NoSuchMethodError", "NoClassDefFoundError", "ClassNotFoundException",
                    "AbstractMethodError", "IncompatibleClassChangeError" },
            "模组或库版本不兼容",
            "游戏在找一个不存在的类或方法——通常是用错了模组版本，或该模组不是给这个游戏版本做的。",
            "确认模组支持当前 Minecraft 版本；Forge 与 Fabric 的 jar 不能混用。"),

        new(new[] { "EXCEPTION_ACCESS_VIOLATION", "SIGSEGV", "Problematic frame" },
            "游戏进程崩溃（访问冲突）",
            "JVM 层面崩了，不是普通 Java 异常。常见原因是显卡驱动、优化模组（OptiFine / Sodium）" +
            "与其它模组冲突，或 32 位 Java 内存溢出。",
            "先试着去掉 OptiFine / 优化类模组。确认用的是 64 位 Java（设置页能看到位数）。"),

        new(new[] { "UnknownHostException", "Connection refused", "SocketTimeoutException",
                    "Failed to download", "Unable to connect", "Read timed out" },
            "网络问题",
            "连不上验证服务器或资源站。可能是网络不通、代理设置不对，或者镜像挂了。",
            "到「设置」换个下载源，或关掉系统代理再试。正版登录还需要能访问 Microsoft 的服务器。"),

        new(new[] { "Could not find or load main class", "ClassNotFoundException: net.minecraft",
                    "Unsupported major.minor version" },
            "启动参数或类路径有问题",
            "启动器给游戏准备的运行文件不全，或者某个 jar 被删掉了。",
            "主页点「补全文件」，让它把缺的库下回来。"),

        new(new[] { "is not a valid mod file", "Invalid mod file", "zip END header not found",
                    "java.util.zip.ZipException" },
            "模组文件损坏",
            "某个 .jar 不是完整的压缩包——下载中断，或者根本不是模组文件。",
            "把最近加进去的模组重新下一次。文件名正常但大小明显偏小（几 KB）的那种最可疑。"),

        new(new[] { "AnvilChunkLoader", "loadChunk__Async", "NBTTagCompound.func_152449_a",
                    "CompressedStreamTools.func_74794_a" },
            "存档区块损坏",
            "读世界区块的时候崩了：saves 里那个世界的 .mca 区块文件已经坏了" +
            "（断电、磁盘错误，或者被别的游戏版本 / 模组写过）。这和启动器、和模组都没关系。",
            "① 先新建一个世界，能进就说明是老存档的问题；② 备份 saves/<世界名> 后，" +
            "删掉 region 里最近修改的那几个 .mca 再试；③ 有备份就直接还原。" +
            "另外：关掉版本隔离时所有版本共用 saves，被高版本（1.13+）打开过的存档" +
            "再回到 1.8.9 会读不了，这是最常见的原因之一。"),

        new(new[] { "The game crashed whilst", "Crash report saved to" },
            "游戏崩溃（有崩溃报告）",
            "Minecraft 自己写了崩溃报告，里面有具体堆栈。",
            "打开 crash-reports 里最新的那个文件，最上面几行写着直接原因。" +
            "（开了版本隔离在 versions/<版本>/game/crash-reports/，关着就在数据根目录）"),

        new(new[] { "Address already in use", "Failed to bind to port" },
            "端口被占用",
            "上一次的游戏进程没完全退出，还占着端口 / 文件锁。",
            "任务管理器里结束残留的 javaw.exe，再启动一次。"),
    };

    // Exit codes that carry meaning on their own when the log has nothing to say.
    private static readonly Dictionary<int, Diagnosis> ExitCodes = new()
    {
        [-1073741819] = new("游戏进程崩溃（0xC0000005 访问冲突）",
            "程序访问了不属于它的内存。多数是显卡驱动或原生模组（OptiFine、光影、优化模组）导致。",
            "先移除 OptiFine / 光影 / 优化类模组再启动；确认 Java 是 64 位。", true),

        [-1073740791] = new("游戏进程崩溃（0xC0000409 栈溢出）",
            "调用栈被打爆了，通常是模组里递归调用出错，或 JVM 参数里的 -Xss 设得太小。",
            "把设置里自定义 JVM 参数的 -Xss 去掉，或改成 -Xss2M。", true),

        [-1073741571] = new("游戏进程崩溃（0xC00000FD 栈溢出）",
            "同上，调用栈溢出。", "去掉 -Xss 之类的自定义参数，或把它调大。", true),

        [-1] = new("游戏异常退出",
            "退出码 -1，通常是被外部进程结束，或者 JVM 初始化失败。",
            "确认没有杀毒软件拦截 java.exe；重新点一次启动。", true),

        [1] = new("游戏异常退出（退出码 1）",
            "Java 侧抛了未捕获的异常就退出了，具体原因在日志里。",
            "看日志最后 30 行；如果只有一句 \"Stopping!\"，多半是主菜单之前就崩了。", true),
    };

    /// <summary>
    /// Analyses a finished game session.
    /// </summary>
    /// <param name="exitCode">Process exit code.</param>
    /// <param name="logTail">Last N lines of the game log, oldest first. 200 is plenty.</param>
    public static Diagnosis Analyze(int exitCode, IReadOnlyList<string> logTail)
    {
        // Log evidence beats the exit code: 0xC0000005 with "OutOfMemoryError" above it
        // is a memory problem, not a driver problem.
        foreach (var rule in Rules)
        {
            foreach (string line in logTail)
            {
                if (Contains(line, rule.Needles, rule.CaseSensitive))
                    return new Diagnosis(rule.Title, rule.Summary, rule.Advice, true);
            }
        }

        if (ExitCodes.TryGetValue(exitCode, out var byCode))
            return byCode;

        return new Diagnosis("未知原因", "没有匹配到已知特征。", "查看日志末尾，或把日志发出来。", false);
    }

    private static bool Contains(string line, string[] needles, bool caseSensitive)
    {
        foreach (string needle in needles)
        {
            if (line.Contains(needle, caseSensitive
                    ? StringComparison.Ordinal
                    : StringComparison.OrdinalIgnoreCase))
                return true;
        }
        return false;
    }
}
