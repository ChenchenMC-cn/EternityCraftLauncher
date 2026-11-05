// SPDX-License-Identifier: MPL-2.0
// Copyright (c) 2026 ChenchenMC <https://github.com/ChenchenMC-cn/EternityCraftLauncher>
using System;
using System.IO;
using System.IO.Compression;
using System.Text;
using System.Text.RegularExpressions;
using ECL.Models;

namespace ECL.Services;

/// <summary>
/// One-click evidence pack: logs, crash files, settings and environment info in a single
/// zip. Built because a remote report of "崩了" is otherwise unanswerable -- the tester
/// cannot be talked through collecting five scattered files.
///
/// <para>Everything is redacted on the way in: tokens never leave the machine.</para>
/// </summary>
public static class DiagnosticBundle
{
    private static readonly Regex Secrets = new(
        @"(--accessToken\s+\S+)|(token:\S+)|(Bearer\s+\S+)|(""[A-Za-z]*(?:[Tt]oken|[Ss]ecret)[A-Za-z]*""\s*:\s*""[^""]+"")",
        RegexOptions.Compiled);

    public static string Create(GamePaths paths, LauncherSettings settings, string destinationDir)
    {
        Directory.CreateDirectory(destinationDir);

        string stamp = DateTime.Now.ToString("yyyyMMdd-HHmmss");
        string zipPath = Path.Combine(destinationDir, $"ECL-diagnostic-{stamp}.zip");

        if (File.Exists(zipPath)) File.Delete(zipPath);

        using var zip = ZipFile.Open(zipPath, ZipArchiveMode.Create);
        var used = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        AddText(zip, "system.txt", Describe(paths, settings));

        // Only log-shaped files, never a recursive walk of a directory that may BE the
        // data root (portable installs keep data next to the exe -- the first version of
        // this swept in 228 MB of assets and somebody's accounts.json).
        long budget = 24L * 1024 * 1024;

        foreach (var (dir, pattern, prefix) in new[]
        {
            (paths.LogsDir, "*.log", "logs/"),
            (ErrorReporter.CrashDir, "*.txt", "crash/")
        })
        {
            if (!Directory.Exists(dir)) continue;

            foreach (string file in Directory.EnumerateFiles(dir, pattern, SearchOption.TopDirectoryOnly))
            {
                if (budget <= 0) break;

                var info = new FileInfo(file);
                if (info.Length == 0 || info.Length > 8 * 1024 * 1024) continue;
                if (info.Name.Equals("accounts.json", StringComparison.OrdinalIgnoreCase)) continue;

                string entryName = prefix + info.Name;
                if (!used.Add(entryName)) continue;

                try
                {
                    string text = Secrets.Replace(File.ReadAllText(file), m => Redact(m.Value));
                    AddText(zip, entryName, text);
                    budget -= info.Length;
                }
                catch
                {
                    // A file the game still holds open is not worth failing the bundle.
                }
            }
        }

        // The non-fatal error log lives next to the exe, as one JSON line per incident.
        try
        {
            if (File.Exists(ErrorReporter.ErrorsFile) && new FileInfo(ErrorReporter.ErrorsFile).Length < budget)
                AddText(zip, "errors.jsonl",
                    Secrets.Replace(File.ReadAllText(ErrorReporter.ErrorsFile), m => Redact(m.Value)));
        }
        catch { }

        try
        {
            if (File.Exists(InstalledVersionScanner.CatalogPath(paths)))
                AddText(zip, "eclversions.config", File.ReadAllText(InstalledVersionScanner.CatalogPath(paths)));
        }
        catch { }

        // Settings minus anything account-shaped.
        try
        {
            AddText(zip, "settings.json", RedactSettings(File.ReadAllText(paths.SettingsFile)));
        }
        catch { }

        return zipPath;
    }

    private static string Describe(GamePaths paths, LauncherSettings settings)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"version     : {AppInfo.Version}");
        sb.AppendLine($"generated   : {DateTime.Now:O}");
        sb.AppendLine($"os          : {Environment.OSVersion} (64bit={Environment.Is64BitOperatingSystem})");
        sb.AppendLine($"runtime     : {Environment.Version}");
        sb.AppendLine($"cpus        : {Environment.ProcessorCount}");
        sb.AppendLine($"data root   : {paths.Root}");
        sb.AppendLine($"isolate     : {paths.IsolateByVersion}");
        sb.AppendLine($"mirror      : {settings.UseMirror} ({settings.MirrorBase})");
        sb.AppendLine($"safe download: {settings.SafeDownload}");
        sb.AppendLine($"threads     : {settings.DownloadThreads}");
        sb.AppendLine($"memory mb   : {settings.MemoryMb}");
        sb.AppendLine($"theme       : {settings.ThemeFamily}/{settings.ThemeAccent} @ {settings.AcrylicOpacity:P0}");
        sb.AppendLine($"java override: {settings.JavaPath ?? "(auto)"}");
        sb.AppendLine($"native      : {(NativeCore.IsAvailable(out var nv) ? nv : "MISSING")}");
        sb.AppendLine($"data size   : {DirectorySize(paths.Root) / 1024.0 / 1024.0:F0} MB");
        return sb.ToString();
    }

    private static long DirectorySize(string dir)
    {
        try
        {
            long total = 0;
            foreach (string file in Directory.EnumerateFiles(dir, "*", SearchOption.AllDirectories))
            {
                try { total += new FileInfo(file).Length; } catch { }
            }
            return total;
        }
        catch
        {
            return 0;
        }
    }

    private static string Redact(string match) => match.Split(' ')[0] + " <redacted>";

    /// <summary>Keeps the shape of settings.json but drops the client id.</summary>
    private static string RedactSettings(string json) =>
        Regex.Replace(json, @"""msClientId""\s*:\s*""[^""]*""", "\"msClientId\": \"<redacted>\"");

    private static void AddText(ZipArchive zip, string name, string content)
    {
        var entry = zip.CreateEntry(name, CompressionLevel.Optimal);
        using var stream = entry.Open();
        using var writer = new StreamWriter(stream, new UTF8Encoding(false));
        writer.Write(content);
    }
}
