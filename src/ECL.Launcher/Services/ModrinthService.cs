// SPDX-License-Identifier: MPL-2.0
// Copyright (c) 2026 ChenchenMC <https://github.com/ChenchenMC-cn/EternityCraftLauncher>
using System.IO;
using System.Net.Http;
using System.Text.Json;

namespace ECL.Services;

/// <summary>One search result from Modrinth.</summary>
public sealed record ModrinthHit(
    string ProjectId,
    string Slug,
    string Title,
    string Author,
    long Downloads,
    string Description,
    string DateModified)
{
    /// <summary>Compact Chinese-friendly download count ("2.5亿", "35.8万").</summary>
    public string DownloadsText => Downloads switch
    {
        >= 100_000_000 => $"{Downloads / 100_000_000.0:0.0#}亿",
        >= 10_000 => $"{Downloads / 10_000.0:0.#}万",
        _ => Downloads.ToString("N0")
    };
}

/// <summary>A downloadable file of one project version.</summary>
public sealed record ModrinthFile(string Url, string FileName, long Size, bool Primary);

/// <summary>One published version of a project, as shown in the version picker.</summary>
public sealed record ModrinthVersion(
    string Id,
    string Name,
    string VersionNumber,
    string Channel,
    IReadOnlyList<string> GameVersions,
    IReadOnlyList<string> Loaders,
    long Downloads,
    string DatePublished,
    ModrinthFile? File)
{
    public bool IsRelease => Channel.Equals("release", StringComparison.OrdinalIgnoreCase);

    public string ChannelLabel => Channel switch
    {
        "release" => "正式版",
        "beta" => "测试版",
        "alpha" => "内测版",
        _ => Channel
    };

    /// <summary>Short "1.20.1 · fabric" tail for the row subtitle.</summary>
    public string TargetText
    {
        get
        {
            string gv = GameVersions.Count switch
            {
                0 => "",
                1 => GameVersions[0],
                _ => $"{GameVersions[0]} +{GameVersions.Count - 1}"
            };
            string loaders = Loaders.Count > 0 ? string.Join("/", Loaders) : "";
            if (gv.Length == 0) return loaders;
            return loaders.Length == 0 ? gv : $"{gv} · {loaders}";
        }
    }

    public string SizeText => File == null ? "" : File.Size switch
    {
        >= 1024 * 1024 => $"{File.Size / 1024.0 / 1024.0:0.0} MB",
        >= 1024 => $"{File.Size / 1024.0:0} KB",
        _ => $"{File.Size} B"
    };

    public string DownloadsText => Downloads switch
    {
        >= 100_000_000 => $"{Downloads / 100_000_000.0:0.0#}亿",
        >= 10_000 => $"{Downloads / 10_000.0:0.#}万",
        _ => Downloads.ToString("N0")
    };

    public bool HasFile => File != null;
}

/// <summary>
/// Modrinth API v2 client: search, version listing, download.
///
/// <para>Search and metadata come from <c>api.modrinth.com</c>; the jars live on
/// <c>cdn.modrinth.com</c>. No API key is needed — unlike CurseForge, whose API
/// requires a key ECL will not ship anonymously.</para>
/// </summary>
public sealed class ModrinthService
{
    private const string ApiBase = "https://api.modrinth.com/v2";

    private readonly HttpClient _http;
    private readonly ModService _mods;
    private readonly GamePaths _paths;

    public ModrinthService(GamePaths paths)
    {
        _paths = paths;
        _mods = new ModService(paths);
        _http = new HttpClient { Timeout = TimeSpan.FromSeconds(30) };
        _http.DefaultRequestHeaders.UserAgent.ParseAdd("ECL/0.8 (EternityCraftLauncher)");
        // Modrinth asks for a contact address in the UA; the project has none, so
        // the launcher name stays the identifier.
    }

    /// <summary>Modrinth "categories" facet value for a version kind, null = don't filter.</summary>
    public static string? LoaderCategory(LoaderKind kind) => kind switch
    {
        LoaderKind.Fabric => "fabric",
        LoaderKind.Quilt => "quilt",
        LoaderKind.Forge => "forge",
        _ => null
    };

    // ------------------------------------------------------------- search ---

    public async Task<List<ModrinthHit>> SearchAsync(string query, string gameVersion,
        string? loaderCategory, int limit = 25, CancellationToken ct = default)
    {
        // Facets are JSON arrays of arrays, all entries AND-ed.
        string facets = loaderCategory is null
            ? $"[[\"versions:{gameVersion}\"]]"
            : $"[[\"versions:{gameVersion}\"],[\"categories:{loaderCategory}\"]]";

        string url = $"{ApiBase}/search?limit={limit}&query={Uri.EscapeDataString(query ?? "")}" +
                     $"&facets={Uri.EscapeDataString(facets)}";

        using var resp = await _http.GetAsync(url, ct).ConfigureAwait(false);
        resp.EnsureSuccessStatusCode();
        using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false));

        var result = new List<ModrinthHit>();
        foreach (var hit in doc.RootElement.GetProperty("hits").EnumerateArray())
        {
            string projectId = GetStr(hit, "project_id");
            if (projectId.Length == 0) continue;
            result.Add(new ModrinthHit(
                projectId,
                GetStr(hit, "slug"),
                GetStr(hit, "title"),
                GetStr(hit, "author"),
                hit.TryGetProperty("downloads", out var d) && d.TryGetInt64(out long dl) ? dl : 0,
                GetStr(hit, "description"),
                GetStr(hit, "date_modified")));
        }
        return result;
    }

    // ----------------------------------------------------------- versions ---

    /// <summary>
    /// Every published version of a project that targets <paramref name="gameVersion"/>
    /// and (optionally) a loader, newest first. Used by the version picker so the
    /// user chooses instead of silently getting whatever is newest.
    /// </summary>
    public async Task<List<ModrinthVersion>> ListVersionsAsync(string projectIdOrSlug,
        string? gameVersion, string? loaderCategory, CancellationToken ct = default)
    {
        var query = new List<string>();
        if (!string.IsNullOrWhiteSpace(gameVersion))
            query.Add("game_versions=" + Uri.EscapeDataString($"[\"{gameVersion}\"]"));
        if (!string.IsNullOrWhiteSpace(loaderCategory))
            query.Add("loaders=" + Uri.EscapeDataString($"[\"{loaderCategory}\"]"));

        string url = $"{ApiBase}/project/{Uri.EscapeDataString(projectIdOrSlug)}/version";
        if (query.Count > 0) url += "?" + string.Join("&", query);

        using var resp = await _http.GetAsync(url, ct).ConfigureAwait(false);
        resp.EnsureSuccessStatusCode();
        using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false));

        var result = new List<ModrinthVersion>();
        foreach (var v in doc.RootElement.EnumerateArray())
        {
            ModrinthFile? file = null;
            if (v.TryGetProperty("files", out var files) &&
                files.ValueKind == JsonValueKind.Array)
            {
                ModrinthFile? primary = null, first = null;
                foreach (var f in files.EnumerateArray())
                {
                    string u = GetStr(f, "url");
                    if (u.Length == 0) continue;

                    var candidate = new ModrinthFile(
                        u,
                        GetStr(f, "filename"),
                        f.TryGetProperty("size", out var sz) && sz.TryGetInt64(out long s) ? s : 0,
                        f.TryGetProperty("primary", out var p) && p.ValueKind == JsonValueKind.True);

                    first ??= candidate;
                    if (candidate.Primary) { primary = candidate; break; }
                }
                file = primary ?? first;
            }

            result.Add(new ModrinthVersion(
                GetStr(v, "id"),
                GetStr(v, "name"),
                GetStr(v, "version_number"),
                GetStr(v, "version_type"),
                ReadStringArray(v, "game_versions"),
                ReadStringArray(v, "loaders"),
                v.TryGetProperty("downloads", out var d) && d.TryGetInt64(out long dl) ? dl : 0,
                GetStr(v, "date_published"),
                file));
        }

        return result;
    }

    private static IReadOnlyList<string> ReadStringArray(JsonElement parent, string name)
    {
        if (!parent.TryGetProperty(name, out var arr) || arr.ValueKind != JsonValueKind.Array)
            return Array.Empty<string>();

        return arr.EnumerateArray()
                  .Where(x => x.ValueKind == JsonValueKind.String)
                  .Select(x => x.GetString() ?? "")
                  .Where(x => x.Length > 0)
                  .ToList();
    }

    /// <summary>Newest file of a project that matches the game version and loader.</summary>
    public async Task<ModrinthFile?> LatestFileAsync(string projectIdOrSlug, string gameVersion,
        string? loaderCategory, CancellationToken ct = default)
    {
        string gvFacet = Uri.EscapeDataString($"[\"{gameVersion}\"]");
        string url = $"{ApiBase}/project/{Uri.EscapeDataString(projectIdOrSlug)}/version?game_versions={gvFacet}";
        if (loaderCategory != null)
            url += "&loaders=" + Uri.EscapeDataString($"[\"{loaderCategory}\"]");

        using var resp = await _http.GetAsync(url, ct).ConfigureAwait(false);
        resp.EnsureSuccessStatusCode();
        using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false));

        foreach (var version in doc.RootElement.EnumerateArray())
        {
            // Versions come newest-first; take the first one that has a file.
            if (!version.TryGetProperty("files", out var files)) continue;
            ModrinthFile? primary = null, first = null;
            foreach (var f in files.EnumerateArray())
            {
                string url2 = GetStr(f, "url");
                if (url2.Length == 0) continue;
                var file = new ModrinthFile(
                    url2,
                    GetStr(f, "filename"),
                    f.TryGetProperty("size", out var sz) && sz.TryGetInt64(out long s) ? s : 0,
                    f.TryGetProperty("primary", out var p) && p.ValueKind == JsonValueKind.True);
                first ??= file;
                if (file.Primary) { primary = file; break; }
            }
            var chosen = primary ?? first;
            if (chosen != null) return chosen;
        }
        return null;
    }

    // ---------------------------------------------------------- download ---

    /// <summary>
    /// Streams the jar into the version's mods folder. Throws with the direct URL
    /// included when the CDN is unreachable, so the user can grab it manually.
    /// </summary>
    public async Task<ModEntry> DownloadIntoModsAsync(ModrinthFile file, string versionId,
        IProgress<(long Done, long Total)>? progress, CancellationToken ct = default)
    {
        string modsDir = _mods.ModsDir(versionId);
        Directory.CreateDirectory(modsDir);
        string dest = Path.Combine(modsDir, file.FileName);

        HttpResponseMessage resp;
        try
        {
            resp = await _http.GetAsync(file.Url, HttpCompletionOption.ResponseHeadersRead, ct)
                              .ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
        {
            throw new InvalidOperationException(
                "无法连接 Modrinth 下载服务器（cdn.modrinth.com）。" +
                "可到浏览器手动下载后「导入模组」：\n" + file.Url);
        }

        if (!resp.IsSuccessStatusCode)
            throw new InvalidOperationException($"下载失败（HTTP {(int)resp.StatusCode}）：{file.Url}");

        long total = file.Size > 0 ? file.Size : (long?)resp.Content.Headers.ContentLength ?? -1;
        await using var src = await resp.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
        await using var outFs = File.Create(dest);

        var buffer = new byte[256 * 1024];
        long done = 0;
        int read;
        while ((read = await src.ReadAsync(buffer, ct).ConfigureAwait(false)) > 0)
        {
            await outFs.WriteAsync(buffer.AsMemory(0, read), ct).ConfigureAwait(false);
            done += read;
            progress?.Report((done, total));
        }

        var entry = new ModEntry
        {
            FileName = Path.GetFileName(dest),
            FullPath = dest,
            SizeBytes = new FileInfo(dest).Length,
            IsEnabled = true
        };
        try { ModService.ReadMetadata(entry, dest); }
        catch { /* metadata stays unparsed; the list still shows the file */ }
        return entry;
    }

    /// <summary>One-stop install used by the UI button and the headless mode.</summary>
    public async Task<ModEntry> InstallAsync(string versionId, string projectIdOrSlug, string gameVersion,
        string? loaderCategory, IProgress<(long Done, long Total)>? progress, CancellationToken ct = default)
    {
        var file = await LatestFileAsync(projectIdOrSlug, gameVersion, loaderCategory, ct).ConfigureAwait(false)
                   ?? throw new InvalidOperationException(
                       $"Modrinth 上没有 {projectIdOrSlug} 的 {gameVersion}" +
                       (loaderCategory != null ? $" / {loaderCategory}" : "") + " 版本文件。");

        return await DownloadIntoModsAsync(file, versionId, progress, ct).ConfigureAwait(false);
    }

    private static string GetStr(JsonElement obj, string name) =>
        obj.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() ?? "" : "";
}
