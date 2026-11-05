// SPDX-License-Identifier: MPL-2.0
// Copyright (c) 2026 ChenchenMC <https://github.com/ChenchenMC-cn/EternityCraftLauncher>
using System.IO;
using System.Net.Http;
using System.Text.Json;
using ECL.Models;

namespace ECL.Services;

public sealed record QuiltLoaderInfo(string Version);

/// <summary>
/// Installs Quilt the same way Fabric is installed: the meta server already
/// publishes a complete version json (inheritsFrom + libraries + KnotClient), so
/// writing it and planning the downloads is the whole job.
///
/// <para>Quilt's v3 API has the same nested shape as Fabric's v2
/// (<c>[{ "loader": { "version": ... } }]</c>) but never sets a <c>stable</c> flag,
/// so "stable" is derived from the version string: <c>0.26.4</c> is stable,
/// <c>0.20.0-beta.9</c> is not.</para>
/// </summary>
public sealed class QuiltInstaller
{
    public const string MetaBase = "https://meta.quiltmc.org";
    public const string MavenBase = "https://maven.quiltmc.org/repository/release";

    /// <summary>
    /// bmclapi has no <c>quilt-meta</c> tree (verified: 404 on
    /// <c>/quilt-meta/v3/versions/loader/1.20.1</c>, 2026-11-01), so Quilt keeps the
    /// official host. It still gets the retry and the on-disk cache from
    /// <see cref="LoaderMeta"/>.
    /// </summary>
    private const string? MirrorMetaBase = null;

    private readonly HttpClient _http;
    private readonly GamePaths _paths;
    private readonly bool _useMirror;

    private static readonly JsonSerializerOptions JsonOpts = new() { PropertyNameCaseInsensitive = true };

    public QuiltInstaller(GamePaths paths, HttpClient? http = null, bool useMirror = false)
    {
        _paths = paths;
        _http = http ?? LoaderMeta.CreateClient();
        _useMirror = useMirror;
        _http.DefaultRequestHeaders.UserAgent.ParseAdd("ECL/0.3 (EternityCraftLauncher)");
    }

    private IReadOnlyList<string> Urls(string officialTail, string mirrorTail) =>
        LoaderMeta.Candidates(MetaBase + officialTail, MirrorMetaBase + mirrorTail, _useMirror);

    public async Task<List<QuiltLoaderInfo>> GetLoadersAsync(string mcVersion, CancellationToken ct,
        IProgress<string>? log = null)
    {
        string tail = $"/v3/versions/loader/{Uri.EscapeDataString(mcVersion)}";
        string json;
        try
        {
            json = await LoaderMeta.FetchAsync(_http, Urls(tail, tail), $"quilt-loaders-{mcVersion}",
                _paths, log, ct).ConfigureAwait(false);
        }
        catch (LoaderMetaException ex) when (ex.NotFound)
        {
            log?.Report($"Quilt：没有 {mcVersion} 的 loader（接口 404）");
            return new List<QuiltLoaderInfo>();
        }

        var list = new List<QuiltLoaderInfo>();
        using var doc = JsonDocument.Parse(json);
        if (doc.RootElement.ValueKind != JsonValueKind.Array) return list;

        foreach (var item in doc.RootElement.EnumerateArray())
        {
            if (!item.TryGetProperty("loader", out var loader)) continue;
            string? version = loader.TryGetProperty("version", out var v) ? v.GetString() : null;
            if (!string.IsNullOrEmpty(version)) list.Add(new QuiltLoaderInfo(version));
        }
        return list;
    }

    /// <summary>True for release-style versions; Quilt has no stable flag in its meta.</summary>
    public static bool IsStable(string version) =>
        !version.Contains("-beta", StringComparison.OrdinalIgnoreCase) &&
        !version.Contains("-alpha", StringComparison.OrdinalIgnoreCase) &&
        !version.Contains("-rc", StringComparison.OrdinalIgnoreCase) &&
        !version.Contains("-pre", StringComparison.OrdinalIgnoreCase);

    public async Task<string> InstallProfileAsync(string mcVersion, string? loaderVersion,
        IProgress<string>? log, CancellationToken ct)
    {
        var loaders = await GetLoadersAsync(mcVersion, ct).ConfigureAwait(false);
        if (loaders.Count == 0)
            throw new InvalidOperationException($"Quilt 不支持 Minecraft {mcVersion}（没有可用的 loader）。");

        var loader = loaderVersion == null
            ? loaders.FirstOrDefault(l => IsStable(l.Version)) ?? loaders[0]
            : loaders.FirstOrDefault(l => l.Version == loaderVersion)
              ?? throw new InvalidOperationException($"Quilt loader {loaderVersion} 不存在。");

        log?.Report($"Quilt loader {loader.Version}（{(IsStable(loader.Version) ? "稳定版" : "测试版")}）");

        string tail = $"/v3/versions/loader/{Uri.EscapeDataString(mcVersion)}/{Uri.EscapeDataString(loader.Version)}/profile/json";
        string json = await LoaderMeta.FetchAsync(_http, Urls(tail, tail),
            $"quilt-profile-{mcVersion}-{loader.Version}", _paths, log, ct).ConfigureAwait(false);

        var detail = JsonSerializer.Deserialize<VersionDetail>(json, JsonOpts)
                     ?? throw new InvalidOperationException("Quilt 返回的 profile 无法解析。");
        if (string.IsNullOrEmpty(detail.Id))
            throw new InvalidOperationException("Quilt profile 缺少 id 字段。");
        if (detail.InheritsFrom != mcVersion)
            throw new InvalidOperationException($"Quilt profile 的父版本是 {detail.InheritsFrom}，与请求的 {mcVersion} 不符。");

        Directory.CreateDirectory(_paths.VersionDir(detail.Id));
        await File.WriteAllTextAsync(_paths.VersionJson(detail.Id), json, ct).ConfigureAwait(false);

        log?.Report($"已写入 Quilt 配置：{detail.Id}（{detail.Libraries.Count} 个库）");
        return detail.Id;
    }
}
