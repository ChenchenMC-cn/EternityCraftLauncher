// SPDX-License-Identifier: MPL-2.0
// Copyright (c) 2026 ChenchenMC <https://github.com/ChenchenMC-cn/EternityCraftLauncher>
using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Text;
using System.Text.RegularExpressions;

namespace ECL.Services;

public sealed record OptiFineBuild(string FileName, string MinecraftVersion, string Edition)
{
    public string Display => $"{Edition}  ({FileName})";
}

/// <summary>
/// Installs OptiFine as its own version.
///
/// <para>optifine.net has no API: the download links live behind an ad interstitial
/// that only hands out a tokenised URL to a request carrying the right Referer.
/// The flow is downloads page → adloadx → downloadx, and the resulting jar is a
/// GUI installer whose {@code main} ignores arguments. ECL therefore drives the
/// installer's {@code doInstall(File)} through
/// {@code ecl.runtime.OptifineInstall} (see that class for why the jar must be on
/// the class path rather than a custom loader).</para>
/// </summary>
public sealed class OptiFineInstaller
{
    private const string Host = "https://optifine.net";
    private const string DownloadsPage = Host + "/downloads";
    // The site rejects the default .NET agent; a browser string is required.
    private const string UserAgent =
        "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/122.0 Safari/537.36";

    private static readonly Regex AdLinkRx = new(
        @"adloadx\?f=(?<file>OptiFine_[^""'&\s>]+\.jar)(?:&x=(?<tok>[0-9a-fA-F]+))?",
        RegexOptions.Compiled);

    private static readonly Regex DownloadLinkRx = new(
        @"downloadx\?f=[^""'&\s>]+&x=[0-9a-fA-F]+",
        RegexOptions.Compiled);

    private static readonly Regex JarNameRx = new(
        @"^OptiFine_(?<mc>[0-9][0-9.]*)_(?<ed>.+)\.jar$",
        RegexOptions.Compiled);

    private readonly HttpClient _http;
    private readonly GamePaths _paths;
    private readonly string _runtimeJar;

    public OptiFineInstaller(GamePaths paths, string? runtimeJar = null)
    {
        _paths = paths;
        _runtimeJar = runtimeJar ?? Path.Combine(AppContext.BaseDirectory, "ECL.Runtime.jar");
        _http = new HttpClient(new HttpClientHandler { AllowAutoRedirect = true })
        {
            Timeout = TimeSpan.FromSeconds(60)
        };
        _http.DefaultRequestHeaders.UserAgent.ParseAdd(UserAgent);
    }

    // ----------------------------------------------------------- discovery ---

    public async Task<List<OptiFineBuild>> GetBuildsAsync(string mcVersion, CancellationToken ct)
    {
        string html = await _http.GetStringAsync(DownloadsPage, ct).ConfigureAwait(false);
        return ParseBuilds(html, mcVersion);
    }

    public static List<OptiFineBuild> ParseBuilds(string html, string mcVersion)
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var result = new List<OptiFineBuild>();

        foreach (Match m in AdLinkRx.Matches(html))
        {
            string file = m.Groups["file"].Value;
            if (!seen.Add(file)) continue;

            var name = JarNameRx.Match(file);
            if (!name.Success) continue;

            string mc = name.Groups["mc"].Value;
            if (!string.Equals(mc, mcVersion, StringComparison.OrdinalIgnoreCase)) continue;

            result.Add(new OptiFineBuild(file, mc, name.Groups["ed"].Value));
        }

        // Newest build first: I6 before I5, pre5 before pre4.
        result.Sort((a, b) => CompareEdition(b.Edition, a.Edition));
        return result;
    }

    /// <summary>Compares "HD_U_I6" vs "HD_U_I5" numerically on the trailing segment.</summary>
    private static int CompareEdition(string a, string b)
    {
        static (string head, int num, int pre) Split(string s)
        {
            var match = Regex.Match(s, @"^(?<head>.*?)(?<num>\d+)(?:_pre(?<pre>\d+))?$");
            if (!match.Success) return (s, 0, 0);
            return (match.Groups["head"].Value,
                int.TryParse(match.Groups["num"].Value, out int n) ? n : 0,
                int.TryParse(match.Groups["pre"].Value, out int p) ? p : 0);
        }

        var (h1, n1, p1) = Split(a);
        var (h2, n2, p2) = Split(b);
        int c = string.CompareOrdinal(h1, h2);
        if (c != 0) return c;
        c = n1.CompareTo(n2);
        return c != 0 ? c : p1.CompareTo(p2);
    }

    // ------------------------------------------------------------ download ---

    private async Task<string> DownloadAsync(string fileName, IProgress<string>? log, CancellationToken ct)
    {
        string cacheDir = Path.Combine(_paths.Root, "cache", "installers");
        Directory.CreateDirectory(cacheDir);
        string dest = Path.Combine(cacheDir, fileName);

        if (File.Exists(dest) && new FileInfo(dest).Length > 1_000_000 && IsZip(dest))
        {
            log?.Report($"复用已下载的 {fileName}");
            return dest;
        }

        log?.Report($"解析 OptiFine 下载页：{fileName}");
        string downloadsHtml = await _http.GetStringAsync(DownloadsPage, ct).ConfigureAwait(false);

        // The token we saw on the listing page is a short session marker; the ad
        // page hands out the real one. Try the plain link first, then with token.
        string? adUrl = null;
        foreach (Match m in AdLinkRx.Matches(downloadsHtml))
        {
            if (!string.Equals(m.Groups["file"].Value, fileName, StringComparison.OrdinalIgnoreCase)) continue;
            string tok = m.Groups["tok"].Value;
            adUrl = tok.Length > 0
                ? $"{Host}/adloadx?f={fileName}&x={tok}"
                : $"{Host}/adloadx?f={fileName}";
            break;
        }

        if (adUrl == null)
            throw new InvalidOperationException($"下载页上找不到 {fileName}（OptiFine 可能已下架该版本）。");

        log?.Report("打开 OptiFine 广告页取下载令牌…");
        using (var adReq = new HttpRequestMessage(HttpMethod.Get, adUrl))
        {
            adReq.Headers.Referrer = new Uri(DownloadsPage);
            using var adResp = await _http.SendAsync(adReq, ct).ConfigureAwait(false);
            adResp.EnsureSuccessStatusCode();
            string adHtml = await adResp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);

            var dl = DownloadLinkRx.Match(adHtml);
            if (!dl.Success)
                throw new InvalidOperationException("广告页没有返回下载链接（页面结构可能变了）。");

            string dlUrl = $"{Host}/{dl.Value}";
            log?.Report("下载 OptiFine 安装包…");

            using var jarReq = new HttpRequestMessage(HttpMethod.Get, dlUrl);
            jarReq.Headers.Referrer = new Uri(adUrl);
            using var jarResp = await _http.SendAsync(jarReq, HttpCompletionOption.ResponseHeadersRead, ct)
                .ConfigureAwait(false);
            jarResp.EnsureSuccessStatusCode();

            await using (var fs = File.Create(dest))
                await jarResp.Content.CopyToAsync(fs, ct).ConfigureAwait(false);
        }

        if (!IsZip(dest))
        {
            try { File.Delete(dest); } catch { }
            throw new InvalidOperationException("下载到的不是有效的 jar（可能被广告页拦截）。");
        }

        log?.Report($"已下载 {fileName}（{new FileInfo(dest).Length / 1024 / 1024.0:F1} MB）");
        return dest;
    }

    private static bool IsZip(string path)
    {
        try
        {
            using var fs = File.OpenRead(path);
            int a = fs.ReadByte(), b = fs.ReadByte();
            return a == 'P' && b == 'K';
        }
        catch
        {
            return false;
        }
    }

    // ------------------------------------------------------------- install ---

    public async Task<string> InstallAsync(string mcVersion, string fileName, string javaExe,
        IProgress<string>? log, CancellationToken ct)
    {
        if (!File.Exists(_runtimeJar))
            throw new InvalidOperationException("找不到 ECL.Runtime.jar，无法驱动 OptiFine 安装器。");

        string jarPath = await DownloadAsync(fileName, log, ct).ConfigureAwait(false);

        // OptiFine updates launcher_profiles.json; the installer fails without it.
        string profiles = Path.Combine(_paths.Root, "launcher_profiles.json");
        if (!File.Exists(profiles))
        {
            File.WriteAllText(profiles, """
                {
                  "profiles": {},
                  "settings": { "enableSnapshots": false, "keepLauncherOpen": false },
                  "version": 3
                }
                """);
        }

        var before = VersionDirs();
        log?.Report("运行 OptiFine 安装器（headless）…");

        var (ok, output) = await RunInstallerAsync(javaExe, jarPath, _paths.Root, log, ct).ConfigureAwait(false);
        var after = VersionDirs();

        var created = after.Except(before, StringComparer.OrdinalIgnoreCase)
                           .Where(d => d.Contains("OptiFine", StringComparison.OrdinalIgnoreCase))
                           .ToList();

        if (created.Count == 0)
        {
            string tail = output.Length > 1200 ? output[^1200..] : output;
            throw new InvalidOperationException(
                "OptiFine 安装器没有生成版本目录。" + (ok ? "" : "（退出码非 0）") + "\n" + tail);
        }

        string id = created[0];
        log?.Report($"OptiFine 安装完成：{id}");
        return id;
    }

    // ---------------------------------------------------- as a Forge mod ---

    /// <summary>
    /// Drops OptiFine into a Forge profile's <c>mods/</c> folder instead of
    /// creating a standalone version. The downloaded jar is already a valid Forge
    /// mod (it carries <c>META-INF/mods.toml</c> with <c>modId="optifine"</c> and a
    /// ModLauncher transformation service), so no patching is involved.
    /// </summary>
    public async Task<string> InstallAsForgeModAsync(string mcVersion, string fileName, string forgeVersionId,
        IProgress<string>? log, CancellationToken ct)
    {
        string profileJson = _paths.VersionJson(forgeVersionId);
        if (!File.Exists(profileJson))
            throw new InvalidOperationException(
                $"先安装 Forge（{forgeVersionId}），之后才能把 OptiFine 当模组装进去。");

        string jarPath = await DownloadAsync(fileName, log, ct).ConfigureAwait(false);

        if (!HasForgeModMetadata(jarPath))
            throw new InvalidOperationException(
                $"{fileName} 里没有 META-INF/mods.toml，不能作为 Forge 模组装载。");

        string modsDir = _paths.ModsDir(forgeVersionId);
        Directory.CreateDirectory(modsDir);

        // Replace any previous OptiFine build in this profile (a disabled one
        // included) so two versions of the same mod cannot collide.
        foreach (var stale in Directory.EnumerateFiles(modsDir, "OptiFine_*", SearchOption.TopDirectoryOnly))
        {
            if (!stale.EndsWith(".jar", StringComparison.OrdinalIgnoreCase) &&
                !stale.EndsWith(".jar.disabled", StringComparison.OrdinalIgnoreCase)) continue;
            log?.Report($"移除旧的 {Path.GetFileName(stale)}");
            try { File.Delete(stale); } catch { }
        }

        string dest = Path.Combine(modsDir, fileName);
        File.Copy(jarPath, dest, overwrite: true);
        log?.Report($"OptiFine 已作为模组放入 {dest}");
        return dest;
    }

    /// <summary>True when the archive is a Forge mod (1.13+ mods.toml layout).</summary>
    public static bool HasForgeModMetadata(string jarPath)
    {
        try
        {
            using var zip = System.IO.Compression.ZipFile.OpenRead(jarPath);
            foreach (var e in zip.Entries)
            {
                if (e.FullName.Equals("META-INF/mods.toml", StringComparison.OrdinalIgnoreCase) ||
                    e.FullName.Equals("META-INF/neoforge.mods.toml", StringComparison.OrdinalIgnoreCase))
                    return true;
            }
            return false;
        }
        catch
        {
            return false;
        }
    }

    private HashSet<string> VersionDirs()
    {
        var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (!Directory.Exists(_paths.VersionsDir)) return set;
        foreach (var d in Directory.EnumerateDirectories(_paths.VersionsDir))
            set.Add(Path.GetFileName(d));
        return set;
    }

    private static async Task<(bool Ok, string Output)> RunInstallerAsync(string javaExe, string optifineJar,
        string dataRoot, IProgress<string>? log, CancellationToken ct)
    {
        var psi = new ProcessStartInfo(javaExe)
        {
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
            WorkingDirectory = dataRoot
        };
        // OptiFine's classes must live on the application class path so the
        // installer can resolve its own archive. ECL.Runtime.jar supplies the entry.
        psi.ArgumentList.Add("-cp");
        psi.ArgumentList.Add(Path.Combine(AppContext.BaseDirectory, "ECL.Runtime.jar") + ";" + optifineJar);
        psi.ArgumentList.Add("ecl.runtime.OptifineInstall");
        psi.ArgumentList.Add(dataRoot);

        using var proc = new Process { StartInfo = psi };
        var sb = new StringBuilder();

        void Collect(string? line)
        {
            if (line == null) return;
            lock (sb)
            {
                sb.AppendLine(line);
                if (sb.Length > 60_000) sb.Remove(0, 30_000);
            }
            string t = line.Trim();
            if (t.Length > 0) log?.Report("  " + t);
        }

        proc.OutputDataReceived += (_, e) => Collect(e.Data);
        proc.ErrorDataReceived += (_, e) => Collect(e.Data);

        if (!proc.Start()) throw new InvalidOperationException("无法启动 OptiFine 安装器。");
        proc.BeginOutputReadLine();
        proc.BeginErrorReadLine();

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromMinutes(5));
        try
        {
            await proc.WaitForExitAsync(timeout.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            try { proc.Kill(entireProcessTree: true); } catch { }
            throw new OperationCanceledException("OptiFine 安装超时或已取消。");
        }

        lock (sb) return (proc.ExitCode == 0, sb.ToString());
    }
}
