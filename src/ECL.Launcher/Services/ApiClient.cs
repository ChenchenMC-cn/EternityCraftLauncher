// SPDX-License-Identifier: MPL-2.0
// Copyright (c) 2026 ChenchenMC <https://github.com/ChenchenMC-cn/EternityCraftLauncher>
using System.IO;
using System.Net.Http;
using System.Text.Json;
using ECL.Models;

namespace ECL.Services;

/// <summary>
/// Talks to the Mojang metadata endpoints (optionally through a mirror) and keeps
/// a local copy of the version manifest so the launcher still lists versions when
/// the network is down.
/// </summary>
public sealed class ApiClient : IDisposable
{
    public const string OfficialManifest = "https://piston-meta.mojang.com/mc/game/version_manifest_v2.json";

    private readonly HttpClient _http;
    private readonly LauncherSettings _settings;
    private readonly string _manifestCache;

    private static readonly JsonSerializerOptions JsonOpts = new() { PropertyNameCaseInsensitive = true };

    public ApiClient(LauncherSettings settings, string cacheDir)
    {
        _settings = settings;
        _http = new HttpClient(new SocketsHttpHandler
        {
            PooledConnectionLifetime = TimeSpan.FromMinutes(10),
            AutomaticDecompression = System.Net.DecompressionMethods.All,
            ConnectTimeout = TimeSpan.FromSeconds(20)
        })
        {
            Timeout = TimeSpan.FromSeconds(120)
        };
        _http.DefaultRequestHeaders.UserAgent.ParseAdd("ECL/0.1 (EternityCraftLauncher)");
        _manifestCache = Path.Combine(cacheDir, "version_manifest_v2.json");
    }

    public bool UsingMirror => _settings.UseMirror;

    /// <summary>Maps an official CDN url onto the configured mirror, when enabled.</summary>
    public string Rewrite(string url)
    {
        if (!_settings.UseMirror || string.IsNullOrEmpty(url)) return url;
        string m = _settings.MirrorBase;

        // Only bulk payload hosts are remapped. Metadata stays on the official
        // host because mirrors expose it under different paths.
        if (url.StartsWith("https://libraries.minecraft.net/", StringComparison.OrdinalIgnoreCase))
            return m + "/maven/" + url["https://libraries.minecraft.net/".Length..];
        if (url.StartsWith("https://resources.download.minecraft.net/", StringComparison.OrdinalIgnoreCase))
            return m + "/assets/" + url["https://resources.download.minecraft.net/".Length..];
        return url;
    }

    public string ClientJarUrl(string versionId, string officialUrl)
    {
        if (!_settings.UseMirror) return officialUrl;
        return $"{_settings.MirrorBase}/version/{Uri.EscapeDataString(versionId)}/client";
    }

    public async Task<VersionManifest> GetManifestAsync(IProgress<string>? log, CancellationToken ct)
    {
        string primary = _settings.UseMirror
            ? _settings.MirrorBase + "/mc/game/version_manifest_v2.json"
            : OfficialManifest;

        foreach (var candidate in new[] { primary, OfficialManifest })
        {
            try
            {
                log?.Report($"Fetching version list: {new Uri(candidate).Host}");
                var text = await _http.GetStringAsync(candidate, ct).ConfigureAwait(false);
                var manifest = JsonSerializer.Deserialize<VersionManifest>(text, JsonOpts);
                if (manifest?.Versions.Count > 0)
                {
                    await SaveCacheAsync(text).ConfigureAwait(false);
                    return manifest;
                }
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                log?.Report($"  ... failed ({ex.GetType().Name}), trying next source");
            }
        }

        if (File.Exists(_manifestCache))
        {
            log?.Report("Network unavailable - using cached version list.");
            var text = await File.ReadAllTextAsync(_manifestCache, ct).ConfigureAwait(false);
            var manifest = JsonSerializer.Deserialize<VersionManifest>(text, JsonOpts);
            if (manifest != null) return manifest;
        }
        throw new InvalidOperationException("Could not load the version list. Check your connection.");
    }

    private async Task SaveCacheAsync(string text)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_manifestCache)!);
            await File.WriteAllTextAsync(_manifestCache, text).ConfigureAwait(false);
        }
        catch
        {
            // cache is optional
        }
    }

    /// <summary>Downloads the per-version json document as text.</summary>
    public async Task<string> GetVersionJsonAsync(string versionId, string officialUrl, CancellationToken ct)
    {
        var urls = new List<string>();
        if (_settings.UseMirror)
            urls.Add($"{_settings.MirrorBase}/version/{Uri.EscapeDataString(versionId)}/json");
        urls.Add(officialUrl);

        Exception? last = null;
        foreach (var url in urls)
        {
            try
            {
                return await _http.GetStringAsync(url, ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                last = ex;
            }
        }
        throw new InvalidOperationException($"Could not fetch the metadata for {versionId}: {last?.Message}");
    }

    public async Task<string> GetStringAsync(string url, CancellationToken ct)
        => await _http.GetStringAsync(url, ct).ConfigureAwait(false);

    public void Dispose() => _http.Dispose();
}
