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
            return $"整合包要 Minecraft {index.Minecraft}，当前档案是 {profileMcVersion}";

        if (index.Loader != "vanilla" &&
            !profileLoader.Equals(index.Loader, StringComparison.OrdinalIgnoreCase))
            return $"整合包要 {index.Loader}，当前档案不是 {index.Loader}";

        return null;
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
                byte[] bytes = await _http.GetByteArrayAsync(file.Url, ct).ConfigureAwait(false);

                if (file.Sha1.Length > 0)
                {
                    string actual = Convert.ToHexString(SHA1.HashData(bytes)).ToLowerInvariant();
                    if (!string.Equals(actual, file.Sha1, StringComparison.OrdinalIgnoreCase))
                    {
                        problems.Add($"哈希不符：{file.Path}");
                        failed++;
                        continue;
                    }
                }

                await File.WriteAllBytesAsync(target, bytes, ct).ConfigureAwait(false);
                downloaded++;
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
            var marker = new
            {
                name = index.Name,
                version = index.Version,
                loader = index.Loader,
                minecraft = index.Minecraft,
                source = Path.GetFileName(mrpackPath),
                installedAt = DateTime.Now.ToString("s")
            };
            await File.WriteAllTextAsync(Path.Combine(gameDir, ".ecl-modpack.json"),
                JsonSerializer.Serialize(marker, new JsonSerializerOptions { WriteIndented = true }), ct)
                .ConfigureAwait(false);
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

    private static string Sha1Of(string path)
    {
        using var stream = File.OpenRead(path);
        return Convert.ToHexString(SHA1.HashData(stream)).ToLowerInvariant();
    }
}
