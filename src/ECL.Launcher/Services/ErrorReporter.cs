// SPDX-License-Identifier: MPL-2.0
// Copyright (c) 2026 ChenchenMC <https://github.com/ChenchenMC-cn/EternityCraftLauncher>
using System;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Windows;

namespace ECL.Services;

/// <summary>
/// Collects two kinds of trouble and hands them to the native viewers
/// (<c>eclcr.exe</c> / <c>eclnfer.exe</c>).
///
/// <list type="bullet">
/// <item><b>Crash</b> — the launcher itself threw something unhandled. One file per
/// incident under <c>ECL\crash\</c>, because after the process dies there is nothing
/// left to ask.</item>
/// <item><b>Non-fatal</b> — a download failed, a mod jar would not parse, an action was
/// refused. Appended to <c>ECL\errors.jsonl</c>, one JSON object per line, so the file
/// stays appendable and greppable without a parser.</item>
/// </list>
///
/// <para>Everything here is best effort: a reporter that throws while reporting is
/// worse than no reporter.</para>
/// </summary>
public static class ErrorReporter
{
    /// <summary>Serialises writes: a crash can fire from several threads at once.</summary>
    private static readonly object Gate = new();

    /// <summary>Guards against a crash while writing the crash report looping forever.</summary>
    private static int _inCrashHandler;

    public static string BaseDir => AppContext.BaseDirectory;

    private static string? _reportDir;

    /// <summary>
    /// Where reports are written. Usually next to the exe, but an install under
    /// Program Files cannot be written to -- the crash file then silently vanished,
    /// which is why "崩了但没有报告" happened. Fall back to LocalAppData, then temp.
    /// </summary>
    public static string ReportDir => _reportDir ??= PickReportDir();

    public static string CrashDir => Path.Combine(ReportDir, "crash");
    public static string ErrorsFile => Path.Combine(ReportDir, "errors.jsonl");

    private static string PickReportDir()
    {
        foreach (string candidate in new[]
        {
            BaseDir,
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "EternityCraft"),
            Path.GetTempPath()
        })
        {
            try
            {
                Directory.CreateDirectory(candidate);
                string probe = Path.Combine(candidate, ".write-" + Guid.NewGuid().ToString("N"));
                File.WriteAllText(probe, "x");
                File.Delete(probe);
                return candidate;
            }
            catch
            {
                // try the next one
            }
        }

        return Path.GetTempPath();
    }

    private static string CrashExe => Path.Combine(BaseDir, "eclcr.exe");
    private static string NonFatalExe => Path.Combine(BaseDir, "eclnfer.exe");

    /// <summary>
    /// Writes one crash file and returns its path (empty when even that failed).
    /// </summary>
    public static string WriteCrashFile(Exception? ex, string context)
    {
        // A throw inside the crash handler would recurse into this method.
        if (Interlocked.Exchange(ref _inCrashHandler, 1) == 1) return "";

        try
        {
            Directory.CreateDirectory(CrashDir);

            string stamp = DateTime.Now.ToString("yyyyMMdd-HHmmss");
            string path = Path.Combine(CrashDir, $"crash-{stamp}.txt");

            var sb = new StringBuilder();
            sb.AppendLine("ECL 崩溃报告");
            sb.AppendLine("=============");
            sb.AppendLine($"时间     : {DateTime.Now:yyyy-MM-dd HH:mm:ss}");
            sb.AppendLine($"版本     : {AppInfo.Version} ({AppInfo.VersionNumber})");
            sb.AppendLine($"系统     : {Environment.OSVersion.VersionString} ({Environment.OSVersion.Version.Build})");
            sb.AppendLine($"位数     : {(Environment.Is64BitProcess ? "64-bit" : "32-bit")}");
            sb.AppendLine($"运行时   : .NET {Environment.Version}");
            sb.AppendLine($"程序目录 : {BaseDir}");
            sb.AppendLine($"上下文   : {context}");
            sb.AppendLine();

            if (ex != null)
            {
                sb.AppendLine("[异常类型] " + ex.GetType().FullName);
                sb.AppendLine("[异常消息] " + ex.Message);
                sb.AppendLine();
                sb.AppendLine("[堆栈]");
                sb.AppendLine(ex.ToString());
                sb.AppendLine();

                // Inner exceptions are where the real cause usually hides.
                var inner = ex.InnerException;
                int depth = 1;
                while (inner != null && depth <= 5)
                {
                    sb.AppendLine($"[内部异常 {depth}] {inner.GetType().FullName}: {inner.Message}");
                    inner = inner.InnerException;
                    depth++;
                }
                sb.AppendLine();
            }

            // The startup trace is the only record of what happened before the UI existed.
            // It is written next to the exe; fall back to the report dir.
            string startupLog = Path.Combine(BaseDir, "startup.log");
            if (!File.Exists(startupLog)) startupLog = Path.Combine(ReportDir, "startup.log");
            if (File.Exists(startupLog))
            {
                sb.AppendLine("[启动日志 startup.log]");
                try
                {
                    foreach (string line in File.ReadLines(startupLog))
                        sb.AppendLine("  " + line);
                }
                catch (Exception readEx)
                {
                    sb.AppendLine("  (读取失败：" + readEx.Message + ")");
                }
            }

            File.WriteAllText(path, sb.ToString(), new UTF8Encoding(false));
            return path;
        }
        catch
        {
            return "";
        }
        finally
        {
            Interlocked.Exchange(ref _inCrashHandler, 0);
        }
    }

    /// <summary>
    /// Records a problem that did not stop the launcher. Never throws.
    /// </summary>
    /// <param name="kind">Short tag: download / mod / launch / auth / io …</param>
    public static void ReportNonFatal(string kind, string message, string? detail = null)
    {
        lock (Gate)
        {
            try
            {
                var entry = new
                {
                    time = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"),
                    version = AppInfo.Version,
                    kind,
                    message,
                    detail = detail ?? ""
                };

                File.AppendAllText(ErrorsFile,
                    JsonSerializer.Serialize(entry) + Environment.NewLine,
                    new UTF8Encoding(false));
            }
            catch
            {
                // Losing one error line beats taking the app down.
            }
        }
    }

    /// <summary>Opens eclcr.exe on the newest crash file.</summary>
    public static void ShowCrashReport()
    {
        LaunchTool(CrashExe);
    }

    /// <summary>Opens eclnfer.exe on the accumulated non-fatal log.</summary>
    public static void ShowNonFatalReport()
    {
        LaunchTool(NonFatalExe);
    }

    /// <summary>
    /// True when the viewer exists next to the launcher. The dist/ build ships it;
    /// a bare `dotnet run` does not, and the UI hides the entry point in that case.
    /// </summary>
    public static bool CrashViewerAvailable => File.Exists(CrashExe);
    public static bool NonFatalViewerAvailable => File.Exists(NonFatalExe);

    private static void LaunchTool(string exe)
    {
        try
        {
            if (!File.Exists(exe))
            {
                MessageBox.Show(
                    "报告工具不在启动器目录里（" + Path.GetFileName(exe) + "）。\r\n" +
                    "如果你是用 ECL-Setup.exe 安装的，重装一次即可恢复。",
                    "找不到报告工具", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            // Match the launcher's language; the viewers default to the OS UI
            // language when they are opened directly.
            string lang = Localization.IsEnglish ? "en" : "zh";

            Process.Start(new ProcessStartInfo(exe, $"--dir \"{BaseDir}\" --lang {lang}")
            {
                UseShellExecute = true
            });
        }
        catch (Exception ex)
        {
            MessageBox.Show("打不开报告工具：" + ex.Message, "错误", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }
}
