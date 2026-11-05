// SPDX-License-Identifier: MPL-2.0
// Copyright (c) 2026 ChenchenMC <https://github.com/ChenchenMC-cn/EternityCraftLauncher>
using System.IO;
using System.Text.Json;
using ECL.Models;

namespace ECL.Services;

public readonly record struct InstallProgress(string Stage, DownloadProgress Download);

/// <summary>
/// Turns a version id into a concrete file list, then hands that list to the
/// native download engine. Handles both a fresh vanilla install (metadata fetched
/// from Mojang) and an already-written modded profile (Fabric/Forge), where the
/// library list comes from merging with the parent version.
/// </summary>
public sealed class InstallService
{
    private readonly LauncherSettings _settings;
    private readonly GamePaths _paths;
    private readonly ApiClient _api;

    private static readonly JsonSerializerOptions JsonOpts = new() { PropertyNameCaseInsensitive = true };

    public InstallService(LauncherSettings settings, GamePaths paths, ApiClient api)
    {
        _settings = settings;
        _paths = paths;
        _api = api;
    }

    public GamePaths Paths => _paths;

    // ------------------------------------------------------------- planning ---

    /// <summary>Vanilla path: fetch the version json from Mojang, then plan.</summary>
    public async Task<(InstallPlan Plan, VersionDetail Detail)> BuildPlanAsync(
        string versionId, string versionJsonUrl, IProgress<string>? log, CancellationToken ct)
    {
        log?.Report($"获取 {versionId} 的版本元数据");
        string json = await _api.GetVersionJsonAsync(versionId, versionJsonUrl, ct).ConfigureAwait(false);

        var detail = JsonSerializer.Deserialize<VersionDetail>(json, JsonOpts)
                     ?? throw new InvalidOperationException(
                         "这个版本的信息文件读不出来，可能上次下载断掉了。删掉它重装一次就行。");

        Directory.CreateDirectory(_paths.VersionDir(versionId));
        await File.WriteAllTextAsync(_paths.VersionJson(versionId), json, ct).ConfigureAwait(false);

        // A modded profile listed in the manifest (rare) still resolves locally.
        if (!string.IsNullOrEmpty(detail.InheritsFrom))
            return await BuildPlanFromLocalAsync(versionId, log, ct).ConfigureAwait(false);

        var plan = new InstallPlan { VersionId = versionId };
        AddClientJar(plan, detail, versionId);
        AddLibraries(plan, detail, versionId, log);

        if (detail.AssetIndex is { Url: not null } index)
            await AddAssetsAsync(plan, index, log, ct).ConfigureAwait(false);

        if (detail.Logging?.Client?.File is { Url: not null } logFile)
        {
            string dest = Path.Combine(_paths.AssetsDir, "log_configs", logFile.Id ?? "client");
            plan.Files.Add(new NativeCore.DownloadItem(logFile.Url, dest, logFile.Size, logFile.Sha1 ?? ""));
        }

        FinishPlan(plan, log);
        return (plan, detail);
    }

    /// <summary>Modded path: the profile json already exists on disk (Fabric/Forge).</summary>
    public async Task<(InstallPlan Plan, VersionDetail Detail)> BuildPlanFromLocalAsync(
        string versionId, IProgress<string>? log, CancellationToken ct)
    {
        var resolved = await VersionResolver.ResolveAsync(versionId, _paths, log, ct).ConfigureAwait(false);
        var detail = resolved.Detail;

        var plan = new InstallPlan { VersionId = versionId };

        // The client jar always comes from the vanilla parent.
        AddClientJar(plan, detail, resolved.JarVersionId);
        AddLibraries(plan, detail, versionId, log);

        if (detail.AssetIndex is { Url: not null } index)
            await AddAssetsAsync(plan, index, log, ct).ConfigureAwait(false);

        if (detail.Logging?.Client?.File is { Url: not null } logFile)
        {
            string dest = Path.Combine(_paths.AssetsDir, "log_configs", logFile.Id ?? "client");
            plan.Files.Add(new NativeCore.DownloadItem(logFile.Url, dest, logFile.Size, logFile.Sha1 ?? ""));
        }

        FinishPlan(plan, log);
        return (plan, detail);
    }

    private void AddClientJar(InstallPlan plan, VersionDetail detail, string jarVersionId)
    {
        if (detail.Downloads?.Client is not { Url: not null } client) return;
        plan.Files.Add(new NativeCore.DownloadItem(
            _api.ClientJarUrl(jarVersionId, client.Url),
            _paths.VersionJar(jarVersionId),
            client.Size,
            client.Sha1 ?? ""));
    }

    private void AddLibraries(InstallPlan plan, VersionDetail detail, string versionId, IProgress<string>? log)
    {
        string nativesDir = _paths.VersionNatives(versionId);
        var nativesZips = new List<string>();
        int skipped = 0;

        foreach (var lib in detail.Libraries)
        {
            if (!IsAllowed(lib.Rules)) continue;

            // A null/empty name is a broken entry in somebody's profile json; skipping
            // it beats throwing NullReferenceException halfway through a launch.
            if (string.IsNullOrWhiteSpace(lib.Name))
            {
                log?.Report("跳过 1 个没有 name 的库条目（档案 json 有问题）");
                continue;
            }

            var artifact = lib.Downloads?.Artifact;
            if (artifact?.Url != null && !string.IsNullOrEmpty(artifact.Path))
            {
                if (!NativesArchMatches(artifact.Path)) { skipped++; continue; }
                plan.Files.Add(new NativeCore.DownloadItem(
                    _api.Rewrite(artifact.Url),
                    _paths.LibraryFile(artifact.Path),
                    artifact.Size,
                    artifact.Sha1 ?? ""));
            }
            else if (artifact == null && lib.Name.Contains(':'))
            {
                // Fabric and pre-1.13 vanilla omit the downloads block; derive the path
                // from the maven coordinates and the repository declared on the entry.
                string baseUrl = string.IsNullOrWhiteSpace(lib.Url) ? "https://libraries.minecraft.net/" : lib.Url;

                // Natives-only containers ("*platform": lwjgl-platform, jinput-platform,
                // twitch-platform) ship no usable main jar, and Mojang deleted some of
                // them from libraries.minecraft.net (lwjgl-platform 2.9.1,
                // jinput-platform 2.0.5 -> HTTP 404). The official launcher only takes
                // their natives classifiers, so skip the main artifact for those.
                string artifactId = lib.Name.Split(':')[1];
                bool nativesOnly = artifactId.EndsWith("platform", StringComparison.OrdinalIgnoreCase) ||
                                   artifactId.EndsWith("natives", StringComparison.OrdinalIgnoreCase);

                if (!nativesOnly)
                {
                    var (path, url) = MavenFromName(lib.Name, baseUrl);
                    if (path != null && url != null && NativesArchMatches(path))
                        AddOldFormatLibrary(plan, url, path);
                }
            }

            string? classifier = null;
            if (lib.Natives != null && lib.Natives.TryGetValue("windows", out var raw))
                classifier = raw.Replace("${arch}", Environment.Is64BitProcess ? "64" : "32");

            if (classifier != null && lib.Downloads?.Classifiers != null &&
                lib.Downloads.Classifiers.TryGetValue(classifier, out var nat) && nat.Url != null)
            {
                string zipName = SanitizeName(lib.Name) + "-" + classifier + ".jar";
                string zipPath = Path.Combine(nativesDir, zipName);
                plan.Files.Add(new NativeCore.DownloadItem(_api.Rewrite(nat.Url), zipPath, nat.Size, nat.Sha1 ?? ""));
                nativesZips.Add(zipPath);
            }
            else if (classifier != null && lib.Downloads == null && lib.Name.Contains(':'))
            {
                // Old-format libraries carry no downloads block at all: build the
                // natives classifier URL from the coordinates the same way.
                string baseUrl2 = string.IsNullOrWhiteSpace(lib.Url) ? "https://libraries.minecraft.net/" : lib.Url;
                string withClassifier = lib.Name + ":" + classifier;
                var (cpath, curl) = MavenFromName(withClassifier, baseUrl2);
                if (cpath != null && curl != null && NativesArchMatches(cpath))
                {
                    string zipName = SanitizeName(lib.Name) + "-" + classifier + ".jar";
                    string zipPath = Path.Combine(nativesDir, zipName);
                    AddOldFormatLibrary(plan, curl, cpath, zipPath);
                    nativesZips.Add(zipPath);
                }
            }
        }

        foreach (var zip in nativesZips)
            plan.Extracts.Add((zip, nativesDir));

        if (skipped > 0)
            log?.Report($"  跳过 {skipped} 个其他架构的 natives 包");
    }

    private void AddOldFormatLibrary(InstallPlan plan, string url, string mavenPath, string? destOverride = null)
    {
        string dest = destOverride ?? _paths.LibraryFile(mavenPath);

        // Always listed, even when the file is already on disk. Skipping here made the
        // plan incomplete, so `--verify` reported fewer files than were installed and
        // silently ignored exactly these libraries (Fabric/Quilt ship 8 like this).
        // ExecuteAsync filters out the ones that are already present.
        plan.Files.Add(new NativeCore.DownloadItem(_api.Rewrite(url), dest, -1, ""));
    }

    /// <summary>
    /// True when a "no known size" item is already on disk. Only used at download time:
    /// without a size or hash we cannot tell a good file from a truncated one, so
    /// presence plus non-zero length is the best available signal.
    /// </summary>
    private static bool AlreadyPresent(NativeCore.DownloadItem item)
    {
        if (item.ExpectedSize >= 0) return false;

        try
        {
            var info = new FileInfo(item.Destination);
            return info.Exists && info.Length > 0;
        }
        catch
        {
            return false;
        }
    }

    private void FinishPlan(InstallPlan plan, IProgress<string>? log)
    {
        plan.TotalBytes = plan.Files.Sum(f => Math.Max(0, f.ExpectedSize));
        log?.Report($"计划就绪：{plan.Files.Count} 个文件，{plan.TotalBytes / 1024.0 / 1024.0:F1} MB");
    }

    private async Task AddAssetsAsync(InstallPlan plan, AssetIndexRef index, IProgress<string>? log, CancellationToken ct)
    {
        string localIndex = _paths.AssetIndex(index.Id);
        string text;

        bool cached = File.Exists(localIndex) && new FileInfo(localIndex).Length > 0;
        if (cached)
        {
            text = await File.ReadAllTextAsync(localIndex, ct).ConfigureAwait(false);
        }
        else
        {
            log?.Report($"获取资源索引 {index.Id}");
            text = await _api.GetStringAsync(index.Url!, ct).ConfigureAwait(false);
            Directory.CreateDirectory(_paths.AssetIndexesDir);
            await File.WriteAllTextAsync(localIndex, text, ct).ConfigureAwait(false);
        }

        AssetIndexFile? assets;
        try
        {
            assets = JsonSerializer.Deserialize<AssetIndexFile>(text, JsonOpts);
        }
        catch (JsonException)
        {
            // Cached copy was truncated - refetch once from the network.
            text = await _api.GetStringAsync(index.Url!, ct).ConfigureAwait(false);
            await File.WriteAllTextAsync(localIndex, text, ct).ConfigureAwait(false);
            assets = JsonSerializer.Deserialize<AssetIndexFile>(text, JsonOpts);
        }
        if (assets == null) return;

        // An index maps *names* to objects, and several names can point at the same
        // hash (1.7.10: 12 dupes, 1.20.1: 23). Without this guard the same file lands
        // in the plan twice, which means two threads writing one destination, an
        // inflated file count, and duplicate rows in the verify report.
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var (_, obj) in assets.Objects)
        {
            if (obj.Hash.Length < 2) continue;
            if (!seen.Add(obj.Hash)) continue;

            string url = $"https://resources.download.minecraft.net/{obj.Hash[..2]}/{obj.Hash}";
            plan.Files.Add(new NativeCore.DownloadItem(
                _api.Rewrite(url), _paths.AssetObject(obj.Hash), obj.Size, obj.Hash));
        }
    }

    // ------------------------------------------------------------- execution ---

    /// <summary>
    /// Runs the download plan in two waves (game payload first, assets second) so
    /// the player sees a meaningful stage label instead of one giant percentage.
    /// </summary>
    public async Task<int> ExecuteAsync(InstallPlan plan, IProgress<InstallProgress>? progress,
                                        IProgress<string>? log, CancellationToken ct)
    {
        int threads = Math.Clamp(_settings.DownloadThreads, 1, 64);
        int totalBytes = 0;

        var core = plan.Files
            .Where(f => !f.Destination.StartsWith(_paths.AssetObjectsDir, StringComparison.OrdinalIgnoreCase))
            .Where(f => !AlreadyPresent(f))
            .ToList();
        var assets = plan.Files
            .Where(f => f.Destination.StartsWith(_paths.AssetObjectsDir, StringComparison.OrdinalIgnoreCase))
            .Where(f => !AlreadyPresent(f))
            .ToList();

        async Task RunWave(string stage, List<NativeCore.DownloadItem> batch)
        {
            if (batch.Count == 0) return;
            log?.Report($"{stage}：{batch.Count} 个文件（{batch.Sum(f => Math.Max(0, f.ExpectedSize)) / 1024.0 / 1024.0:F1} MB）");

            // Breadcrumb for native crashes: the aggregate progress callback is the only
            // signal we get from ECL.Native, so record it. A crash then leaves a trace
            // whose last line says roughly where it died.
            string breadcrumb = Path.Combine(_paths.LogsDir, "download-progress.log");

            var bridge = progress == null ? null : new Progress<DownloadProgress>(d =>
            {
                progress.Report(new InstallProgress(stage, d));

                if (d.FilesDone % 100 == 0)
                {
                    try
                    {
                        Directory.CreateDirectory(_paths.LogsDir);
                        File.AppendAllText(breadcrumb,
                            $"{DateTime.Now:HH:mm:ss.fff} {stage} {d.FilesDone}/{d.FilesTotal} " +
                            $"{d.BytesDone / 1024 / 1024} MB{Environment.NewLine}");
                    }
                    catch
                    {
                        // diagnostics only
                    }
                }
            });

            // Safe mode (or a missing native DLL): same files, plain HttpClient. Kept as
            // a first-class path so "下载就崩" can be bisected instead of guessed at.
            if (_settings.SafeDownload || !NativeCore.IsAvailable(out _))
            {
                log?.Report($"{stage}：使用内置 HTTP 通道（安全模式），并发 {threads}");
                var managed = await ManagedDownloader.DownloadAsync(
                    batch, threads, bridge, ct,
                    Path.Combine(_paths.LogsDir, "download-trace.log"),
                    log == null ? null : new Action<string>(log.Report)).ConfigureAwait(false);

                totalBytes += (int)Math.Min(int.MaxValue, batch.Sum(f => Math.Max(0, f.ExpectedSize)));

                if (managed.Failed > 0)
                    throw new IOException($"{stage}失败：{managed.Failed} 个文件没下下来" +
                                          $"（第一个错误：{managed.FirstError}）。详见 logs\\download-trace.log");

                log?.Report($"{stage}：完成，新下载 {managed.Downloaded} 个，跳过 {managed.Skipped} 个");
                return;
            }

            var result = await NativeCore.DownloadAsync(batch, threads, bridge, ct).ConfigureAwait(false);
            totalBytes += (int)Math.Min(int.MaxValue, batch.Sum(f => Math.Max(0, f.ExpectedSize)));

            if (result.Cancelled) throw new OperationCanceledException("安装已取消。");
            if (!result.Success) throw new IOException($"{stage}失败：{result.Error}");

            log?.Report($"{stage}：完成，用时 {result.Elapsed.TotalSeconds:F1}s " +
                        $"（{result.MegaBytesPerSecond:F2} MB/s，{result.CacheHits} 个文件已存在）");
        }

        await RunWave("下载游戏文件", core).ConfigureAwait(false);
        await RunWave("下载资源文件", assets).ConfigureAwait(false);

        foreach (var (zip, dest) in plan.Extracts)
        {
            ct.ThrowIfCancellationRequested();
            if (!File.Exists(zip)) continue;

            // Natives land under versions/<id>/natives/<arch>, the first path to hit
            // MAX_PATH on a deep data root. Fail with a named path instead of a bare
            // IOException halfway through extraction.
            PathGuard.EnsureUsable(dest, "natives 目录");

            Directory.CreateDirectory(dest);
            log?.Report($"解压 {Path.GetFileName(zip)}");
            var (ok, err) = await NativeCore.UnzipAsync(zip, dest, ct).ConfigureAwait(false);
            if (!ok) log?.Report($"  natives 解压警告：{err}");
        }

        return totalBytes;
    }

    public void MarkInstalled(string versionId, string? baseVersionId = null)
    {
        if (!string.IsNullOrEmpty(baseVersionId) && baseVersionId != versionId)
            EnsureVersionJar(versionId, baseVersionId);

        Directory.CreateDirectory(_paths.VersionDir(versionId));
        File.WriteAllText(Path.Combine(_paths.VersionDir(versionId), ".installed"), DateTime.Now.ToString("O"));
    }

    /// <summary>
    /// Modded profiles need a client jar under their own name. Forge's ignore list
    /// contains <c>${version_name}.jar</c> — i.e. "&lt;profile&gt;.jar". If the classpath
    /// points at the vanilla "1.20.1.jar" instead, that jar is picked up as an
    /// automatic module and the module layer dies with "package net.minecraft.server
    /// exported twice". Copying the vanilla jar under the profile's name is exactly
    /// the layout the official launcher ends up with.
    /// </summary>
    public string EnsureVersionJar(string versionId, string baseVersionId)
    {
        string target = _paths.VersionJar(versionId);
        if (File.Exists(target) && new FileInfo(target).Length > 0) return target;

        string source = _paths.VersionJar(baseVersionId);
        if (versionId == baseVersionId || !File.Exists(source)) return source;

        try
        {
            Directory.CreateDirectory(_paths.VersionDir(versionId));
            File.Copy(source, target, overwrite: true);
            return target;
        }
        catch
        {
            return source;
        }
    }

    public bool IsInstalled(string versionId) =>
        File.Exists(_paths.VersionJar(versionId)) &&
        File.Exists(Path.Combine(_paths.VersionDir(versionId), ".installed"));

    // ----------------------------------------------------------------- rules ---

    public static bool IsAllowed(List<RuleNode>? rules)
    {
        if (rules == null || rules.Count == 0) return true;

        bool allowed = false;
        foreach (var rule in rules)
        {
            if (!RuleMatches(rule)) continue;
            allowed = string.Equals(rule.Action, "allow", StringComparison.OrdinalIgnoreCase);
        }
        return allowed;
    }

    /// <summary>
    /// Mojang lists the arm64 and x86 native variants of e.g. LWJGL as "allow on
    /// windows" without narrowing the arch, so rules alone would fetch all three.
    /// Filtering by the file name suffix keeps the download and the classpath at
    /// one variant per library.
    /// </summary>
    public static bool NativesArchMatches(string? artifactPath)
    {
        if (string.IsNullOrEmpty(artifactPath)) return true;
        if (artifactPath.Contains("-natives-windows-arm64", StringComparison.OrdinalIgnoreCase))
            return System.Runtime.InteropServices.RuntimeInformation.ProcessArchitecture
                   == System.Runtime.InteropServices.Architecture.Arm64;
        if (artifactPath.Contains("-natives-windows-x86", StringComparison.OrdinalIgnoreCase))
            return !Environment.Is64BitProcess;
        return true;
    }

    private static bool RuleMatches(RuleNode rule)
    {
        if (rule.Os != null)
        {
            if (rule.Os.Name != null && !rule.Os.Name.Equals("windows", StringComparison.OrdinalIgnoreCase))
                return false;
            if (rule.Os.Arch != null)
            {
                string want = rule.Os.Arch;
                bool ok = want switch
                {
                    "x86" => !Environment.Is64BitProcess,
                    "x86_64" or "amd64" => Environment.Is64BitProcess,
                    "arm64" or "aarch64" => System.Runtime.InteropServices.RuntimeInformation.ProcessArchitecture
                        == System.Runtime.InteropServices.Architecture.Arm64,
                    _ => true
                };
                if (!ok) return false;
            }
        }

        if (rule.Features != null)
        {
            // We never enable optional features (demo mode, custom resolution,
            // quick play), so any rule that requires one does not apply.
            foreach (var (_, required) in rule.Features)
                if (required) return false;
        }
        return true;
    }

    /// <summary>Builds a maven path from "group:artifact:version[:classifier]".</summary>
    public static (string? Path, string? Url) MavenFromName(string name, string baseUrl = "https://libraries.minecraft.net/")
    {
        var parts = name.Split(':');
        if (parts.Length < 3) return (null, null);
        string group = parts[0].Replace('.', '/');
        string artifact = parts[1];
        string version = parts[2];
        string? classifier = parts.Length > 3 ? parts[3] : null;
        string file = classifier == null
            ? $"{artifact}-{version}.jar"
            : $"{artifact}-{version}-{classifier}.jar";
        string path = $"{group}/{artifact}/{version}/{file}";
        return (path, baseUrl.TrimEnd('/') + "/" + path);
    }

    private static string SanitizeName(string name) =>
        string.Concat(name.Select(c => char.IsLetterOrDigit(c) || c == '.' || c == '-' || c == '_' ? c : '_'));
}
