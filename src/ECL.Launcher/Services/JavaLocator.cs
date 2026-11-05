// SPDX-License-Identifier: MPL-2.0
// Copyright (c) 2026 ChenchenMC <https://github.com/ChenchenMC-cn/EternityCraftLauncher>
using System.Diagnostics;
using System.IO;
using System.Text.Json;
using ECL.Models;

namespace ECL.Services;

public sealed record JavaProbeInfo(
    string Version, int Major, string Vendor, string Home, string VmName, string ClassVersion, long MaxHeapMb, int Bitness);

public sealed record JavaRuntime(string JavaExe, JavaProbeInfo Info)
{
    public string Display => $"Java {Info.Major}  ·  {Info.Vendor}";
    public override string ToString() => $"{JavaExe}  (Java {Info.Major})";
}

/// <summary>
/// Finds installed JVMs and asks each one for its capabilities by running
/// <c>ecl.runtime.Probe</c> from ECL.Runtime.jar. Falls back to parsing
/// <c>java -version</c> when the jar is not deployed.
/// </summary>
public sealed class JavaLocator
{
    private static readonly string[] RegistryHives =
    {
        @"SOFTWARE\JavaSoft\JDK", @"SOFTWARE\JavaSoft\JRE",
        @"SOFTWARE\JavaSoft\Java Development Kit", @"SOFTWARE\JavaSoft\Java Runtime Environment",
        @"SOFTWARE\WOW6432Node\JavaSoft\JDK", @"SOFTWARE\WOW6432Node\JavaSoft\JRE",
        @"SOFTWARE\WOW6432Node\JavaSoft\Java Development Kit",
        @"SOFTWARE\WOW6432Node\JavaSoft\Java Runtime Environment"
    };

    private readonly LauncherSettings _settings;
    private readonly string _probeJar;
    private readonly List<JavaRuntime> _cache = new();

    public JavaLocator(LauncherSettings settings)
    {
        _settings = settings;
        _probeJar = Path.Combine(AppContext.BaseDirectory, "ECL.Runtime.jar");
    }

    public IReadOnlyList<JavaRuntime> Known => _cache;

    public async Task<IReadOnlyList<JavaRuntime>> DiscoverAsync(IProgress<string>? log, CancellationToken ct)
    {
        _cache.Clear();
        var candidates = new List<string>();

        void Add(string? exe)
        {
            if (string.IsNullOrWhiteSpace(exe)) return;
            try
            {
                var full = Path.GetFullPath(exe);
                if (File.Exists(full)) candidates.Add(full);
            }
            catch
            {
                // malformed path, ignore
            }
        }

        if (!string.IsNullOrWhiteSpace(_settings.JavaPath))
            Add(_settings.JavaPath);

        Add(FindOnPath("java.exe"));

        string? javaHome = Environment.GetEnvironmentVariable("JAVA_HOME");
        if (!string.IsNullOrWhiteSpace(javaHome)) Add(Path.Combine(javaHome, "bin", "java.exe"));

        // Every JVM that ran an installer is registered under JavaSoft -- including the
        // 32-bit ones (WOW6432Node) and the ones a user dropped into D://Java, which no
        // directory scan would ever reach. "缺少 java8" usually ended here.
        int fromRegistry = 0;
        foreach (string hive in RegistryHives)
        {
            try
            {
                using var key = Microsoft.Win32.Registry.LocalMachine.OpenSubKey(hive);
                if (key == null) continue;

                foreach (string name in key.GetSubKeyNames())
                {
                    using var sub = key.OpenSubKey(name);
                    if (sub?.GetValue("JavaHome") is string home && home.Length > 0)
                    {
                        string exe = Path.Combine(home.TrimEnd('\\'), "bin", "java.exe");
                        if (File.Exists(exe)) fromRegistry++;
                        Add(exe);
                    }
                }
            }
            catch
            {
                // A locked or missing hive must not stop the sweep.
            }
        }

        // Oracle's launcher stub, i.e. what `java` in cmd resolves to on a stock JRE 8.
        Add(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonProgramFiles),
            "Oracle", "Java", "javapath", "java.exe"));

        // Bundled runtime shipped with the launcher data folder.
        foreach (var root in new[] { _settings.DataRoot })
        {
            var runtimeRoot = Path.Combine(root, "runtime");
            if (Directory.Exists(runtimeRoot))
            {
                foreach (var dir in Directory.EnumerateDirectories(runtimeRoot))
                {
                    Add(Path.Combine(dir, "bin", "java.exe"));
                    foreach (var sub in Directory.EnumerateDirectories(dir))
                        Add(Path.Combine(sub, "bin", "java.exe"));
                }
            }
        }

        string[] vendors =
        {
            @"C:\Program Files\Java", @"C:\Program Files\Eclipse Adoptium",
            @"C:\Program Files\Microsoft", @"C:\Program Files\Zulu",
            @"C:\Program Files\BellSoft", @"C:\Program Files\Amazon Corretto",
            @"C:\Program Files\Semeru", @"C:\Program Files\RedHat",
            @"C:\Program Files (x86)\Java", @"C:\Program Files (x86)\Eclipse Adoptium",
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Programs"),
            @"C:\Program Files\JetBrains",
            Path.Combine(_settings.DataRoot, ".jdk")
        };

        foreach (var vendor in vendors)
        {
            if (!Directory.Exists(vendor)) continue;
            foreach (var dir in SafeEnumerate(vendor))
            {
                Add(Path.Combine(dir, "bin", "java.exe"));
                Add(Path.Combine(dir, "jre", "bin", "java.exe"));
                foreach (var sub in SafeEnumerate(dir))
                {
                    Add(Path.Combine(sub, "bin", "java.exe"));
                    Add(Path.Combine(sub, "jre", "bin", "java.exe"));
                }
            }
        }

        // Support breadcrumb: "缺少 java8" reports need to know what was searched.
        log?.Report($"Java 候选 {candidates.Distinct(StringComparer.OrdinalIgnoreCase).Count()} 个" +
                    $"（注册表贡献 {fromRegistry} 个，目录扫描 {vendors.Length} 处）");

        using var budget = CancellationTokenSource.CreateLinkedTokenSource(ct);
        budget.CancelAfter(TimeSpan.FromSeconds(20));

        int i = 0;
        foreach (var exe in candidates.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            // User cancellation must still surface; only the internal budget breaks
            // out quietly.
            ct.ThrowIfCancellationRequested();

            if (budget.IsCancellationRequested)
            {
                log?.Report("  探测超时（20 秒），使用已找到的运行时");
                break;
            }

            log?.Report($"[{++i}] Probing {exe}");
            var info = await ProbeAsync(exe, budget.Token).ConfigureAwait(false);
            if (info != null)
            {
                _cache.Add(new JavaRuntime(exe, info));
                log?.Report($"    Java {info.Major} · {info.Vendor} · {info.Bitness}-bit · max heap {info.MaxHeapMb} MB");
            }
            else
            {
                log?.Report("    not usable, skipped");
            }
        }

        return _cache
            .OrderByDescending(r => r.Info.Major)
            .ThenBy(r => r.JavaExe, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private static IEnumerable<string> SafeEnumerate(string dir)
    {
        try { return Directory.EnumerateDirectories(dir); }
        catch { return Array.Empty<string>(); }
    }

    private static string? FindOnPath(string exe)
    {
        var path = Environment.GetEnvironmentVariable("PATH");
        if (string.IsNullOrEmpty(path)) return null;
        foreach (var part in path.Split(Path.PathSeparator))
        {
            if (string.IsNullOrWhiteSpace(part)) continue;
            try
            {
                var full = Path.Combine(part.Trim('"'), exe);
                if (File.Exists(full)) return full;
            }
            catch
            {
                // ignore invalid PATH segments
            }
        }
        return null;
    }

    /// <summary>Runs the probe; null means the binary is not a working JVM.</summary>
    public async Task<JavaProbeInfo?> ProbeAsync(string javaExe, CancellationToken ct)
    {
        if (File.Exists(_probeJar))
        {
            var json = await RunCaptureAsync(javaExe,
                $"-cp \"{_probeJar}\" ecl.runtime.Probe", ct).ConfigureAwait(false);
            if (!string.IsNullOrWhiteSpace(json))
            {
                try
                {
                    var doc = JsonDocument.Parse(json.Trim());
                    var r = doc.RootElement;
                    return new JavaProbeInfo(
                        GetStr(r, "version"), GetInt(r, "major"), GetStr(r, "vendor"),
                        GetStr(r, "home"), GetStr(r, "vmName"), GetStr(r, "classVersion"),
                        GetLong(r, "maxHeapMB"), GetInt(r, "bitness"));
                }
                catch (JsonException)
                {
                    // fall through to -version parsing
                }
            }
        }

        return await ProbeByVersionFlagAsync(javaExe, ct).ConfigureAwait(false);
    }

    private static async Task<JavaProbeInfo?> ProbeByVersionFlagAsync(string javaExe, CancellationToken ct)
    {
        var text = await RunCaptureAsync(javaExe, "-version", ct, captureStderr: true).ConfigureAwait(false);
        if (string.IsNullOrWhiteSpace(text)) return null;

        // openjdk version "17.0.19" 2026-04-21
        var m = System.Text.RegularExpressions.Regex.Match(text, "\"([0-9][^\"]*)\"");
        if (!m.Success) return null;
        string ver = m.Groups[1].Value;
        int major = ver.StartsWith("1.") ? int.Parse(ver.Substring(2, 1)) : int.Parse(new string(ver.TakeWhile(char.IsDigit).ToArray()));
        bool is64 = text.Contains("64-Bit", StringComparison.OrdinalIgnoreCase);

        string vendor = "";
        foreach (var known in new[] { "Temurin", "Adoptium", "Microsoft", "Zulu", "Corretto", "GraalVM", "OpenJDK", "Oracle" })
            if (text.Contains(known, StringComparison.OrdinalIgnoreCase)) { vendor = known; break; }

        return new JavaProbeInfo(ver, major, vendor, "", "", "", 0, is64 ? 64 : 32);
    }

    private static async Task<string?> RunCaptureAsync(string exe, string args, CancellationToken ct, bool captureStderr = false)
    {
        try
        {
            var psi = new ProcessStartInfo(exe, args)
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };
            using var p = Process.Start(psi);
            if (p == null) return null;

            var stdout = p.StandardOutput.ReadToEndAsync(ct);
            var stderr = p.StandardError.ReadToEndAsync(ct);
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            // A healthy JVM answers in ~0.5 s; anything past 8 s is a broken install.
            timeout.CancelAfter(TimeSpan.FromSeconds(8));
            try
            {
                await p.WaitForExitAsync(timeout.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                try { p.Kill(entireProcessTree: true); } catch { }
                return null;
            }

            string o = await stdout.ConfigureAwait(false);
            string e = await stderr.ConfigureAwait(false);
            return captureStderr ? (o + "\n" + e) : (string.IsNullOrWhiteSpace(o) ? e : o);
        }
        catch
        {
            return null;
        }
    }

    private static string GetStr(JsonElement e, string name) =>
        e.TryGetProperty(name, out var v) ? v.GetString() ?? "" : "";

    private static int GetInt(JsonElement e, string name) =>
        e.TryGetProperty(name, out var v) && v.TryGetInt32(out int i) ? i : 0;

    private static long GetLong(JsonElement e, string name) =>
        e.TryGetProperty(name, out var v) && v.TryGetInt64(out long i) ? i : 0;

    /// <summary>
    /// Picks the smallest runtime that satisfies the version requirement. Old
    /// Minecraft (Java 8 requirement) breaks on Java 9+, so for those the answer
    /// must be exactly 8 — never a newer JVM, and never a fallback.
    /// </summary>
    public static JavaRuntime? Choose(IReadOnlyList<JavaRuntime> available, int requiredMajor)
    {
        var fitting = requiredMajor <= 8
            ? available.Where(j => j.Info.Major == 8)
            : available.Where(j => j.Info.Major >= requiredMajor);

        var best = fitting.OrderBy(j => j.Info.Major)
                          .ThenByDescending(j => j.Info.Bitness)
                          .FirstOrDefault();
        if (best != null) return best;

        // Fallback to the newest JVM only for modern targets; old versions must
        // not silently run on Java 9+ (they crash in confusing ways).
        return requiredMajor <= 8 ? null : available.OrderByDescending(j => j.Info.Major).FirstOrDefault();
    }
}
