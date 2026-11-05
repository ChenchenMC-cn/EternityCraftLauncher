// SPDX-License-Identifier: MPL-2.0
// Copyright (c) 2026 ChenchenMC <https://github.com/ChenchenMC-cn/EternityCraftLauncher>
using System.IO;
using System.Net.Http;
using System.Text.Json;
using ECL.Models;

namespace ECL.Services;

public sealed record FabricLoaderInfo(string Version, bool Stable);

/// <summary>
/// Installs Fabric by pulling a ready-made profile from meta.fabricmc.net.
/// Fabric ships a complete version json (inheritsFrom + libraries + mainClass),
/// so no installer process is needed: write the json, then let the normal
/// download planner fetch the loader libraries.
/// </summary>
public sealed class FabricInstaller
{
    public const string MetaBase = "https://meta.fabricmc.net";
    public const string MavenBase = "https://maven.fabricmc.net";

    /// <summary>bmclapi mirrors the whole meta tree under this prefix.</summary>
    public const string MirrorMetaBase = LoaderMeta.MirrorBase + "/fabric-meta";

    private readonly HttpClient _http;
    private readonly GamePaths _paths;
    private readonly bool _useMirror;

    private static readonly JsonSerializerOptions JsonOpts = new() { PropertyNameCaseInsensitive = true };

    public FabricInstaller(GamePaths paths, HttpClient? http = null, bool useMirror = false)
    {
        _paths = paths;
        _http = http ?? LoaderMeta.CreateClient();
        _useMirror = useMirror;
        _http.DefaultRequestHeaders.UserAgent.ParseAdd("ECL/0.2 (EternityCraftLauncher)");
    }

    private IReadOnlyList<string> Urls(string officialTail, string mirrorTail) =>
        LoaderMeta.Candidates(MetaBase + officialTail, MirrorMetaBase + mirrorTail, _useMirror);

    public async Task<List<FabricLoaderInfo>> GetLoadersAsync(string mcVersion, CancellationToken ct,
        IProgress<string>? log = null)
    {
        string tail = $"/v2/versions/loader/{Uri.EscapeDataString(mcVersion)}";
        string json;
        try
        {
            json = await LoaderMeta.FetchAsync(_http, Urls(tail, tail), $"fabric-loaders-{mcVersion}",
                _paths, log, ct).ConfigureAwait(false);
        }
        catch (LoaderMetaException ex) when (ex.NotFound)
        {
            // Both hosts said 404/400: Fabric has no loader for this game version.
            // The caller turns an empty list into the "不支持这个版本" message.
            log?.Report($"Fabric：没有 {mcVersion} 的 loader（接口 404）");
            return new List<FabricLoaderInfo>();
        }

        var list = new List<FabricLoaderInfo>();
        using var doc = JsonDocument.Parse(json);
        if (doc.RootElement.ValueKind != JsonValueKind.Array) return list;

        foreach (var item in doc.RootElement.EnumerateArray())
        {
            // Shape: [ { "loader": { "version": "0.16.9", "stable": true }, "intermediary": ... } ]
            if (!item.TryGetProperty("loader", out var loader)) continue;
            string? version = loader.TryGetProperty("version", out var v) ? v.GetString() : null;
            if (string.IsNullOrEmpty(version)) continue;

            bool stable = loader.TryGetProperty("stable", out var s) && s.ValueKind == JsonValueKind.True;
            list.Add(new FabricLoaderInfo(version, stable));
        }
        return list;
    }

    /// <summary>Fetches the profile and writes versions/&lt;id&gt;/&lt;id&gt;.json. Returns the version id.</summary>
    public async Task<string> InstallProfileAsync(string mcVersion, string? loaderVersion,
        IProgress<string>? log, CancellationToken ct)
    {
        var loaders = await GetLoadersAsync(mcVersion, ct).ConfigureAwait(false);
        if (loaders.Count == 0)
            throw new InvalidOperationException($"Fabric 不支持 Minecraft {mcVersion}（没有可用的 loader）。");

        var loader = loaderVersion == null
            ? loaders.FirstOrDefault(l => l.Stable) ?? loaders[0]
            : loaders.FirstOrDefault(l => l.Version == loaderVersion)
              ?? throw new InvalidOperationException($"Fabric loader {loaderVersion} 不存在。");

        log?.Report($"Fabric loader {loader.Version}（{(loader.Stable ? "稳定版" : "测试版")}）");

        string tail = $"/v2/versions/loader/{Uri.EscapeDataString(mcVersion)}/{Uri.EscapeDataString(loader.Version)}/profile/json";
        string json = await LoaderMeta.FetchAsync(_http, Urls(tail, tail),
            $"fabric-profile-{mcVersion}-{loader.Version}", _paths, log, ct).ConfigureAwait(false);

        var detail = JsonSerializer.Deserialize<VersionDetail>(json, JsonOpts)
                     ?? throw new InvalidOperationException("Fabric 返回的 profile 无法解析。");
        if (string.IsNullOrEmpty(detail.Id))
            throw new InvalidOperationException("Fabric profile 缺少 id 字段。");
        if (detail.InheritsFrom != mcVersion)
            throw new InvalidOperationException($"Fabric profile 的父版本是 {detail.InheritsFrom}，与请求的 {mcVersion} 不符。");

        Directory.CreateDirectory(_paths.VersionDir(detail.Id));
        await File.WriteAllTextAsync(_paths.VersionJson(detail.Id), json, ct).ConfigureAwait(false);

        log?.Report($"已写入 Fabric 配置：{detail.Id}（{detail.Libraries.Count} 个库）");
        return detail.Id;
    }

    /// <summary>True when a Fabric profile for this version is already on disk.</summary>
    public bool IsInstalled(string versionId) =>
        File.Exists(_paths.VersionJson(versionId)) &&
        File.Exists(Path.Combine(_paths.VersionDir(versionId), ".installed"));
}
