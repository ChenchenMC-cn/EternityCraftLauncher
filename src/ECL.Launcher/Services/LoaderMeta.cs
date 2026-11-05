// SPDX-License-Identifier: MPL-2.0
// Copyright (c) 2026 ChenchenMC <https://github.com/ChenchenMC-cn/EternityCraftLauncher>
using System.IO;
using System.Net.Http;
using ECL.Models;

namespace ECL.Services;

/// <summary>Raised when no candidate host could answer. <see cref="NotFound"/> means
/// at least one host said 404/400 — i.e. the loader has no build for that game
/// version, which is a different story from "the network is down".</summary>
public sealed class LoaderMetaException : InvalidOperationException
{
    public bool NotFound { get; }

    public LoaderMetaException(string message, bool notFound) : base(message) => NotFound = notFound;
}

/// <summary>
/// Fetches loader metadata (Fabric / Quilt / Forge build lists) with the things a
/// plain <c>GetStringAsync</c> was missing:
///
/// <list type="bullet">
/// <item>a mirror fallback — official hosts are routinely slow or unreachable from
/// mainland China, which showed up as "The request was canceled due to the
/// configured HttpClient.Timeout of 30 seconds elapsing";</item>
/// <item>a retry, because meta hosts hiccup;</item>
/// <item>an on-disk cache, so the install page still lists builds when the network
/// is down instead of going blank.</item>
/// </list>
/// </summary>
public static class LoaderMeta
{
    /// <summary>Same mirror the download planner uses for game files.</summary>
    public const string MirrorBase = "https://bmclapi2.bangbang93.com";

    private const string MirrorHost = "bangbang93";

    /// <summary>
    /// Official first, then the mirror — or the other way round when the user turned
    /// the mirror on. Either way both are tried before giving up: a mirror path that
    /// 404s just falls through to the official host.
    /// </summary>
    public static IReadOnlyList<string> Candidates(string officialUrl, string? mirrorUrl, bool preferMirror)
    {
        var list = new List<string>(2);
        if (preferMirror && !string.IsNullOrWhiteSpace(mirrorUrl)) list.Add(mirrorUrl!);
        if (!string.IsNullOrWhiteSpace(officialUrl)) list.Add(officialUrl);
        if (!preferMirror && !string.IsNullOrWhiteSpace(mirrorUrl)) list.Add(mirrorUrl!);
        return list;
    }

    /// <summary>
    /// Tries each candidate in order. Returns the first successful body, or the cached
    /// copy when every host failed, or throws with a message that names what was tried.
    /// </summary>
    public static async Task<string> FetchAsync(
        HttpClient http,
        IReadOnlyList<string> candidates,
        string cacheKey,
        GamePaths? cacheRoot,
        IProgress<string>? log,
        CancellationToken ct)
    {
        string? cachePath = CachePath(cacheRoot, cacheKey);
        var failures = new List<string>();
        bool triedMirror = candidates.Any(c => c.Contains(MirrorHost, StringComparison.OrdinalIgnoreCase));
        bool notFound = false;

        foreach (string url in candidates)
        {
            string host = SafeHost(url);

            for (int attempt = 1; attempt <= 2; attempt++)
            {
                ct.ThrowIfCancellationRequested();

                try
                {
                    string body = await http.GetStringAsync(url, ct).ConfigureAwait(false);

                    if (string.IsNullOrWhiteSpace(body))
                        throw new InvalidOperationException("响应为空");

                    SaveCache(cachePath, body);
                    if (attempt > 1) log?.Report($"加载器元数据：{host} 第 {attempt} 次尝试成功");
                    return body;
                }
                catch (OperationCanceledException) when (ct.IsCancellationRequested)
                {
                    throw;
                }
                catch (Exception ex)
                {
                    // TaskCanceledException without an outer cancel is HttpClient's own
                    // timeout; say so instead of "A task was canceled".
                    string reason = ex is TaskCanceledException
                        ? $"超时（{http.Timeout.TotalSeconds:0} 秒）"
                        : ex.Message;

                    if (reason.Contains("404", StringComparison.Ordinal) ||
                        reason.Contains("400", StringComparison.Ordinal))
                        notFound = true;

                    if (attempt == 2)
                        failures.Add($"{host}：{reason}");
                    else
                        await Task.Delay(400, ct).ConfigureAwait(false);
                }
            }
        }

        if (cachePath != null && File.Exists(cachePath))
        {
            try
            {
                string cached = await File.ReadAllTextAsync(cachePath, ct).ConfigureAwait(false);
                if (!string.IsNullOrWhiteSpace(cached))
                {
                    log?.Report($"联网失败，改用缓存的加载器列表（{File.GetLastWriteTime(cachePath):MM-dd HH:mm}）");
                    return cached;
                }
            }
            catch
            {
                // Fall through to the exception below.
            }
        }

        string detail = failures.Count > 0 ? string.Join("；", failures) : "没有可用的地址";
        string hint = triedMirror
            ? "官方和镜像都不通，检查网络/代理后重试。"
            : "可以在设置里打开「使用镜像」后重试，或检查网络/代理。";

        throw new LoaderMetaException($"连不上加载器的版本服务器（{detail}）。{hint}", notFound);
    }

    /// <summary>HttpClient tuned for metadata calls: short body, no huge timeouts.</summary>
    public static HttpClient CreateClient(int timeoutSeconds = 20)
    {
        var client = new HttpClient(new SocketsHttpHandler
        {
            ConnectTimeout = TimeSpan.FromSeconds(8),
            PooledConnectionLifetime = TimeSpan.FromMinutes(5)
        })
        {
            Timeout = TimeSpan.FromSeconds(timeoutSeconds)
        };

        client.DefaultRequestHeaders.UserAgent.ParseAdd("ECL/0.2 (EternityCraftLauncher)");
        return client;
    }

    private static string? CachePath(GamePaths? root, string cacheKey)
    {
        if (root == null || string.IsNullOrWhiteSpace(cacheKey)) return null;

        try
        {
            string dir = Path.Combine(root.Root, "cache", "loader-meta");
            Directory.CreateDirectory(dir);

            char[] invalid = Path.GetInvalidFileNameChars();
            var safe = new System.Text.StringBuilder(cacheKey.Length);
            foreach (char c in cacheKey)
                safe.Append(Array.IndexOf(invalid, c) >= 0 ? '_' : c);

            return Path.Combine(dir, safe + ".json");
        }
        catch
        {
            return null;
        }
    }

    private static void SaveCache(string? path, string body)
    {
        if (path == null) return;
        try { File.WriteAllText(path, body); }
        catch { /* cache is a nicety, never a failure */ }
    }

    private static string SafeHost(string url)
    {
        try { return new Uri(url).Host; }
        catch { return url; }
    }
}
