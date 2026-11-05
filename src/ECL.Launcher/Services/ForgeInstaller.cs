// SPDX-License-Identifier: MPL-2.0
// Copyright (c) 2026 ChenchenMC <https://github.com/ChenchenMC-cn/EternityCraftLauncher>
using System.Diagnostics;
using System.IO.Compression;
using System.IO;
using System.Net.Http;
using System.Text;
using System.Text.Json;

namespace ECL.Services;

/// <summary>
/// Installs Forge by driving the official installer in headless mode
/// (<c>--installClient</c>). Forge needs bytecode patching through its own
/// "processors" pipeline — reimplementing that would be a maintenance trap, and
/// the official installer is the reference implementation. ECL supplies the
/// directory layout (versions/ libraries/ assets/) it expects.
/// </summary>
/// <summary>
/// One Forge build. <see cref="Raw"/> is the maven version exactly as the metadata
/// lists it ("1.20.1-47.3.39", "1.7.10-10.13.4.1614-1.7.10"); <see cref="Display"/>
/// is the bare Forge number for UI and profile ids ("47.3.39", "10.13.4.1614").
/// The two differ per era, so both travel together.
/// </summary>
public sealed record ForgeBuild(string Raw, string Display);

public sealed class ForgeInstaller
{
    /// <summary>
    /// OptiFine's transformation service strips <c>ResourceLocation.parse</c> from
    /// the runtime, and Forge 47.4+ calls exactly that method from
    /// <c>ObjectHolderRegistry.scanTarget</c> as soon as any mod is present. The
    /// result is <c>NoSuchMethodError</c> during mod loading. 47.0–47.3 are fine,
    /// so "Forge + OptiFine" has to stay on those builds.
    /// </summary>
    public static bool IsOptiFineCompatible(string? forgeVersion)
    {
        if (string.IsNullOrWhiteSpace(forgeVersion)) return true;

        // Accept both the bare version and any profile-id form
        // ("1.20.1-forge-47.4.28"): only the trailing dotted number matters.
        string version = forgeVersion.Trim();
        int dash = version.LastIndexOf('-');
        if (dash >= 0 && dash + 1 < version.Length) version = version[(dash + 1)..];

        string[] parts = version.Split('.');
        if (parts.Length < 2 || !int.TryParse(parts[0], out int major) || !int.TryParse(parts[1], out int minor))
            return true;

        // Only the 1.20.1 line (47.x) is affected; other Minecraft versions use
        // other Forge major numbers and are untouched by this bug.
        if (major != 47) return true;
        return minor < 4;
    }

    public const string MavenBase = "https://maven.minecraftforge.net";
    public const string MetadataUrl = "https://files.minecraftforge.net/net/minecraftforge/forge/maven-metadata.json";
    public const string PromotionsUrl = "https://files.minecraftforge.net/net/minecraftforge/forge/promotions_slim.json";

    private readonly HttpClient _http;
    private readonly GamePaths _paths;
    private readonly string? _mirrorBase;

    /// <summary>
    /// Shared client. A fresh instance per install kept sockets alive long enough
    /// to matter when someone installs several Forge builds in a row; this one is
    /// pooled and never disposed (the process owns it).
    /// </summary>
    private static readonly HttpClient SharedHttp = CreateSharedHttp();

    private static HttpClient CreateSharedHttp()
    {
        var client = new HttpClient(new System.Net.Http.SocketsHttpHandler
        {
            PooledConnectionLifetime = TimeSpan.FromMinutes(5)
        })
        {
            Timeout = TimeSpan.FromSeconds(120)
        };
        client.DefaultRequestHeaders.UserAgent.ParseAdd("ECL/0.8 (EternityCraftLauncher)");
        return client;
    }

    public ForgeInstaller(GamePaths paths, HttpClient? http = null, string? mirrorBase = null)
    {
        _paths = paths;
        _http = http ?? SharedHttp;
        _mirrorBase = string.IsNullOrWhiteSpace(mirrorBase) ? null : mirrorBase.TrimEnd('/');
    }

    /// <summary>
    /// Strips the Minecraft version that the metadata glues onto the Forge number.
    /// Newer eras write "&lt;mc&gt;-&lt;forge&gt;" (1.20.1-47.3.39), the 1.7 era writes
    /// "&lt;mc&gt;-&lt;forge&gt;-&lt;mc&gt;" (1.7.10-10.13.4.1614-1.7.10) and some 1.7.2 builds
    /// end in "-mc172" which is kept as-is because it is part of the number.
    /// </summary>
    public static string DisplayVersion(string mcVersion, string raw)
    {
        string v = raw.Trim();
        string prefix = mcVersion + "-";
        if (v.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) v = v[prefix.Length..];
        string suffix = "-" + mcVersion;
        if (v.EndsWith(suffix, StringComparison.OrdinalIgnoreCase) && v.Length > suffix.Length)
            v = v[..^suffix.Length];
        return v;
    }

    /// <summary>Forge builds for a Minecraft version, newest first.</summary>
    public async Task<List<ForgeBuild>> GetVersionsAsync(string mcVersion, CancellationToken ct)
    {
        string json = await _http.GetStringAsync(MetadataUrl, ct).ConfigureAwait(false);
        using var doc = JsonDocument.Parse(json);
        if (!doc.RootElement.TryGetProperty(mcVersion, out var arr) || arr.ValueKind != JsonValueKind.Array)
            return new List<ForgeBuild>();

        // The metadata strings are also the maven coordinates — the installer URL
        // is built from them verbatim. Only the display form is derived.
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var list = new List<ForgeBuild>();
        foreach (var item in arr.EnumerateArray())
        {
            string? raw = item.GetString();
            if (string.IsNullOrEmpty(raw)) continue;
            string display = DisplayVersion(mcVersion, raw);
            if (!seen.Add(display)) continue;
            list.Add(new ForgeBuild(raw, display));
        }
        list.Reverse();   // metadata is oldest-first
        return list;
    }

    public async Task<string?> GetRecommendedAsync(string mcVersion, CancellationToken ct)
    {
        try
        {
            string json = await _http.GetStringAsync(PromotionsUrl, ct).ConfigureAwait(false);
            using var doc = JsonDocument.Parse(json);
            if (!doc.RootElement.TryGetProperty("promos", out var promos)) return null;
            if (promos.TryGetProperty($"{mcVersion}-recommended", out var rec) && rec.GetString() is { } r)
                return DisplayVersion(mcVersion, r);
            if (promos.TryGetProperty($"{mcVersion}-latest", out var latest) && latest.GetString() is { } l)
                return DisplayVersion(mcVersion, l);
        }
        catch
        {
            // promotions file is optional
        }
        return null;
    }

    public static string VersionId(string mcVersion, string forgeVersion) => $"{mcVersion}-forge-{forgeVersion}";

    /// <summary>
    /// Downloads the installer and runs <c>--installClient</c>. Returns the resulting
    /// version id, verified to exist on disk afterwards.
    /// </summary>
    public async Task<string> InstallAsync(string mcVersion, ForgeBuild build, string javaExe,
        IProgress<string>? log, CancellationToken ct)
    {
        string id = VersionId(mcVersion, build.Display);
        string cacheDir = Path.Combine(_paths.Root, "cache", "installers");
        Directory.CreateDirectory(cacheDir);

        string installerPath = Path.Combine(cacheDir, $"forge-{build.Raw}-installer.jar");
        await EnsureInstallerAsync(build, installerPath, log, ct).ConfigureAwait(false);

        EnsureLauncherProfile();

        // Legacy installers (1.7.x era) do not even know --installClient — their
        // only options are installServer / extract / help. Everything a client
        // install needs is embedded in the jar, so do it by hand.
        if (IsLegacyInstaller(installerPath))
            return InstallLegacy(installerPath, mcVersion, log);

        log?.Report("运行 Forge 安装器（headless，可能需要几分钟）…");

        // A previously interrupted install leaves versions/<id>/ behind; the Forge
        // installer then treats the profile as done and skips its patching
        // processors, yielding a profile whose srg/extra/client jars never existed.
        // Always start from a clean folder.
        string versionDir = _paths.VersionDir(id);
        if (Directory.Exists(versionDir))
        {
            // Only the files the installer wrote may be removed. `game/` holds the
            // player's saves, mods and options.txt — deleting the whole folder to make
            // the installer re-run its processors silently destroys all of it.
            log?.Report($"清理旧的 {id} 产物（保留 game/）");

            foreach (string entry in Directory.EnumerateFileSystemEntries(versionDir))
            {
                string name = Path.GetFileName(entry);
                if (string.Equals(name, "game", StringComparison.OrdinalIgnoreCase))
                {
                    log?.Report("  保留 game/（存档与模组）");
                    continue;
                }

                try
                {
                    if (Directory.Exists(entry)) Directory.Delete(entry, recursive: true);
                    else File.Delete(entry);
                }
                catch (Exception ex)
                {
                    // A locked jar must not abort the install; the installer overwrites
                    // what it needs anyway.
                    log?.Report($"  跳过 {name}：{ex.Message}");
                }
            }
        }

        var before = VersionDirs();
        var (ok, output) = await RunInstallerAsync(javaExe, installerPath, _paths.Root, log, ct)
            .ConfigureAwait(false);
        var created = VersionDirs().Except(before, StringComparer.OrdinalIgnoreCase).ToList();

        // Modern installers create "<mc>-forge-<forge>" (our expected id). Legacy
        // ones (1.7.x) pick their own name ("1.7.10-Forge10.13.4.1614-1.7.10"),
        // so fall back to whatever new folder appeared.
        string jsonPath = _paths.VersionJson(id);
        if (!File.Exists(jsonPath))
        {
            var alt = created.FirstOrDefault(d =>
                File.Exists(Path.Combine(_paths.VersionsDir, d, d + ".json")));
            if (alt == null)
            {
                string tail = output.Length > 1500 ? output[^1500..] : output;
                throw new InvalidOperationException(
                    $"Forge 安装器没有生成 {id}。\n" + (ok ? "" : "安装器退出码非 0。\n") + tail);
            }
            log?.Report($"安装器生成的版本名是 {alt}（不是 {id}），按实际目录为准");
            id = alt;
        }

        log?.Report($"Forge 安装完成：{id}");
        return id;
    }

    private HashSet<string> VersionDirs()
    {
        var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (!Directory.Exists(_paths.VersionsDir)) return set;
        foreach (var d in Directory.EnumerateDirectories(_paths.VersionsDir))
            set.Add(Path.GetFileName(d));
        return set;
    }

    /// <summary>
    /// The Forge installer refuses to run without <c>launcher_profiles.json</c>
    /// ("you need to run the launcher first"). ECL does not use that file for
    /// anything, so a minimal valid stub is enough to satisfy the check.
    /// </summary>
    private void EnsureLauncherProfile()
    {
        string path = Path.Combine(_paths.Root, "launcher_profiles.json");
        if (File.Exists(path)) return;
        File.WriteAllText(path, """
            {
              "profiles": {},
              "settings": { "enableSnapshots": false, "keepLauncherOpen": false },
              "version": 3
            }
            """);
    }

    private static async Task<(bool Ok, string Output)> RunInstallerAsync(string javaExe, string installerPath,
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
        // --installClient <dir> is the headless code path; without it Forge opens a GUI.
        psi.ArgumentList.Add("-jar");
        psi.ArgumentList.Add(installerPath);
        psi.ArgumentList.Add("--installClient");
        psi.ArgumentList.Add(dataRoot);

        using var proc = new Process { StartInfo = psi };
        var sb = new StringBuilder();
        var lastReport = DateTime.UtcNow;

        void Collect(string? line)
        {
            if (line == null) return;
            lock (sb)
            {
                sb.AppendLine(line);
                if (sb.Length > 200_000) sb.Remove(0, 100_000);
            }
            // Forge prints progress as plain lines; surface them at most 2x/second.
            if (DateTime.UtcNow - lastReport > TimeSpan.FromMilliseconds(500))
            {
                lastReport = DateTime.UtcNow;
                log?.Report("  " + line.Trim());
            }
        }

        proc.OutputDataReceived += (_, e) => Collect(e.Data);
        proc.ErrorDataReceived += (_, e) => Collect(e.Data);

        if (!proc.Start()) throw new InvalidOperationException("无法启动 Forge 安装器。");

        proc.BeginOutputReadLine();
        proc.BeginErrorReadLine();

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromMinutes(20));

        try
        {
            await proc.WaitForExitAsync(timeout.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            try { proc.Kill(entireProcessTree: true); } catch { }
            throw new OperationCanceledException("Forge 安装超时或已取消。");
        }

        lock (sb) return (proc.ExitCode == 0, sb.ToString());
    }

    /// <summary>
    /// <summary>
    /// True when the file is a complete Forge installer jar: big enough to be real,
    /// openable as a ZIP, and carrying <c>install_profile.json</c>. A truncated
    /// download fails at <c>OpenRead</c> (no end-of-central-directory record) or at
    /// the entry lookup, which is exactly the signal we need.
    /// </summary>
    private static bool IsCompleteInstaller(string path)
    {
        try
        {
            var info = new FileInfo(path);
            // Real installers are 3–8 MB; anything under 1 MB is a fragment of one.
            if (!info.Exists || info.Length < 1_000_000) return false;

            using var zip = ZipFile.OpenRead(path);
            return zip.GetEntry("install_profile.json") != null;
        }
        catch
        {
            return false;
        }
    }

    private const string FallbackMirror = "https://bmclapi2.bangbang93.com";

    /// <summary>
    /// Makes sure a complete installer jar is on disk. Every download is verified,
    /// and a failure falls through to the other source — the official maven
    /// regularly truncates this 8 MB jar on slow links, which used to poison the
    /// cache with a fragment that still passed the old size check.
    /// </summary>
    private async Task EnsureInstallerAsync(ForgeBuild build, string installerPath,
        IProgress<string>? log, CancellationToken ct)
    {
        if (IsCompleteInstaller(installerPath)) return;

        if (File.Exists(installerPath))
        {
            log?.Report("缓存里的 Forge 安装器不完整，重新下载");
            try { File.Delete(installerPath); } catch { }
        }

        string path = $"net/minecraftforge/forge/{build.Raw}/forge-{build.Raw}-installer.jar";
        var sources = new List<(string Label, string Url)>();
        if (_mirrorBase != null) sources.Add(("镜像", $"{_mirrorBase}/maven/{path}"));
        sources.Add(("官方源", $"{MavenBase}/{path}"));
        if (_mirrorBase == null) sources.Add(("镜像", $"{FallbackMirror}/maven/{path}"));

        Exception? last = null;
        for (int attempt = 1; attempt <= 3; attempt++)
        {
            foreach (var (label, url) in sources)
            {
                ct.ThrowIfCancellationRequested();
                try
                {
                    log?.Report($"下载 Forge 安装器（第 {attempt} 次 · {label}）：{build.Raw}");
                    await DownloadInstallerAsync(url, installerPath, ct).ConfigureAwait(false);
                    log?.Report($"  安装器就绪：{new FileInfo(installerPath).Length / 1024} KB");
                    return;
                }
                catch (OperationCanceledException) when (ct.IsCancellationRequested)
                {
                    throw;
                }
                catch (Exception ex)
                {
                    last = ex;
                    log?.Report($"  失败：{ex.Message}");
                    try { if (File.Exists(installerPath)) File.Delete(installerPath); } catch { }
                }
            }
        }

        throw new InvalidOperationException(
            "Forge 安装器下载失败（官方源和镜像都试过了）。请检查网络后重试。" +
            (last == null ? "" : " 最后一次错误：" + last.Message), last);
    }

    /// <summary>Downloads to a side file and publishes it only after verification.</summary>
    private async Task DownloadInstallerAsync(string url, string dest, CancellationToken ct)
    {
        string part = dest + ".part";
        try
        {
            using var resp = await _http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, ct)
                .ConfigureAwait(false);
            resp.EnsureSuccessStatusCode();

            await using (var fs = File.Create(part))
                await resp.Content.CopyToAsync(fs, ct).ConfigureAwait(false);

            if (!IsCompleteInstaller(part))
                throw new InvalidOperationException("下载内容不完整（jar 打不开或缺少 install_profile.json）");

            File.Move(part, dest, overwrite: true);
        }
        finally
        {
            try { if (File.Exists(part)) File.Delete(part); } catch { }
        }
    }

    /// Legacy profiles carry a complete versionInfo and no processors pipeline;
    /// modern ones (1.8+ installers) have processors and support --installClient.
    /// </summary>
    private static bool IsLegacyInstaller(string installerPath)
    {
        try
        {
            using var zip = ZipFile.OpenRead(installerPath);
            var entry = zip.GetEntry("install_profile.json");
            if (entry == null) return false;
            using var reader = new StreamReader(entry.Open());
            using var doc = JsonDocument.Parse(reader.ReadToEnd());
            var root = doc.RootElement;
            return root.TryGetProperty("versionInfo", out _) &&
                   !root.TryGetProperty("processors", out _) &&
                   !root.TryGetProperty("data", out _);
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// Client install for legacy installers, replacing their GUI: drop the bundled
    /// universal jar into libraries/, write the embedded versionInfo as the profile
    /// json, and copy the vanilla client jar under the profile's own name.
    /// </summary>
    private string InstallLegacy(string installerPath, string mcVersion, IProgress<string>? log)
    {
        using var zip = ZipFile.OpenRead(installerPath);
        var profileEntry = zip.GetEntry("install_profile.json")
            ?? throw new InvalidOperationException("安装器里没有 install_profile.json。");
        string profileText;
        using (var reader = new StreamReader(profileEntry.Open()))
            profileText = reader.ReadToEnd();
        using var doc = JsonDocument.Parse(profileText);
        var root = doc.RootElement;
        var install = root.GetProperty("install");
        var versionInfo = root.GetProperty("versionInfo");

        string id = install.TryGetProperty("target", out var t) && t.GetString() is { } target && target.Length > 0
            ? target
            : versionInfo.GetProperty("id").GetString()!;

        // 1) the bundled forge jar goes where its maven coordinate says
        string coord = install.GetProperty("path").GetString()!;
        var parts = coord.Split(':');
        string libPath = $"{parts[0].Replace('.', '/')}/{parts[1]}/{parts[2]}/{parts[1]}-{parts[2]}.jar";
        string libDest = _paths.LibraryFile(libPath);
        Directory.CreateDirectory(Path.GetDirectoryName(libDest)!);

        string? filePath = install.TryGetProperty("filePath", out var fp) ? fp.GetString() : null;
        var jarEntry = (filePath != null ? zip.GetEntry(filePath) : null)
                       ?? zip.Entries.FirstOrDefault(e =>
                           e.FullName.EndsWith("-universal.jar", StringComparison.OrdinalIgnoreCase) &&
                           !e.FullName.Contains('/'))
                       ?? throw new InvalidOperationException(
                           $"安装器里没有找到 Forge 主 jar（{filePath ?? "?"}）。");
        using (var es = jarEntry.Open())
        using (var fs = File.Create(libDest))
            es.CopyTo(fs);
        log?.Report($"Legacy 安装：{libPath}（{new FileInfo(libDest).Length / 1024} KB）");

        // 2) the embedded versionInfo is the profile json
        string dir = _paths.VersionDir(id);
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, id + ".json"), versionInfo.GetRawText());

        // 3) legacy profiles expect the vanilla client jar under the profile name
        string vanillaJar = _paths.VersionJar(mcVersion);
        if (File.Exists(vanillaJar))
        {
            File.Copy(vanillaJar, Path.Combine(dir, id + ".jar"), overwrite: true);
            log?.Report($"Legacy 安装：复制原版 {mcVersion} jar（{new FileInfo(vanillaJar).Length / 1024} KB）");
        }
        else
        {
            log?.Report($"警告：找不到原版 {mcVersion} 的 jar，启动时将复用继承链上的 jar");
        }

        log?.Report($"Legacy Forge 安装完成：{id}");
        return id;
    }

    public bool IsInstalled(string mcVersion, string forgeVersion) =>
        File.Exists(_paths.VersionJson(VersionId(mcVersion, forgeVersion)));
}
