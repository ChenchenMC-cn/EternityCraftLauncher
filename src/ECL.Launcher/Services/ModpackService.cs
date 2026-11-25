// SPDX-License-Identifier: MPL-2.0
// Copyright (c) 2026 ChenchenMC <https://github.com/ChenchenMC-cn/EternityCraftLauncher>
using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace ECL.Services;

/// <summary>One file the pack wants on disk, relative to the game folder.</summary>
public sealed record ModpackFile(string Path, string? Url, string Sha1, long Size, bool ClientRelevant);

/// <summary>The parsed <c>modrinth.index.json</c> of a <c>.mrpack</c>.</summary>
public sealed record ModpackIndex(
    string Name,
    string Version,
    string Summary,
    string Minecraft,
    string? FabricLoader,
    string? QuiltLoader,
    string? ForgeVersion,
    IReadOnlyList<ModpackFile> Files)
{
    /// <summary>"fabric" / "quilt" / "forge" / "vanilla" — what the profile needs.</summary>
    public string Loader =>
        !string.IsNullOrWhiteSpace(FabricLoader) ? "fabric" :
        !string.IsNullOrWhiteSpace(QuiltLoader) ? "quilt" :
        !string.IsNullOrWhiteSpace(ForgeVersion) ? "forge" : "vanilla";
}

/// <summary>One file a marker remembers, so a later update can diff without the old pack.</summary>
public sealed record ModpackFileRecord(string Path, string Sha1);

/// <summary>
/// What was installed, written to <c>&lt;game&gt;/.ecl-modpack.json</c>.
///
/// <para>The file list is the point: a pack has no project id in its index, so "is there an
/// update?" cannot be answered by asking Modrinth. The next pack file is diffed against
/// this list instead, which is exact and works offline.</para>
/// </summary>
public sealed record ModpackMarker(
    string Name,
    string Version,
    string Loader,
    string Minecraft,
    string Source,
    string InstalledAt,
    IReadOnlyList<ModpackFileRecord> Files);

/// <summary>What changed between the installed pack and a newer file.</summary>
public sealed record ModpackDiff(
    IReadOnlyList<ModpackFile> Added,
    IReadOnlyList<ModpackFile> Changed,
    IReadOnlyList<string> Removed)
{
    public bool IsEmpty => Added.Count == 0 && Changed.Count == 0 && Removed.Count == 0;
    public int Total => Added.Count + Changed.Count + Removed.Count;
}

/// <summary>
/// Installer for Modrinth <c>.mrpack</c> files.
///
/// <para>The format is a zip with <c>modrinth.index.json</c> plus <c>overrides/</c>. The
/// index lists every file with a URL and a SHA-1, so installation is "create the profile,
/// fetch the list, drop the overrides in" — no pack-specific logic.</para>
///
/// <para>Forge packs are rejected for now: assembling a Forge profile needs the official
/// installer to run under a real JVM, which is a separate flow from this one. Fabric and
/// Quilt ship the same profile layout the launcher already builds.</para>
/// </summary>
public sealed class ModpackService
{
    private readonly GamePaths _paths;
    private readonly HttpClient _http = new() { Timeout = TimeSpan.FromMinutes(5) };

    public ModpackService(GamePaths paths)
    {
        _paths = paths;
        _http.DefaultRequestHeaders.UserAgent.ParseAdd("ECL/1.0.6 (EternityCraftLauncher)");
    }

    public static ModpackIndex? ReadIndex(string mrpackPath, out string error)
    {
        error = "";
        try
        {
            using var zip = ZipFile.OpenRead(mrpackPath);

            var entry = zip.GetEntry("modrinth.index.json");
            if (entry == null)
            {
                error = "不是有效的整合包：缺少 modrinth.index.json";
                return null;
            }

            using var stream = entry.Open();
            using var doc = JsonDocument.Parse(stream);
            var root = doc.RootElement;

            string name = root.TryGetProperty("name", out var n) ? n.GetString() ?? "" : "";
            string version = root.TryGetProperty("versionId", out var v) ? v.GetString() ?? "" : "";
            string summary = root.TryGetProperty("summary", out var s) ? s.GetString() ?? "" : "";

            string mc = "";
            string? fabric = null, quilt = null, forge = null;

            if (root.TryGetProperty("dependencies", out var deps) && deps.ValueKind == JsonValueKind.Object)
            {
                foreach (var d in deps.EnumerateObject())
                {
                    string? value = d.Value.ValueKind == JsonValueKind.String ? d.Value.GetString() : null;
                    switch (d.Name.ToLowerInvariant())
                    {
                        case "minecraft": mc = value ?? ""; break;
                        case "fabric-loader": fabric = value; break;
                        case "quilt-loader": quilt = value; break;
                        case "forge": forge = value; break;
                    }
                }
            }

            if (string.IsNullOrWhiteSpace(mc))
            {
                error = "整合包没有声明 Minecraft 版本";
                return null;
            }

            var files = new List<ModpackFile>();
            if (root.TryGetProperty("files", out var arr) && arr.ValueKind == JsonValueKind.Array)
            {
                foreach (var f in arr.EnumerateArray())
                {
                    string path = f.TryGetProperty("path", out var p) ? p.GetString() ?? "" : "";
                    if (path.Length == 0) continue;

                    // env.client == "unsupported" means a server-only file.
                    bool clientRelevant = true;
                    if (f.TryGetProperty("env", out var env) && env.ValueKind == JsonValueKind.Object &&
                        env.TryGetProperty("client", out var cl))
                    {
                        string? c = cl.GetString();
                        if (string.Equals(c, "unsupported", StringComparison.OrdinalIgnoreCase))
                            clientRelevant = false;
                    }

                    string? url = null;
                    string sha1 = "";
                    long size = 0;

                    if (f.TryGetProperty("downloads", out var dl) && dl.ValueKind == JsonValueKind.Array)
                        foreach (var u in dl.EnumerateArray())
                            if (u.ValueKind == JsonValueKind.String) { url = u.GetString(); break; }

                    if (f.TryGetProperty("hashes", out var hashes) && hashes.ValueKind == JsonValueKind.Object &&
                        hashes.TryGetProperty("sha1", out var s1))
                        sha1 = s1.GetString() ?? "";

                    if (f.TryGetProperty("fileSize", out var fs) && fs.TryGetInt64(out long fsz))
                        size = fsz;

                    files.Add(new ModpackFile(path, url, sha1, size, clientRelevant));
                }
            }

            return new ModpackIndex(name, version, summary, mc, fabric, quilt, forge, files);
        }
        catch (Exception ex)
        {
            error = ex.Message;
            return null;
        }
    }

    /// <summary>
    /// Why this pack cannot go into that profile, or null when it fits.
    ///
    /// <para>Installing a pack means "add its files to a profile that already has the right
    /// loader". Building the profile is the installer's job (see InstallService and the
    /// loader installers) — duplicating that pipeline here would be a second, subtly
    /// different copy of it.</para>
    /// </summary>
    public static string? MismatchReason(ModpackIndex index, string profileId, string profileMcVersion, string profileLoader)
    {
        if (!string.Equals(index.Minecraft, profileMcVersion, StringComparison.OrdinalIgnoreCase))
            return $"整合包要 Minecraft {index.Minecraft}，当前档案是 {profileMcVersion}。" +
                   $"请先在「安装」页装一个 {index.Minecraft} {(index.Loader == "vanilla" ? "" : index.Loader + " ")}版本。";

        if (index.Loader == "vanilla")
            return null;    // a vanilla pack drops jars into mods/ and simply does nothing

        if (profileLoader.Equals(index.Loader, StringComparison.OrdinalIgnoreCase))
        {
            // Forge packs name an exact build; the profile may carry a different one. That is
            // usually fine (mods target a range), so it is a warning, not a rejection.
            return null;
        }

        return index.Loader switch
        {
            "forge" => $"这个整合包要 Forge，请先在「安装」页装一个 {index.Minecraft} 的 Forge 版本，" +
                       $"装好后再回来导入（Forge 档案必须由官方安装器装配）。",
            "fabric" => $"这个整合包要 Fabric，当前档案是 {profileLoader}。请先装一个 {index.Minecraft} Fabric 版本。",
            "quilt" => $"这个整合包要 Quilt，当前档案是 {profileLoader}。请先装一个 {index.Minecraft} Quilt 版本。",
            _ => $"整合包要 {index.Loader}，当前档案是 {profileLoader}。"
        };
    }

    /// <summary>Non-blocking note when a Forge pack names a different build than the profile.</summary>
    public static string? ForgeVersionNote(ModpackIndex index, string profileId)
    {
        if (index.Loader != "forge" || string.IsNullOrWhiteSpace(index.ForgeVersion)) return null;

        // Profile ids look like "1.20.1-forge-47.3.39".
        int marker = profileId.IndexOf("-forge-", StringComparison.OrdinalIgnoreCase);
        if (marker < 0) return null;

        string profileForge = profileId[(marker + "-forge-".Length)..];
        return profileForge.Equals(index.ForgeVersion, StringComparison.OrdinalIgnoreCase)
            ? null
            : $"整合包标的是 Forge {index.ForgeVersion}，当前档案是 {profileForge}，通常仍可运行。";
    }

    /// <summary>What one install produced, for the log and the UI.</summary>
    public sealed record ModpackInstallResult(string VersionId, string Name, string Version,
        int Downloaded, int Skipped, int Failed, IReadOnlyList<string> Problems);

    /// <summary>
    /// Materialises a pack's files into an existing profile: downloads the index's file
    /// list (SHA-1 verified, already-present files skipped) and merges the overrides.
    /// </summary>
    public async Task<ModpackInstallResult> InstallFilesAsync(string mrpackPath, string versionId,
        IProgress<string>? log, IProgress<(long Done, long Total)>? progress, CancellationToken ct = default)
    {
        var index = ReadIndex(mrpackPath, out string readError)
                    ?? throw new InvalidOperationException(readError);

        log?.Report($"整合包：{index.Name} {index.Version}（Minecraft {index.Minecraft} / {index.Loader}）");

        string gameDir = _paths.GameDir(versionId);
        Directory.CreateDirectory(gameDir);

        // Leftovers from an interrupted run: a stale .part would never be resumed, so drop it.
        try
        {
            foreach (string stale in Directory.EnumerateFiles(gameDir, "*.part", SearchOption.AllDirectories))
                File.Delete(stale);
        }
        catch { /* best effort */ }

        var problems = new List<string>();
        int downloaded = 0, skipped = 0, failed = 0, done = 0;

        var clientFiles = index.Files.Where(f => f.ClientRelevant).ToList();
        foreach (var file in clientFiles)
        {
            ct.ThrowIfCancellationRequested();
            done++;
            progress?.Report((done, clientFiles.Count));

            string target = Path.Combine(gameDir, file.Path.Replace('/', Path.DirectorySeparatorChar));
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);

            if (File.Exists(target) && (file.Sha1.Length == 0 || Sha1Of(target) == file.Sha1))
            {
                skipped++;
                continue;
            }

            if (string.IsNullOrWhiteSpace(file.Url))
            {
                problems.Add($"没有下载地址：{file.Path}");
                failed++;
                continue;
            }

            try
            {
                // Streamed, not GetByteArrayAsync: a throttled CDN would otherwise look like
                // a hang (the whole request waits up to the 5-minute timeout) and hold the
                // entire jar in memory. Streaming also lets a stall fail one file instead of
                // the whole pack.
                string? error = await DownloadFileAsync(file, target, ct).ConfigureAwait(false);
                if (error != null)
                {
                    problems.Add($"{file.Path}：{error}");
                    failed++;
                    continue;
                }

                downloaded++;
                if (done % 10 == 0) log?.Report($"  已下载 {done}/{clientFiles.Count}");
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex)
            {
                problems.Add($"{file.Path}：{ex.Message}");
                failed++;
            }
        }

        ExtractOverrides(mrpackPath, gameDir, log);

        try
        {
            var marker = new ModpackMarker(
                index.Name,
                index.Version,
                index.Loader,
                index.Minecraft,
                Path.GetFileName(mrpackPath),
                DateTime.Now.ToString("s"),
                clientFiles.Select(f => new ModpackFileRecord(f.Path, f.Sha1)).ToList());

            await File.WriteAllTextAsync(Path.Combine(gameDir, ".ecl-modpack.json"),
                JsonSerializer.Serialize(marker, MarkerJson), ct).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            log?.Report("写入整合包标记失败（不影响使用）：" + ex.Message);
        }

        log?.Report($"完成：{index.Name} → {versionId}（下载 {downloaded} / 跳过 {skipped} / 失败 {failed}）");
        return new ModpackInstallResult(versionId, index.Name, index.Version, downloaded, skipped, failed, problems);
    }

    /// <summary>
    /// Merges <c>overrides/</c> and <c>client-overrides/</c> into the game folder.
    /// client-overrides wins, which is what the pack format specifies.
    /// </summary>
    private static void ExtractOverrides(string mrpackPath, string gameDir, IProgress<string>? log)
    {
        using var zip = ZipFile.OpenRead(mrpackPath);

        foreach (string prefix in new[] { "overrides/", "client-overrides/" })
        {
            foreach (var entry in zip.Entries.Where(e => e.FullName.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)))
            {
                if (entry.FullName.EndsWith("/")) continue;

                string relative = entry.FullName[prefix.Length..];
                if (relative.Length == 0) continue;

                string target = Path.Combine(gameDir, relative.Replace('/', Path.DirectorySeparatorChar));
                Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                entry.ExtractToFile(target, overwrite: true);
            }
        }

        log?.Report("已解压 overrides");
    }

    /// <summary>How long a single read may go without data before the file is given up on.</summary>
    private static readonly TimeSpan StallTimeout = TimeSpan.FromSeconds(25);

    /// <summary>
    /// Downloads one file to a temp path, hashing while writing, and only moves it into
    /// place when the hash matches. A half-downloaded jar must never end up in mods/.
    /// Returns an error string, or null on success.
    /// </summary>
    private async Task<string?> DownloadFileAsync(ModpackFile file, string target, CancellationToken ct)
    {
        string temp = target + ".part";

        try
        {
            using var response = await _http
                .GetAsync(file.Url, HttpCompletionOption.ResponseHeadersRead, ct)
                .ConfigureAwait(false);

            if (!response.IsSuccessStatusCode)
                return $"HTTP {(int)response.StatusCode}";

            string actual;
            // Scoped so the write handle is closed before File.Move — `await using var` at
            // method scope keeps it open until the end and Move fails with "file in use".
            {
                await using var source = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
                await using var sink = new FileStream(temp, FileMode.Create, FileAccess.Write, FileShare.None, 81920, true);

                using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA1);
                byte[] buffer = new byte[81920];

                while (true)
                {
                    using var readCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                    readCts.CancelAfter(StallTimeout);

                    int read;
                    try
                    {
                        read = await source.ReadAsync(buffer, readCts.Token).ConfigureAwait(false);
                    }
                    catch (OperationCanceledException) when (!ct.IsCancellationRequested)
                    {
                        return $"下载停滞超过 {StallTimeout.TotalSeconds:F0} 秒，已放弃这个文件";
                    }

                    if (read == 0) break;

                    hash.AppendData(buffer, 0, read);
                    await sink.WriteAsync(buffer.AsMemory(0, read), ct).ConfigureAwait(false);
                }

                actual = Convert.ToHexString(hash.GetHashAndReset()).ToLowerInvariant();
            }
            if (file.Sha1.Length > 0 && !string.Equals(actual, file.Sha1, StringComparison.OrdinalIgnoreCase))
                return "哈希不符";

            File.Move(temp, target, overwrite: true);
            return null;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            return ex.Message;
        }
        finally
        {
            try { if (File.Exists(temp)) File.Delete(temp); } catch { }
        }
    }

    private static readonly JsonSerializerOptions MarkerJson = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    /// <summary>Reads the marker written at install time; null when this profile has no pack.</summary>
    public ModpackMarker? ReadMarker(string versionId)
    {
        string path = Path.Combine(_paths.GameDir(versionId), ".ecl-modpack.json");
        if (!File.Exists(path)) return null;

        try
        {
            return JsonSerializer.Deserialize<ModpackMarker>(File.ReadAllText(path), MarkerJson);
        }
        catch
        {
            return null;   // a hand-edited or truncated marker means "not a managed pack"
        }
    }

    /// <summary>
    /// Compares a newer pack index against the installed file list.
    ///
    /// <para>Only paths are compared by hash: a file whose SHA-1 changed is a new build of
    /// the same mod, which must be re-downloaded; a path that vanished is a mod the pack
    /// dropped. Client-irrelevant files are ignored on both sides.</para>
    /// </summary>
    public static ModpackDiff Diff(ModpackMarker marker, ModpackIndex next)
    {
        var installed = marker.Files
            .GroupBy(f => f.Path, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.First().Sha1, StringComparer.OrdinalIgnoreCase);

        var incoming = next.Files
            .Where(f => f.ClientRelevant)
            .GroupBy(f => f.Path, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.First(), StringComparer.OrdinalIgnoreCase);

        var added = new List<ModpackFile>();
        var changed = new List<ModpackFile>();

        foreach (var (path, file) in incoming)
        {
            if (!installed.TryGetValue(path, out string? old))
            {
                added.Add(file);
                continue;
            }

            bool same = file.Sha1.Length > 0
                ? string.Equals(old, file.Sha1, StringComparison.OrdinalIgnoreCase)
                : true;    // no hash in the new pack: assume unchanged rather than re-download

            if (!same) changed.Add(file);
        }

        var removed = installed.Keys.Where(p => !incoming.ContainsKey(p)).ToList();
        return new ModpackDiff(added, changed, removed);
    }

    /// <summary>
    /// Applies a newer pack: downloads what changed, then removes what the pack dropped.
    /// Removed files go to <c>.ecl-trash</c> inside the game folder — a 300 MB pack should
    /// not lose files to a single click.
    /// </summary>
    public async Task<ModpackInstallResult> ApplyUpdateAsync(string mrpackPath, string versionId,
        IProgress<string>? log, IProgress<(long Done, long Total)>? progress, CancellationToken ct = default)
    {
        var index = ReadIndex(mrpackPath, out string error)
                    ?? throw new InvalidOperationException(error);

        var marker = ReadMarker(versionId);
        if (marker == null)
            throw new InvalidOperationException(
                "这个档案没有整合包记录（.ecl-modpack.json），只能当作首次安装处理。");

        var diff = Diff(marker, index);
        log?.Report($"对比：新增 {diff.Added.Count}，更新 {diff.Changed.Count}，移除 {diff.Removed.Count}");

        string gameDir = _paths.GameDir(versionId);
        string trash = Path.Combine(gameDir, ".ecl-trash");
        var problems = new List<string>();
        int done = 0, failed = 0;

        foreach (var file in diff.Added.Concat(diff.Changed))
        {
            ct.ThrowIfCancellationRequested();
            done++;
            progress?.Report((done, diff.Added.Count + diff.Changed.Count));

            string target = Path.Combine(gameDir, file.Path.Replace('/', Path.DirectorySeparatorChar));
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);

            if (string.IsNullOrWhiteSpace(file.Url))
            {
                problems.Add($"没有下载地址：{file.Path}");
                failed++;
                continue;
            }

            string? failure = await DownloadFileAsync(file, target, ct).ConfigureAwait(false);
            if (failure != null)
            {
                problems.Add($"{file.Path}：{failure}");
                failed++;
            }
        }

        foreach (string relative in diff.Removed)
        {
            string target = Path.Combine(gameDir, relative.Replace('/', Path.DirectorySeparatorChar));
            if (!File.Exists(target)) continue;

            try
            {
                Directory.CreateDirectory(trash);
                string name = $"{DateTime.Now:yyyyMMdd-HHmmss}-{Path.GetFileName(target)}";
                File.Move(target, Path.Combine(trash, name));
            }
            catch (Exception ex)
            {
                problems.Add($"移除 {relative} 失败：{ex.Message}");
            }
        }

        ExtractOverrides(mrpackPath, gameDir, log);

        var updated = marker with
        {
            Version = index.Version,
            Source = Path.GetFileName(mrpackPath),
            InstalledAt = DateTime.Now.ToString("s"),
            Files = index.Files.Where(f => f.ClientRelevant)
                        .Select(f => new ModpackFileRecord(f.Path, f.Sha1)).ToList()
        };

        await File.WriteAllTextAsync(Path.Combine(gameDir, ".ecl-modpack.json"),
            JsonSerializer.Serialize(updated, MarkerJson), ct).ConfigureAwait(false);

        log?.Report($"更新完成：{updated.Name} → {index.Version}");
        return new ModpackInstallResult(versionId, index.Name, index.Version,
            diff.Added.Count + diff.Changed.Count - failed, 0, failed, problems);
    }

    private static string Sha1Of(string path)
    {
        using var stream = File.OpenRead(path);
        return Convert.ToHexString(SHA1.HashData(stream)).ToLowerInvariant();
    }
}
