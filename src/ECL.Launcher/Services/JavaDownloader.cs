// SPDX-License-Identifier: MPL-2.0
// Copyright (c) 2026 ChenchenMC <https://github.com/ChenchenMC-cn/EternityCraftLauncher>
using System.IO;
using System.IO.Compression;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text.Json;
using ECL.Models;

namespace ECL.Services;

/// <summary>
/// Downloads an Eclipse Temurin (Adoptium) JRE when the version in hand needs a
/// Java major that is not installed.
///
/// <para>Metadata comes from api.adoptium.net; the actual zip is fetched from the
/// TUNA mirror first (reachable and fast in China) and falls back to the official
/// GitHub release. The archive is SHA-256 checked against the checksum Adoptium
/// publishes, extracted into <c>runtime/java-{major}/</c>, and rediscovered by
/// <see cref="JavaLocator"/>.</para>
/// </summary>
public static class JavaDownloader
{
    private const string ApiTemplate =
        "https://api.adoptium.net/v3/assets/feature_releases/{0}/ga" +
        "?architecture=x64&heap_size=normal&image_type=jre&jvm_impl=hotspot" +
        "&os=windows&page=0&page_size=1&project=jdk&sort_method=DATE&sort_order=DESC&vendor=eclipse";

    private const string TunaMirrorTemplate =
        "https://mirrors.tuna.tsinghua.edu.cn/Adoptium/{0}/jre/x64/windows/{1}";

    /// <summary>
    /// The Java major to install for a given Minecraft requirement. Old versions
    /// (Java 8) break on Java 9+, so they are pinned to 8; everything modern maps
    /// to the nearest LTS (17 / 21).
    /// </summary>
    public static int TargetMajorFor(int requiredMajor) =>
        requiredMajor <= 8 ? 8 : requiredMajor <= 17 ? 17 : 21;

    /// <summary>
    /// Ensures a JRE of <paramref name="major"/> exists under the data root and
    /// returns its javaw.exe path (or null on failure). Idempotent.
    /// </summary>
    public static async Task<string?> EnsureAsync(int major, GamePaths paths,
        IProgress<string>? log, CancellationToken ct)
    {
        string destDir = paths.JavaDir(major);
        string javaw = Path.Combine(destDir, "bin", "javaw.exe");
        if (File.Exists(javaw)) return javaw;

        using var http = new HttpClient { Timeout = TimeSpan.FromMinutes(15) };
        http.DefaultRequestHeaders.UserAgent.ParseAdd("ECL/0.8");

        log?.Report($"查询 Temurin Java {major}（JRE x64）…");
        string json = await http.GetStringAsync(string.Format(ApiTemplate, major), ct).ConfigureAwait(false);
        using var doc = JsonDocument.Parse(json);

        string? name = null, link = null, sha256 = null;
        long size = -1;
        foreach (var asset in doc.RootElement.EnumerateArray())
        {
            if (!asset.TryGetProperty("binaries", out var bins)) continue;
            foreach (var bin in bins.EnumerateArray())
            {
                if (!bin.TryGetProperty("package", out var pkg)) continue;
                name = pkg.TryGetProperty("name", out var n) ? n.GetString() : null;
                if (name == null || !name.EndsWith(".zip", StringComparison.OrdinalIgnoreCase)) continue;
                link = pkg.TryGetProperty("link", out var l) ? l.GetString() : null;
                size = pkg.TryGetProperty("size", out var sz) && sz.TryGetInt64(out long s) ? s : -1;
                sha256 = pkg.TryGetProperty("checksum", out var c) ? c.GetString() : null;
                break;
            }
            if (name != null) break;
        }

        if (string.IsNullOrEmpty(name))
            throw new InvalidOperationException($"Adoptium 上没有 Java {major} 的 JRE（x64 windows）。");

        string zipPath = Path.Combine(paths.RuntimeDir, $"{name}.download");
        Directory.CreateDirectory(paths.RuntimeDir);

        string? tried = null;
        var urls = new List<string>
        {
            string.Format(TunaMirrorTemplate, major, name),
            link!
        };
        foreach (var url in urls.Where(u => !string.IsNullOrEmpty(u)))
        {
            tried = url;
            log?.Report($"下载 Java {major}（{name}）…");
            try
            {
                await DownloadAsync(http, url, zipPath, size, sha256, log, ct).ConfigureAwait(false);
                break;
            }
            catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or IOException)
            {
                log?.Report("  下载失败：" + ex.Message + "，尝试下一个源…");
                try { File.Delete(zipPath); } catch { }
                if (url == urls[^1]) throw;
            }
        }

        // Verify before touching the destination: never leave a half-written JRE.
        if (size > 0 && new FileInfo(zipPath).Length != size)
            throw new IOException($"Java 包大小不符（{new FileInfo(zipPath).Length} != {size}），已放弃。");

        if (!string.IsNullOrEmpty(sha256))
        {
            string actual = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(zipPath))).ToLowerInvariant();
            if (!string.Equals(actual, sha256, StringComparison.OrdinalIgnoreCase))
                throw new IOException($"Java 包校验和不符，已放弃（{actual[..16]}…）。");
            log?.Report("SHA-256 校验通过");
        }

        log?.Report("解压 Java 运行时…");
        string extractRoot = Path.Combine(paths.RuntimeDir, ".extract-" + major);
        PathGuard.EnsureUsable(destDir, "Java 运行时目录");
        if (Directory.Exists(extractRoot)) Directory.Delete(extractRoot, recursive: true);
        Directory.CreateDirectory(extractRoot);
        ZipFile.ExtractToDirectory(zipPath, extractRoot);

        // The archive contains a single folder like "jdk-17.0.20.1+1-jre"; move it
        // into place under the stable name runtime/java-{major}.
        string? inner = Directory.GetDirectories(extractRoot).FirstOrDefault()
                        ?? throw new IOException("Java 包解压后没有内容目录。");
        if (Directory.Exists(destDir)) Directory.Delete(destDir, recursive: true);
        Directory.Move(inner, destDir);
        Directory.Delete(extractRoot, recursive: true);
        try { File.Delete(zipPath); } catch { }

        log?.Report($"Java {major} 安装完成：{destDir}");
        return File.Exists(javaw) ? javaw : null;
    }

    private static async Task DownloadAsync(HttpClient http, string url, string dest,
        long expectedSize, string? sha256, IProgress<string>? log, CancellationToken ct)
    {
        using var resp = await http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
        resp.EnsureSuccessStatusCode();

        await using var src = await resp.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
        await using var fs = File.Create(dest);

        var buffer = new byte[256 * 1024];
        long done = 0;
        int read;
        int lastPercent = -1;

        while ((read = await src.ReadAsync(buffer, ct).ConfigureAwait(false)) > 0)
        {
            await fs.WriteAsync(buffer.AsMemory(0, read), ct).ConfigureAwait(false);
            done += read;

            // One line per 10% instead of one per 4 MB: a 42 MB JRE used to print
            // eleven near-identical lines and bury everything else in the log.
            if (expectedSize <= 0) continue;
            int percent = (int)(done * 100 / expectedSize);
            if (percent / 10 == lastPercent / 10) continue;

            lastPercent = percent;
            log?.Report($"  {percent}%  ({done / 1024.0 / 1024.0:F1} / {expectedSize / 1024.0 / 1024.0:F1} MB)");
        }
    }
}
