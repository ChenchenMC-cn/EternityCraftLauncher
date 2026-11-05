// SPDX-License-Identifier: MPL-2.0
// Copyright (c) 2026 ChenchenMC <https://github.com/ChenchenMC-cn/EternityCraftLauncher>
using System.IO;
using ECL.Models;

namespace ECL.Services;

/// <summary>
/// Runs the installer without the UI. Used by the smoke test
/// (<c>ECL.exe --install 1.20.1 --log path</c>) and useful for pre-seeding a
/// shared machine image. Writes a plain text trace so failures are diagnosable
/// from a log file alone.
/// </summary>
public static class HeadlessRunner
{
    public static async Task<int> InstallAsync(string versionId, string logPath, int? threads = null, string? dataRoot = null)
    {
        await using var log = new StreamWriter(logPath, append: false) { AutoFlush = true };
        void Write(string s)
        {
            log.WriteLine($"[{DateTime.Now:HH:mm:ss.fff}] {s}");
        }

        try
        {
            var settings = LauncherSettings.Load(LauncherSettings.ResolveSettingsPath());
            settings.Normalize();
            if (threads is > 0) settings.DownloadThreads = threads.Value;
            if (!string.IsNullOrWhiteSpace(dataRoot)) settings.DataRoot = dataRoot;

            var paths = new GamePaths(settings.DataRoot, settings.IsolateByVersion);
            paths.EnsureLayout();
            Write($"data root: {paths.Root}");
            Write($"threads: {settings.DownloadThreads}, mirror: {settings.UseMirror}");

            NativeCore.IsAvailable(out var nativeVersion);
            Write($"native: {nativeVersion}");
            if (string.IsNullOrEmpty(nativeVersion))
            {
                Write("FATAL: ECL.Native.dll could not be loaded");
                return 2;
            }

            using var api = new ApiClient(settings, paths.Root);
            var manifest = await api.GetManifestAsync(null, CancellationToken.None).ConfigureAwait(false);
            var entry = manifest.Versions.FirstOrDefault(v => v.Id == versionId);
            if (entry == null)
            {
                Write($"FATAL: version '{versionId}' is not in the manifest");
                return 3;
            }
            Write($"target: {entry.Id} ({entry.Type}, {entry.ReleaseTime:yyyy-MM-dd})");

            var install = new InstallService(settings, paths, api);
            var sw = System.Diagnostics.Stopwatch.StartNew();

            var (plan, detail) = await install.BuildPlanAsync(entry.Id, entry.Url,
                new Progress<string>(Write), CancellationToken.None).ConfigureAwait(false);

            Write($"plan: {plan.Files.Count} files, {plan.TotalBytes / 1024.0 / 1024.0:F1} MB, " +
                  $"{plan.Extracts.Count} natives archive(s)");
            Write($"main class: {detail.MainClass}, assets index: {detail.AssetIndex?.Id}, " +
                  $"java: {detail.JavaVersion?.MajorVersion}");

            int lastPercent = -10;
            var progress = new Progress<InstallProgress>(p =>
            {
                int percent = (int)(p.Download.Fraction * 100);
                if (percent >= lastPercent + 10)
                {
                    lastPercent = percent;
                    Write($"{p.Stage}: {percent}%  ({p.Download.FilesDone}/{p.Download.FilesTotal} files, " +
                          $"{p.Download.BytesDone / 1024.0 / 1024.0:F1} MB, " +
                          $"{p.Download.BytesPerSecond / 1024.0 / 1024.0:F2} MB/s)");
                }
            });

            await install.ExecuteAsync(plan, progress, new Progress<string>(Write), CancellationToken.None)
                          .ConfigureAwait(false);

            await File.WriteAllTextAsync(
                Path.Combine(paths.VersionDir(entry.Id), ".installed"),
                DateTime.Now.ToString("O")).ConfigureAwait(false);

            sw.Stop();
            Write($"DONE in {sw.Elapsed.TotalSeconds:F1}s");
            Write($"jar: {paths.VersionJar(entry.Id)} ({new FileInfo(paths.VersionJar(entry.Id)).Length / 1024} KB)");
            Write($"natives: {(Directory.Exists(paths.VersionNatives(entry.Id))
                ? Directory.GetFiles(paths.VersionNatives(entry.Id)).Length
                : 0)} file(s)");
            Write($"libraries: {Directory.GetFiles(paths.LibrariesDir, "*.jar", SearchOption.AllDirectories).Length} jar(s)");
            Write($"assets: {Directory.GetFiles(paths.AssetObjectsDir, "*", SearchOption.AllDirectories).Length} object(s)");
            return 0;
        }
        catch (Exception ex)
        {
            Write("FAILED: " + ex.GetType().Name + ": " + ex.Message);
            return 1;
        }
    }

    /// <summary>Builds the java command line for a version and returns it (dry run).</summary>
    public static async Task<int> PrintLaunchCommandAsync(string versionId, string logPath, string? dataRoot = null)
    {
        await using var log = new StreamWriter(logPath, append: false) { AutoFlush = true };
        try
        {
            var settings = LauncherSettings.Load(LauncherSettings.ResolveSettingsPath());
            settings.Normalize();
            if (!string.IsNullOrWhiteSpace(dataRoot)) settings.DataRoot = dataRoot;
            var paths = new GamePaths(settings.DataRoot, settings.IsolateByVersion);

            if (!EnsureVersionInstalled(paths, versionId, log.WriteLine)) return 1;

            var json = await File.ReadAllTextAsync(paths.VersionJson(versionId)).ConfigureAwait(false);
            var detail = System.Text.Json.JsonSerializer.Deserialize<VersionDetail>(json,
                new System.Text.Json.JsonSerializerOptions { PropertyNameCaseInsensitive = true });
            if (detail == null) return 4;

            var resolved = await VersionResolver.ResolveAsync(versionId, paths, null, CancellationToken.None)
                                                .ConfigureAwait(false);

            int required = resolved.Detail.JavaVersion?.MajorVersion ?? 8;
            var java = await EnsureJavaAsync(settings, paths, required,
                s => log.WriteLine(s), CancellationToken.None).ConfigureAwait(false);
            if (java == null)
            {
                log.WriteLine("no java runtime found");
                return 5;
            }

            log.WriteLine($"java: {java.JavaExe} (major {java.Info.Major}, required {required})");
            log.WriteLine($"loader: {(resolved.IsModded ? $"modded, base {resolved.JarVersionId}" : "vanilla")}");

            var launcher = new LaunchService(settings, paths);
            string classpath = launcher.BuildClasspath(resolved);
            log.WriteLine($"classpath entries: {classpath.Split(';').Length}");

            var account = Account.CreateOffline(settings.UserName);
            var args = launcher.BuildArguments(
                new LaunchService.LaunchRequest(versionId, resolved, account, java));
            log.WriteLine("--- command line ---");
            log.WriteLine(java.JavaExe + " " + string.Join(" ", args.Select(Render)));
            return 0;
        }
        catch (Exception ex)
        {
            log.WriteLine("FAILED: " + ex.GetType().Name + ": " + ex.Message);
            return 1;
        }
    }

    /// <summary>
    /// Installs a mod loader on top of a vanilla version. <paramref name="kind"/> is
    /// "fabric" or "forge". Used by the smoke test and for seeding machines.
    /// </summary>
    public static async Task<int> InstallLoaderAsync(string kind, string mcVersion, string? loaderVersion,
        string logPath, string? dataRoot, int? threads = null, string? optifineEdition = null)
    {
        await using var log = new StreamWriter(logPath, append: false) { AutoFlush = true };
        void Write(string s) => log.WriteLine($"[{DateTime.Now:HH:mm:ss.fff}] {s}");

        // Swapping the two arguments is easy to do and used to surface as
        // "version forge not in manifest", which reads like a network problem.
        if (!LoaderKinds.Contains(kind, StringComparer.OrdinalIgnoreCase))
        {
            Write($"未知的加载器 '{kind}'");
            Write("用法：--install-loader <forge|fabric|quilt|optifine> <minecraft 版本>");
            Write("  例：--install-loader forge 1.20.1");
            return 1;
        }

        try
        {
            var settings = LauncherSettings.Load(LauncherSettings.ResolveSettingsPath());
            settings.Normalize();
            if (threads is > 0) settings.DownloadThreads = threads.Value;
            if (!string.IsNullOrWhiteSpace(dataRoot)) settings.DataRoot = dataRoot;

            var paths = new GamePaths(settings.DataRoot, settings.IsolateByVersion);
            paths.EnsureLayout();
            using var api = new ApiClient(settings, paths.Root);
            var install = new InstallService(settings, paths, api);

            Write($"target: {kind} {loaderVersion ?? "(latest)"} on {mcVersion}");
            Write($"data root: {paths.Root}");

            // ---- vanilla base ----
            if (install.IsInstalled(mcVersion))
            {
                Write($"vanilla {mcVersion} already installed");
            }
            else
            {
                var manifest = await api.GetManifestAsync(null, CancellationToken.None).ConfigureAwait(false);
                var entry = manifest.Versions.FirstOrDefault(v => v.Id == mcVersion)
                            ?? throw new InvalidOperationException($"version {mcVersion} not in manifest");
                var (plan, _) = await install.BuildPlanAsync(mcVersion, entry.Url,
                    new Progress<string>(Write), CancellationToken.None).ConfigureAwait(false);
                await install.ExecuteAsync(plan, null, new Progress<string>(Write), CancellationToken.None)
                             .ConfigureAwait(false);
                install.MarkInstalled(mcVersion);
                Write($"vanilla {mcVersion} installed");
            }

            string target;
            if (kind.Equals("fabric", StringComparison.OrdinalIgnoreCase))
            {
                var fabric = new FabricInstaller(paths, useMirror: settings.UseMirror);
                target = await fabric.InstallProfileAsync(mcVersion, loaderVersion,
                    new Progress<string>(Write), CancellationToken.None).ConfigureAwait(false);

                var (plan, _) = await install.BuildPlanFromLocalAsync(target, new Progress<string>(Write),
                    CancellationToken.None).ConfigureAwait(false);
                await install.ExecuteAsync(plan, null, new Progress<string>(Write), CancellationToken.None)
                             .ConfigureAwait(false);
            }
            else if (kind.Equals("quilt", StringComparison.OrdinalIgnoreCase))
            {
                var quilt = new QuiltInstaller(paths, useMirror: settings.UseMirror);
                target = await quilt.InstallProfileAsync(mcVersion, loaderVersion,
                    new Progress<string>(Write), CancellationToken.None).ConfigureAwait(false);

                var (plan, _) = await install.BuildPlanFromLocalAsync(target, new Progress<string>(Write),
                    CancellationToken.None).ConfigureAwait(false);
                await install.ExecuteAsync(plan, null, new Progress<string>(Write), CancellationToken.None)
                             .ConfigureAwait(false);
            }
            else if (kind.Equals("optifine", StringComparison.OrdinalIgnoreCase))
            {
                var opti = new OptiFineInstaller(paths);
                var builds = await opti.GetBuildsAsync(mcVersion, CancellationToken.None).ConfigureAwait(false);
                Write($"optifine builds: {string.Join(", ", builds.Select(b => b.Edition))}");

                var build = loaderVersion == null
                    ? builds.FirstOrDefault()
                    : builds.FirstOrDefault(b => b.Edition.Equals(loaderVersion, StringComparison.OrdinalIgnoreCase))
                      ?? throw new InvalidOperationException($"no OptiFine build '{loaderVersion}' for {mcVersion}");

                if (build == null) throw new InvalidOperationException($"no OptiFine build for {mcVersion}");

                var java = await EnsureJavaAsync(settings, paths, 8, Write, CancellationToken.None)
                               .ConfigureAwait(false)
                    ?? throw new InvalidOperationException("OptiFine installer needs a Java runtime");

                Write($"using java {java.JavaExe}");
                target = await opti.InstallAsync(mcVersion, build.FileName, java.JavaExe,
                    new Progress<string>(Write), CancellationToken.None).ConfigureAwait(false);
            }
            else if (kind.Equals("forge", StringComparison.OrdinalIgnoreCase))
            {
                var forge = new ForgeInstaller(paths, null, settings.UseMirror ? settings.MirrorBase : null);
                var builds = await forge.GetVersionsAsync(mcVersion, CancellationToken.None).ConfigureAwait(false);
                var recommended = await forge.GetRecommendedAsync(mcVersion, CancellationToken.None)
                                             .ConfigureAwait(false);

                ForgeBuild? chosen = loaderVersion == null
                    ? builds.FirstOrDefault(b => b.Display == recommended) ?? builds.FirstOrDefault()
                    : builds.FirstOrDefault(b =>
                          b.Display.Equals(loaderVersion, StringComparison.OrdinalIgnoreCase) ||
                          b.Raw.Equals(loaderVersion, StringComparison.OrdinalIgnoreCase))
                      ?? new ForgeBuild(loaderVersion, ForgeInstaller.DisplayVersion(mcVersion, loaderVersion));

                if (chosen == null) throw new InvalidOperationException($"no Forge build for {mcVersion}");

                var java = await EnsureJavaAsync(settings, paths, 17, Write, CancellationToken.None)
                               .ConfigureAwait(false)
                    ?? throw new InvalidOperationException("Forge installer needs Java 17+");

                Write($"using java {java.JavaExe}");
                Write($"forge build: {chosen.Raw} (display {chosen.Display})");
                target = await forge.InstallAsync(mcVersion, chosen, java.JavaExe,
                    new Progress<string>(Write), CancellationToken.None).ConfigureAwait(false);

                // Same path the UI takes for "Forge + OptiFine": the jar is copied
                // into the profile's own mods folder, no second installer run.
                if (optifineEdition != null)
                {
                    if (!ForgeInstaller.IsOptiFineCompatible(chosen.Display))
                        throw new InvalidOperationException(
                            "Forge " + chosen.Display + " is incompatible with OptiFine (47.4+ bug)");

                    var opti = new OptiFineInstaller(paths);
                    var ofBuilds = await opti.GetBuildsAsync(mcVersion, CancellationToken.None).ConfigureAwait(false);
                    var ofBuild = ofBuilds.FirstOrDefault(b => b.Edition.Equals(optifineEdition, StringComparison.OrdinalIgnoreCase))
                                  ?? ofBuilds.FirstOrDefault()
                                  ?? throw new InvalidOperationException("no OptiFine build for " + mcVersion);

                    Write($"adding {ofBuild.FileName} to {target} mods");
                    string dest = await opti.InstallAsForgeModAsync(mcVersion, ofBuild.FileName, target,
                        new Progress<string>(Write), CancellationToken.None).ConfigureAwait(false);
                    Write($"optifine mod: {dest}");
                }
            }

            else
            {
                Write("unknown loader kind: " + kind);
                return 3;
            }

            // Modern installers fetch their own libraries; the legacy 1.7.x manual
            // install does not. Re-planning is idempotent, so always reconcile.
            var (forgePlan, _) = await install.BuildPlanFromLocalAsync(target, new Progress<string>(Write),
                CancellationToken.None).ConfigureAwait(false);
            await install.ExecuteAsync(forgePlan, null, new Progress<string>(Write), CancellationToken.None)
                         .ConfigureAwait(false);

            install.MarkInstalled(target, mcVersion);
            Write($"DONE: {target}");
            Write($"json: {paths.VersionJson(target)}");

            var resolved = await VersionResolver.ResolveAsync(target, paths, new Progress<string>(Write),
                CancellationToken.None).ConfigureAwait(false);
            var launcher = new LaunchService(settings, paths);
            string classpath = launcher.BuildClasspath(resolved);
            Write($"classpath entries: {classpath.Split(';').Length}");
            Write($"main class: {resolved.Detail.MainClass}");
            Write($"libraries: {resolved.Detail.Libraries.Count}");
            return 0;
        }
        catch (Exception ex)
        {
            Write("FAILED: " + ex.GetType().Name + ": " + ex.Message);
            return 1;
        }
    }

    // ------------------------------------------------------------- mods ---

    /// <summary>Finds a suitable JVM, downloading a Temurin JRE when none fits.</summary>
    private static async Task<JavaRuntime?> EnsureJavaAsync(LauncherSettings settings, GamePaths paths,
        int requiredMajor, Action<string>? log, CancellationToken ct)
    {
        var runtimes = await new JavaLocator(settings).DiscoverAsync(null, ct).ConfigureAwait(false);
        var java = JavaLocator.Choose(runtimes, requiredMajor);
        if (java != null) return java;

        if (!settings.AutoDownloadJava) return null;

        int target = JavaDownloader.TargetMajorFor(requiredMajor);
        log?.Invoke($"no Java {requiredMajor}; downloading Temurin Java {target}");
        string? javaw = await JavaDownloader.EnsureAsync(target, paths, null, ct).ConfigureAwait(false);
        if (javaw == null) return null;

        runtimes = await new JavaLocator(settings).DiscoverAsync(null, ct).ConfigureAwait(false);
        return JavaLocator.Choose(runtimes, requiredMajor);
    }

    private static (LauncherSettings Settings, GamePaths Paths) LoadContext(string? dataRoot)
    {
        var settings = LauncherSettings.Load(LauncherSettings.ResolveSettingsPath());
        settings.Normalize();
        if (!string.IsNullOrWhiteSpace(dataRoot)) settings.DataRoot = dataRoot;
        return (settings, new GamePaths(settings.DataRoot, settings.IsolateByVersion));
    }

    private static LoaderKind KindOf(GamePaths paths, string versionId) =>
        InstalledVersionScanner.Scan(paths).FirstOrDefault(v => v.Id == versionId)?.Kind ?? LoaderKind.Vanilla;

    /// <summary>Lists the mods of one version with the metadata ECL parsed from each jar.</summary>
    public static Task<int> ListModsAsync(string versionId, string logPath, string? dataRoot)
    {
        using var log = new StreamWriter(logPath, append: false) { AutoFlush = true };
        try
        {
            var (_, paths) = LoadContext(dataRoot);
            if (!EnsureVersionInstalled(paths, versionId, log.WriteLine)) return Task.FromResult(1);

            var service = new ModService(paths);
            var kind = KindOf(paths, versionId);
            var mods = service.List(versionId);

            log.WriteLine($"version: {versionId} ({kind})");
            log.WriteLine($"mods dir: {service.ModsDir(versionId)}");
            log.WriteLine($"isolated: {paths.IsolateByVersion}");
            log.WriteLine($"count: {mods.Count} (enabled {mods.Count(m => m.IsEnabled)})");
            foreach (var m in mods)
            {
                log.WriteLine($"  [{(m.IsEnabled ? "on " : "off")}] {m.PlatformLabel,-9} " +
                              $"{m.Title} | v{m.ModVersion} | {m.FileName} | {m.SizeText} | " +
                              $"compatible={(m.WorksOn(kind) ? "yes" : "NO")}");
                if (m.ParseError != null) log.WriteLine($"        parse error: {m.ParseError}");
            }
            return Task.FromResult(0);
        }
        catch (Exception ex)
        {
            log.WriteLine("FAILED: " + ex.Message);
            return Task.FromResult(1);
        }
    }

    /// <summary>Renames a mod jar between .jar and .jar.disabled.</summary>
    public static Task<int> ToggleModAsync(string versionId, string fileName, bool enable, string logPath,
        string? dataRoot)
    {
        using var log = new StreamWriter(logPath, append: false) { AutoFlush = true };
        try
        {
            var (_, paths) = LoadContext(dataRoot);
            var service = new ModService(paths);
            var entry = FindMod(service.List(versionId), fileName)
                        ?? throw new InvalidOperationException($"no mod '{fileName}' in {versionId}");

            var updated = service.SetEnabled(versionId, entry, enable);
            log.WriteLine($"{(enable ? "enabled" : "disabled")}: {updated.FileName}");
            log.WriteLine($"exists: {File.Exists(updated.FullPath)}");
            return Task.FromResult(0);
        }
        catch (Exception ex)
        {
            log.WriteLine("FAILED: " + ex.Message);
            return Task.FromResult(1);
        }
    }


    /// <summary>
    /// Finds a mod by file name, tolerating the <c>.disabled</c> suffix in either
    /// direction. Disabling renames the file on disk, so "probe-mod.jar" stops matching
    /// after a disable — which made "disable then enable" impossible from the CLI.
    /// </summary>
    private static ModEntry? FindMod(IEnumerable<ModEntry> mods, string name)
    {
        string bare = name.EndsWith(".disabled", StringComparison.OrdinalIgnoreCase)
            ? name[..^".disabled".Length]
            : name;

        return mods.FirstOrDefault(m =>
            m.FileName.Equals(bare, StringComparison.OrdinalIgnoreCase) ||
            m.FileName.Equals(bare + ".disabled", StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>
    /// Saves a PNG as the current skin. The launcher picks it up on the next start and
    /// serves it through the local Yggdrasil endpoint.
    ///
    /// <para>Exists so the feature is usable before the skin page is built, and so the
    /// picker logic can be tested from a script.</para>
    /// </summary>
    public static Task<int> SetSkinAsync(string pngPath, string logPath, string? dataRoot, bool slim)
    {
        using var log = new StreamWriter(logPath, append: false) { AutoFlush = true };
        try
        {
            var (_, paths) = LoadContext(dataRoot);
            paths.EnsureLayout();

            var service = new Models.SkinService(paths.Root);
            var info = service.FromFile(pngPath, slim);
            service.SaveCurrent(info);

            log.WriteLine("皮肤已保存：" + info.Label);
            log.WriteLine("文件：" + info.SkinPath);
            log.WriteLine("模型：" + (info.Slim ? "纤细（Alex）" : "经典（Steve）"));
            log.WriteLine("下次启动游戏时生效。");
            return Task.FromResult(0);
        }
        catch (Exception ex)
        {
            log.WriteLine("FAILED: " + ex.GetType().Name + ": " + ex.Message);
            return Task.FromResult(1);
        }
    }

    /// <summary>Copies a jar into a version's mods folder and prints what was parsed.</summary>
    public static Task<int> ImportModAsync(string versionId, string sourceFile, string logPath, string? dataRoot)
    {
        using var log = new StreamWriter(logPath, append: false) { AutoFlush = true };
        try
        {
            var (_, paths) = LoadContext(dataRoot);
            var service = new ModService(paths);
            var entry = service.Import(versionId, sourceFile);
            log.WriteLine($"imported: {entry.FileName} -> {service.ModsDir(versionId)}");
            log.WriteLine($"platform: {entry.PlatformLabel}, id: {entry.ModId}, name: {entry.Title}, " +
                          $"version: {entry.ModVersion}");
            log.WriteLine($"authors: {entry.Authors}");
            log.WriteLine($"size: {entry.SizeText}");
            log.WriteLine($"compatible with {KindOf(paths, versionId)}: {entry.WorksOn(KindOf(paths, versionId))}");
            return Task.FromResult(0);
        }
        catch (Exception ex)
        {
            log.WriteLine("FAILED: " + ex.Message);
            return Task.FromResult(1);
        }
    }

    /// <summary>Searches Modrinth and prints the top hits (no download).</summary>
    public static async Task<int> ModSearchAsync(string query, string mcVersion, string? loader, string logPath,
        string? dataRoot)
    {
        await using var log = new StreamWriter(logPath, append: false) { AutoFlush = true };
        try
        {
            var (_, paths) = LoadContext(dataRoot);
            var service = new ModrinthService(paths);
            var hits = await service.SearchAsync(query, mcVersion, loader, 15, CancellationToken.None)
                                   .ConfigureAwait(false);
            log.WriteLine($"query: {query}  mc: {mcVersion}  loader: {loader ?? "-"}");
            log.WriteLine($"hits: {hits.Count}");
            foreach (var h in hits)
                log.WriteLine($"  {h.Slug,-28} | {h.Title,-32} | {h.Author,-16} | {h.DownloadsText} | {h.ProjectId}");
            return 0;
        }
        catch (Exception ex)
        {
            log.WriteLine("FAILED: " + ex.GetType().Name + ": " + ex.Message);
            return 1;
        }
    }

    /// <summary>Downloads and imports a Modrinth project into a version's mods folder.</summary>
    public static async Task<int> ModInstallAsync(string versionId, string slug, string logPath, string? dataRoot)
    {
        await using var log = new StreamWriter(logPath, append: false) { AutoFlush = true };
        void Write(string s) => log.WriteLine($"[{DateTime.Now:HH:mm:ss.fff}] {s}");

        try
        {
            var (_, paths) = LoadContext(dataRoot);
            var info = InstalledVersionScanner.Scan(paths).FirstOrDefault(v => v.Id == versionId)
                       ?? throw new InvalidOperationException($"no installed version '{versionId}'");
            string? loader = ModrinthService.LoaderCategory(info.Kind);
            if (loader == null)
                throw new InvalidOperationException($"{info.Kind} cannot load mods (need fabric/quilt/forge)");

            Write($"version: {versionId} ({info.Kind}), mc: {info.BaseVersion}, loader: {loader}");
            var service = new ModrinthService(paths);
            var entry = await service.InstallAsync(versionId, slug, info.BaseVersion, loader,
                new Progress<(long, long)>(p => { if (p.Item2 > 0) Write($"  download: {100.0 * p.Item1 / p.Item2:F0}% ({p.Item1}/{p.Item2})"); }),
                CancellationToken.None).ConfigureAwait(false);

            Write($"installed: {entry.FileName} -> {paths.ModsDir(versionId)}");
            Write($"platform: {entry.PlatformLabel}, id: {entry.ModId}, name: {entry.Title}, size: {entry.SizeText}");
            return 0;
        }
        catch (Exception ex)
        {
            Write("FAILED: " + ex.GetType().Name + ": " + ex.Message);
            return 1;
        }
    }

    /// <summary>Moves a mod jar into mods/.ecl-trash (the UI's "remove" action).</summary>
    public static Task<int> TrashModAsync(string versionId, string fileName, string logPath, string? dataRoot)
    {
        using var log = new StreamWriter(logPath, append: false) { AutoFlush = true };
        try
        {
            var (_, paths) = LoadContext(dataRoot);
            var service = new ModService(paths);
            var entry = FindMod(service.List(versionId), fileName)
                        ?? throw new InvalidOperationException("no mod '" + fileName + "' in " + versionId);

            service.MoveToTrash(versionId, entry);
            log.WriteLine("moved to trash: " + entry.FileName);
            log.WriteLine("still in mods: " + File.Exists(entry.FullPath));
            log.WriteLine("remaining mods: " + service.List(versionId).Count);

            string trash = paths.ModsTrashDir(versionId);
            foreach (var f in Directory.Exists(trash) ? Directory.GetFiles(trash) : Array.Empty<string>())
                log.WriteLine("  trashed: " + Path.GetFileName(f));
            return Task.FromResult(0);
        }
        catch (Exception ex)
        {
            log.WriteLine("FAILED: " + ex.Message);
            return Task.FromResult(1);
        }
    }

    /// <summary>Installs OptiFine as a Forge mod into an already installed profile.</summary>
    public static async Task<int> InstallOptiFineAsModAsync(string mcVersion, string? edition,
        string? forgeVersionId, string logPath, string? dataRoot)
    {
        await using var log = new StreamWriter(logPath, append: false) { AutoFlush = true };
        void Write(string s) => log.WriteLine($"[{DateTime.Now:HH:mm:ss.fff}] {s}");

        try
        {
            var (_, paths) = LoadContext(dataRoot);
            var forgeProfiles = InstalledVersionScanner.Scan(paths)
                .Where(v => v.Kind == LoaderKind.Forge &&
                            v.BaseVersion.Equals(mcVersion, StringComparison.OrdinalIgnoreCase))
                .ToList();
            if (forgeProfiles.Count == 0)
                throw new InvalidOperationException($"no installed Forge profile for {mcVersion}");

            var profile = forgeVersionId != null
                ? forgeProfiles.FirstOrDefault(v => v.Id.Equals(forgeVersionId, StringComparison.OrdinalIgnoreCase))
                  ?? throw new InvalidOperationException($"no installed Forge profile '{forgeVersionId}'")
                : forgeProfiles.FirstOrDefault(v => ForgeInstaller.IsOptiFineCompatible(v.LoaderVersion))
                  ?? throw new InvalidOperationException(
                      $"every Forge profile for {mcVersion} is 47.4+ and OptiFine crashes on those: " +
                      string.Join(", ", forgeProfiles.Select(v => v.Id)));

            if (!ForgeInstaller.IsOptiFineCompatible(profile.LoaderVersion))
                throw new InvalidOperationException(
                    $"Forge {profile.LoaderVersion} is incompatible with OptiFine (47.4+ bug)");

            string profileId = profile.Id;
            Write($"forge profile: {profileId} (Forge {profile.LoaderVersion})");
            Write($"isolation: {paths.IsolateByVersion}, game dir: {paths.GameDir(profileId)}");

            var opti = new OptiFineInstaller(paths);
            var builds = await opti.GetBuildsAsync(mcVersion, CancellationToken.None).ConfigureAwait(false);
            Write($"builds: {string.Join(", ", builds.Select(b => b.Edition))}");

            var build = edition == null
                ? builds.FirstOrDefault()
                : builds.FirstOrDefault(b => b.Edition.Equals(edition, StringComparison.OrdinalIgnoreCase));
            if (build == null) throw new InvalidOperationException($"no OptiFine build '{edition}' for {mcVersion}");

            Write($"installing {build.FileName} as a Forge mod");
            string dest = await opti.InstallAsForgeModAsync(mcVersion, build.FileName, profileId,
                new Progress<string>(Write), CancellationToken.None).ConfigureAwait(false);
            Write($"dest: {dest}");
            Write($"size: {new FileInfo(dest).Length / 1024.0 / 1024.0:F1} MB");

            foreach (var m in new ModService(paths).List(profileId))
                Write($"  mod: {m.PlatformLabel} {m.Title} {m.ModVersion} | {m.FileName}");
            return 0;
        }
        catch (Exception ex)
        {
            Write("FAILED: " + ex.GetType().Name + ": " + ex.Message);
            return 1;
        }
    }

    /// <summary>
    /// Verifies the security-critical primitives without touching the network:
    /// DPAPI round-trip, per-call randomisation, tamper rejection, and that the
    /// account file never contains a token in clear text.
    /// </summary>
    public static int SelfTest(string logPath)
    {
        using var log = new StreamWriter(logPath, append: false) { AutoFlush = true };
        int failures = 0;

        void Check(string name, bool ok, string detail = "")
        {
            if (!ok) failures++;
            log.WriteLine($"{(ok ? "PASS" : "FAIL")}  {name}{(detail.Length == 0 ? "" : "  - " + detail)}");
        }

        // PathGuard must name the offending path and suggest a fix; a silent throw
        // is exactly what made MAX_PATH failures look like random corruption.
        static bool ThrowsWithPath(string tooLong)
        {
            try
            {
                PathGuard.EnsureUsable(tooLong, "测试", supportsLongPaths: false);
                return false;
            }
            catch (IOException ex)
            {
                return ex.Message.Contains("260", StringComparison.Ordinal) ||
                       ex.Message.Length > 40;
            }
        }

        log.WriteLine("=== ECL self test ===");

        // Natives are the one thing a single-file publish can silently lose.
        NativeCore.IsAvailable(out var nativeVersion);
        Check("native core loadable", !string.IsNullOrEmpty(nativeVersion), nativeVersion ?? "(null)");

        string secret = "mc-access-token-" + Guid.NewGuid().ToString("N");
        string? sealed1 = Dpapi.Protect(secret);
        string? sealed2 = Dpapi.Protect(secret);
        string? opened = Dpapi.Unprotect(sealed1);

        Check("DPAPI protects", !string.IsNullOrEmpty(sealed1));
        Check("DPAPI round-trip", opened == secret);
        Check("sealed blob contains no plaintext",
            sealed1 != null && !sealed1.Contains(secret, StringComparison.Ordinal));
        Check("ciphertext randomised per call", sealed1 != sealed2);

        if (sealed1 != null)
        {
            byte[] raw = Convert.FromBase64String(sealed1);
            raw[raw.Length / 2] ^= 0xFF;
            Check("tampered blob rejected", Dpapi.Unprotect(Convert.ToBase64String(raw)) == null);
        }

        Check("empty input handled", Dpapi.Protect("") == null && Dpapi.Unprotect(null) == null);

        // --- Microsoft sign-in plumbing (no network, no account needed) -----
        Check("PKCE S256 matches RFC 7636 vector", MicrosoftAuthService.PkceMatchesRfcVector());

        // --- language table (drives the --lang flag) ------------------------
        Check("MC language table complete", McLanguages.All.Count >= 140,
            $"count={McLanguages.All.Count}");
        string sysLang = McLanguages.DetectSystem();
        Check("system language resolves to a known code", McLanguages.Find(sysLang) != null,
            $"{sysLang} -> {McLanguages.Find(sysLang)?.Name ?? "(unknown)"}");
        Check("explicit language wins over system",
            McLanguages.Resolve("ja_jp") == "ja_jp" && McLanguages.Resolve("auto") == sysLang);

        // --- agreement translations -----------------------------------------
        Check("terms translations loaded", TermsText.Translated.Count == 6 &&
            TermsText.Translated.All(d => d.Blocks.Count >= 5 && d.Signature.Length > 0),
            $"count={TermsText.Translated.Count}");
        Check("unknown language falls back to English",
            TermsText.For("xx_yy", out bool fellBack).Code == TermsText.FallbackCode && fellBack);
        Check("translations are actually distinct",
            TermsText.Translated
                .Select(d => string.Concat(d.Blocks.Select(b => b.Body)))
                .Distinct(StringComparer.Ordinal).Count() == TermsText.Translated.Count,
            string.Join(" | ", TermsText.Translated.Select(d => d.Code)));

        // --- game language --------------------------------------------------
        // "auto" resolves to a concrete code that gets written into options.txt.
        // An empty or unknown code would silently leave the game on its JVM default.
        string detectedLang = McLanguages.DetectSystem();
        bool langKnown = !string.IsNullOrWhiteSpace(detectedLang) &&
                         McLanguages.Resolve("auto") == detectedLang &&
                         McLanguages.Resolve("en_us") == "en_us";

        Check("game language resolves to a real code", langKnown,
            $"detected={detectedLang} auto={McLanguages.Resolve("auto")}");

        // --- localisation ---------------------------------------------------
        // The dictionary is keyed by exact Chinese literals; a typo there silently
        // leaves the UI in Chinese, so pin a few entries down here.
        string previousLanguage = Localization.Language;
        Localization.SetLanguage("en");
        string[] probes = { "选择一个版本", "还没有可启动的版本", "离线账户", "开始游戏", "保存设置" };
        var misses = new System.Collections.Generic.List<string>();
        foreach (string probe in probes)
            if (Localization.T(probe) == probe) misses.Add(probe);

        bool enOk = misses.Count == 0 &&
                    Localization.T("不存在的词") == "不存在的词";   // unknown must pass through
        Localization.SetLanguage("zh");
        bool zhOk = Localization.T("选择一个版本") == "选择一个版本";
        Localization.SetLanguage(previousLanguage);

        Check("localization translates both ways", enOk && zhOk,
            $"en={enOk} zh={zhOk} entries={Localization.EntryCount}" +
            (misses.Count == 0 ? "" : " missing=" + string.Join(",", misses)));

        // --- local skin server ----------------------------------------------
        // Exercises the whole chain the game uses: metadata (public key), profile
        // (base64 textures + signature), and the texture bytes. If the signature is
        // wrong the client silently ignores the skin, so verifying that all three
        // pieces are present is the only cheap way to catch it here.
        try
        {
            string keyPath = Path.Combine(Path.GetTempPath(), "ecl-selftest-skin.pem");
            using var skinServer = new LocalSkinServer(keyPath);

            // Not a real PNG, but the server is not a decoder — it only has to serve
            // bytes, and the game does its own validation.
            skinServer.SetSkin("0123456789abcdef0123456789abcdef", "Tester",
                new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A, 1, 2, 3, 4 },
                slim: false, cape: null);
            skinServer.Start();

            using var skinHttp = new System.Net.Http.HttpClient { Timeout = TimeSpan.FromSeconds(5) };

            // SelfTest is synchronous and runs headless (no SynchronizationContext),
            // so blocking on the task here cannot deadlock.
            string meta = skinHttp.GetStringAsync(skinServer.ApiRoot + "/").GetAwaiter().GetResult();
            bool hasKey = meta.Contains("signaturePublickey", StringComparison.OrdinalIgnoreCase) &&
                          meta.Contains("skinDomains", StringComparison.OrdinalIgnoreCase);

            string profile = skinHttp.GetStringAsync(
                    $"{skinServer.ApiRoot}/sessionserver/session/minecraft/profile/0123456789abcdef0123456789abcdef")
                .GetAwaiter().GetResult();
            // skin.png lives inside the base64 "value", not in the clear — decode it
            // rather than grepping the raw JSON, which would always miss.
            bool hasSignature = profile.Contains("\"signature\"", StringComparison.Ordinal);
            bool decodesToSkin = false;
            int valueKey = profile.IndexOf("\"value\":\"", StringComparison.Ordinal);
            if (valueKey > 0)
            {
                int vs = valueKey + 9;
                int ve = profile.IndexOf('"', vs);
                if (ve > vs)
                {
                    try
                    {
                        string decoded = System.Text.Encoding.UTF8.GetString(
                            Convert.FromBase64String(profile[vs..ve]));
                        decodesToSkin = decoded.Contains("\"SKIN\"", StringComparison.Ordinal) &&
                                        decoded.Contains("skin.png", StringComparison.Ordinal);
                    }
                    catch
                    {
                        decodesToSkin = false;
                    }
                }
            }

            bool hasTextures = profile.Contains("\"textures\"", StringComparison.Ordinal) &&
                               hasSignature && decodesToSkin;

            Check("local skin server serves signed textures", hasKey && hasTextures,
                $"key={hasKey} profile={hasTextures} signature={hasSignature} " +
                $"decoded={decodesToSkin} port={skinServer.Port}");
        }
        catch (Exception ex)
        {
            Check("local skin server serves signed textures", false, ex.GetType().Name + ": " + ex.Message);
        }

        // --- crash analyser -------------------------------------------------
        // Pure function, so the rules can be pinned down here instead of hoping a
        // real crash happens to use the pattern we guessed was common.
        var oom = CrashAnalyzer.Analyze(1, new[]
        {
            "[12:00:00] [Render thread/ERROR]: java.lang.OutOfMemoryError: Java heap space"
        });
        var mixin = CrashAnalyzer.Analyze(1, new[]
        {
            "org.spongepowered.asm.mixin.transformer.throwables.MixinTransformerError: Critical injection failure"
        });
        var accessViolation = CrashAnalyzer.Analyze(-1073741819, new[] { "[12:00:00] [main/INFO]: Starting" });
        // Exit code 3 has no canned diagnosis, so this really exercises the
        // "nothing matched" path (code 1 does have one).
        var unknown = CrashAnalyzer.Analyze(3, new[] { "[12:00:00] [main/INFO]: nothing to see" });

        // Real 1.8.9 log tail: a corrupt chunk, which looks like a launcher bug to users.
        var corruptChunk = CrashAnalyzer.Analyze(1, new[]
        {
            "[20:00:29] [Server thread/ERROR]: Encountered an unexpected exception",
            "java.lang.NullPointerException",
            "\tat net.minecraft.nbt.NBTTagCompound.func_152449_a(NBTTagCompound.java:414)",
            "\tat net.minecraft.world.chunk.storage.AnvilChunkLoader.loadChunk__Async(AnvilChunkLoader.java:92)"
        });

        Check("crash analyser maps known patterns",
            oom.Recognised && oom.Title.Contains("内存") &&
            mixin.Recognised && mixin.Title.Contains("Mixin") &&
            accessViolation.Recognised && accessViolation.Title.Contains("访问冲突") &&
            !unknown.Recognised,
            $"oom={oom.Title} / mixin={mixin.Title} / code={accessViolation.Title} / unknown={!unknown.Recognised}");

        Check("crash analyser recognises a corrupt chunk",
            corruptChunk.Recognised && corruptChunk.Title.Contains("存档"),
            corruptChunk.Title);

        // --- version plumbing ----------------------------------------------
        Check("app version resolved",
            !string.IsNullOrWhiteSpace(AppInfo.Version) &&
            AppInfo.VersionNumber.Split('.').Length == 3 &&
            AppInfo.VersionNumber.All(c => char.IsDigit(c) || c == '.'),
            $"{AppInfo.Version} / numeric {AppInfo.VersionNumber}");

        // --- JSON request bodies must survive obfuscation -------------------
        // Obfuscar renames anonymous-type constructor parameters (RenameFields), and
        // STJ builds deserialization metadata even for serialize-only calls. That used
        // to kill login in the obfuscated build only:
        //   "The deserialization constructor ... contains parameters with null names"
        // Using JsonObject everywhere avoids the type entirely.
        string probeJson;
        try
        {
            var probeBody = new System.Text.Json.Nodes.JsonObject
            {
                ["Properties"] = new System.Text.Json.Nodes.JsonObject
                {
                    ["AuthMethod"] = "RPS",
                    ["RpsTicket"] = "d=probe"
                },
                ["RelyingParty"] = "http://auth.xboxlive.com",
                ["TokenType"] = "JWT"
            };
            probeJson = probeBody.ToJsonString();
        }
        catch (Exception ex)
        {
            probeJson = "THREW: " + ex.GetType().Name + ": " + ex.Message;
        }

        Check("JSON request body serializes (obfuscation-safe)",
            probeJson.Contains("\"RelyingParty\"", StringComparison.Ordinal) &&
            probeJson.Contains("\"AuthMethod\"", StringComparison.Ordinal),
            probeJson.Length > 72 ? probeJson[..72] + "…" : probeJson);

        // --- offline launch gate (1.20.2+ crashes on a fake token) -----------
        var offlineAcct = Account.CreateOffline("SelfTest");
        Check("offline guard allows 1.20.1",
            !LaunchService.OfflineBlocked(offlineAcct, "1.20.1"));
        Check("offline guard blocks 1.20.2",
            LaunchService.OfflineBlocked(offlineAcct, "1.20.2"));
        Check("offline guard blocks 1.21 (no patch component)",
            LaunchService.OfflineBlocked(offlineAcct, "1.21"));
        Check("offline guard allows 1.7.10",
            !LaunchService.OfflineBlocked(offlineAcct, "1.7.10"));

        // --- long path handling ---------------------------------------------
        Check("long path guard reports the offending path",
            ThrowsWithPath(new string('x', 300)), "guard fires on a long path");

        string loopback = MicrosoftAuthService.ProbeLoopbackAsync().GetAwaiter().GetResult();
        Check("loopback listener captures callback", loopback.StartsWith("PASS", StringComparison.Ordinal),
            loopback);

        string tmp = Path.Combine(Path.GetTempPath(), "ecl-selftest-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tmp);
        try
        {
            string file = Path.Combine(tmp, "accounts.json");
            var store = new AccountStore(file);
            var acc = Account.CreateOffline("TestPlayer");
            acc.Type = "microsoft";
            acc.AccessToken = secret;
            acc.RefreshToken = secret + "-refresh";
            store.Add(acc);
            store.Save();

            // Switching accounts must raise PropertyChanged on IsCurrent, otherwise
            // the row's "使用中" badge never moves and the click looks ignored.
            int badgeNotifications = 0;
            var probeAccount = Account.CreateOffline("Badge");
            probeAccount.PropertyChanged += (_, e) =>
            {
                if (e.PropertyName == nameof(Account.IsCurrent)) badgeNotifications++;
            };
            probeAccount.IsCurrent = true;
            probeAccount.IsCurrent = true;   // same value: must not re-notify
            Check("account switch notifies the badge", badgeNotifications == 1,
                $"notifications={badgeNotifications}");

            string json = File.ReadAllText(file);
            Check("accounts.json has no plaintext token", !json.Contains(secret, StringComparison.Ordinal));
            Check("accounts.json carries sealed fields",
                json.Contains("accessSealed", StringComparison.Ordinal) &&
                json.Contains("refreshSealed", StringComparison.Ordinal));

            var reloaded = new AccountStore(file);
            Check("token survives reload", reloaded.Accounts.FirstOrDefault()?.AccessToken == secret);
            Check("offline UUID deterministic",
                Account.CreateOffline("TestPlayer").Uuid == Account.CreateOffline("TestPlayer").Uuid);

            var victim = new AccountStore(file).Accounts.FirstOrDefault();
            if (victim != null)
            {
                victim.AccessSealed = Convert.ToBase64String(new byte[] { 1, 2, 3, 4, 5 });
                Check("corrupt blob degrades to null", victim.AccessToken == null);
            }
        }
        finally
        {
            try { Directory.Delete(tmp, true); } catch { }
        }

        // --- existing install discovery -------------------------------------
        // "I replaced .minecraft and the launcher shows nothing" turned out to be a
        // path problem, not a parsing one. Guard the finder so it keeps resolving
        // every shape a user might point at.
        {
            string tmp2 = Path.Combine(Path.GetTempPath(), "ecl-find-" + Guid.NewGuid().ToString("N"));
            try
            {
                string vdir = Path.Combine(tmp2, "versions", "1.20.1");
                Directory.CreateDirectory(vdir);
                File.WriteAllText(Path.Combine(vdir, "1.20.1.json"),
                    "{\"id\":\"1.20.1\",\"mainClass\":\"net.minecraft.client.main.Main\",\"type\":\"release\"}");
                File.WriteAllBytes(Path.Combine(vdir, "1.20.1.jar"), new byte[] { 0x50, 0x4B, 0x03, 0x04 });

                var fromRoot = ExistingInstallFinder.Inspect(tmp2);
                Check("existing install finder sees a foreign profile",
                    fromRoot is { VersionCount: 1, ReadyCount: 1 },
                    fromRoot == null ? "(null)" : $"{fromRoot.VersionCount}/{fromRoot.ReadyCount}");

                var fromVersions = ExistingInstallFinder.Inspect(Path.Combine(tmp2, "versions"));
                Check("finder resolves the versions subfolder",
                    fromVersions?.Root == fromRoot?.Root, fromVersions?.Root ?? "(null)");

                var fromProfile = ExistingInstallFinder.Inspect(vdir);
                Check("finder resolves a single version folder",
                    fromProfile?.Root == fromRoot?.Root, fromProfile?.Root ?? "(null)");

                string empty = Path.Combine(tmp2, "not-a-game");
                Directory.CreateDirectory(empty);
                Check("finder rejects an unrelated folder",
                    ExistingInstallFinder.Inspect(empty) == null);

                // A profile without a jar must be counted, but not as launchable.
                string bare = Path.Combine(tmp2, "versions", "1.20.2");
                Directory.CreateDirectory(bare);
                File.WriteAllText(Path.Combine(bare, "1.20.2.json"), "{\"id\":\"1.20.2\"}");
                var mixed = ExistingInstallFinder.Inspect(tmp2);
                Check("missing jar lowers the ready count",
                    mixed is { VersionCount: 2, ReadyCount: 1 },
                    mixed == null ? "(null)" : $"{mixed.VersionCount}/{mixed.ReadyCount}");
            }
            catch (Exception ex)
            {
                Check("existing install finder", false, ex.Message);
            }
            finally
            {
                try { Directory.Delete(tmp2, true); } catch { }
            }
        }

        // --- skin head crop (avatar) ----------------------------------------
        // Runs on the dispatcher thread (RunHeadless), so RenderTargetBitmap is legal.
        try
        {
            string tmpSkin = Path.Combine(Path.GetTempPath(), "ecl-skin-" + Guid.NewGuid().ToString("N") + ".png");
            var pixels = new byte[64 * 64 * 4];   // BGRA, zero = fully transparent

            void SetPx(int x, int y, byte b, byte g, byte r)
            {
                int i = (y * 64 + x) * 4;
                pixels[i] = b; pixels[i + 1] = g; pixels[i + 2] = r; pixels[i + 3] = 255;
            }

            for (int y = 8; y < 16; y++)
                for (int x = 8; x < 16; x++) SetPx(x, y, 0, 0, 255);      // head, red

            for (int y = 8; y < 16; y++)
                for (int x = 40; x < 48; x++) SetPx(x, y, 0, 255, 0);     // hat, green

            var wb = new System.Windows.Media.Imaging.WriteableBitmap(64, 64, 96, 96,
                System.Windows.Media.PixelFormats.Bgra32, null);
            wb.WritePixels(new System.Windows.Int32Rect(0, 0, 64, 64), pixels, 64 * 4, 0);

            var encoder = new System.Windows.Media.Imaging.PngBitmapEncoder();
            encoder.Frames.Add(System.Windows.Media.Imaging.BitmapFrame.Create(wb));
            using (var fs = File.Create(tmpSkin)) encoder.Save(fs);

            var face = Models.SkinHead.FromSkinFile(tmpSkin);
            Check("skin head crops to 8x8", face is { PixelWidth: 8, PixelHeight: 8 },
                face == null
                    ? (Models.SkinHead.LastError ?? "(null)")
                    : $"{face.PixelWidth}x{face.PixelHeight}");

            if (face != null)
            {
                var buffer = new byte[8 * 8 * 4];
                face.CopyPixels(buffer, 8 * 4, 0);

                // The hat is drawn last, so the face must show green, not red.
                bool hatOnTop = buffer[1] > 200 && buffer[2] < 60;
                Check("hat layer composites over the head", hatOnTop,
                    $"b={buffer[0]} g={buffer[1]} r={buffer[2]}");
            }

            try { File.Delete(tmpSkin); } catch { }
        }
        catch (Exception ex)
        {
            Check("skin head crop", false, ex.Message);
        }

        log.WriteLine(failures == 0 ? "=== ALL PASS ===" : $"=== {failures} FAILURE(S) ===");
        return failures == 0 ? 0 : 1;
    }

    private static string Render(string arg) =>
        arg.Length == 0 ? "\"\"" : (arg.Contains(' ') ? "\"" + arg + "\"" : arg);

    /// <summary>
    /// Starts the game for real and streams its output to the log file. The
    /// process is terminated after <paramref name="seconds"/> so the smoke test
    /// cannot leave a window open; a zero exit before the deadline means the
    /// game crashed during start-up.
    /// </summary>
    /// <summary>
    /// Exercises the Microsoft sign-in path without the UI.
    /// With the official client id this is two-step: run once to print the
    /// authorize URL, then run again with <c>--callback &lt;pasted url&gt;</c>.
    /// With a custom client id it runs the PKCE flow directly (opens a browser).
    /// </summary>
    public static async Task<int> LoginAsync(string logPath, string? dataRoot, string? callbackUrl)
    {
        await using var log = new StreamWriter(logPath, append: false) { AutoFlush = true };
        void Write(string s) => log.WriteLine($"[{DateTime.Now:HH:mm:ss.fff}] {s}");

        var settings = LauncherSettings.Load(LauncherSettings.ResolveSettingsPath());
        settings.Normalize();
        if (!string.IsNullOrWhiteSpace(dataRoot)) settings.DataRoot = dataRoot;

        try
        {
            using var auth = new MicrosoftAuthService(settings.MsClientId);
            Write($"client id : {settings.MsClientId}");
            Write($"flow      : {(!auth.IsConfigured ? "NOT CONFIGURED (settings.msClientId is empty)" : auth.UsesLegacyLiveClientId ? "official id + authorize page (paste back)" : "authorization code + PKCE (custom id)")}");

            if (auth.UsesLegacyLiveClientId)
            {
                var (url, state) = auth.CreateLiveAuthorizeRequest();

                if (string.IsNullOrWhiteSpace(callbackUrl))
                {
                    Write("1) 在浏览器打开下面的网址并完成登录：");
                    Write(url);
                    Write("2) 登录后浏览器会跳到 oauth20_desktop.srf 的空白页，复制地址栏整串网址，");
                    Write("   然后这样再跑一次（把网址粘在 --callback 后面）：");
                    Write($"   ECL.exe --login --callback \"<地址栏网址>\" --log \"{logPath}\"");
                    return 2;
                }

                Write("正在用粘贴的授权码换取令牌…");
                // A code pasted from the clipboard is single-use and bound to this
                // client, so cross-run state matching is not meaningful here.
                var account = await auth.CompleteLiveAuthorizeAsync(callbackUrl, expectedState: "",
                        new Progress<string>(Write), CancellationToken.None).ConfigureAwait(false);

                Write($"OK: {account.Name} / {account.Uuid} / flow={account.AuthFlow}");
                return 0;
            }

            var accountPkce = await auth.InteractiveLoginAsync(new Progress<string>(Write), null, CancellationToken.None)
                                        .ConfigureAwait(false);
            Write($"OK: {accountPkce.Name} / {accountPkce.Uuid} / flow={accountPkce.AuthFlow}");
            return 0;
        }
        catch (Exception ex)
        {
            Write("FAILED: " + ex.GetType().Name + ": " + ex.Message);
            return 1;
        }
    }

    /// <summary>
    /// Lists Minecraft layouts found outside the current data root. Diagnostics for
    /// "I replaced .minecraft and the launcher sees nothing".
    /// </summary>
    public static int ListInstalls(string logPath, string? dataRoot)
    {
        using var log = new StreamWriter(logPath, append: false) { AutoFlush = true };

        var settings = LauncherSettings.Load(LauncherSettings.ResolveSettingsPath());
        settings.Normalize();
        if (!string.IsNullOrWhiteSpace(dataRoot)) settings.DataRoot = dataRoot;

        var paths = new GamePaths(settings.DataRoot, settings.IsolateByVersion);

        log.WriteLine($"current root: {paths.Root}");
        var found = ExistingInstallFinder.Discover(paths.Root);
        log.WriteLine($"candidates  : {found.Count}");
        foreach (var c in found)
            log.WriteLine($"  {c.Root}  versions={c.VersionCount} ready={c.ReadyCount}");

        return 0;
    }

    /// <summary>
    /// Takes over an existing install: points the data root at it and turns version
    /// isolation off, so the <c>mods</c> / <c>saves</c> / <c>options.txt</c> already
    /// in that folder are the ones the game will use.
    /// </summary>
    public static int AdoptInstall(string path, string logPath, bool persist)
    {
        using var log = new StreamWriter(logPath, append: false) { AutoFlush = true };

        var found = ExistingInstallFinder.Inspect(path);
        if (found == null)
        {
            log.WriteLine($"FATAL: no Minecraft profiles under {path}");
            log.WriteLine("expected <dir>/versions/<id>/<id>.json, or point at versions/ itself");
            return 3;
        }

        log.WriteLine($"source    : {path}");
        log.WriteLine($"root      : {found.Root}");
        log.WriteLine($"versions  : {found.VersionCount}");
        log.WriteLine($"ready     : {found.ReadyCount}");

        var probes = InstalledVersionScanner.Probe(new GamePaths(found.Root, isolateByVersion: false));
        foreach (var p in probes)
            log.WriteLine($"  [{p.Health,-11}] {p.Id,-32} kind={p.Kind,-8} base={p.BaseVersion}");

        if (!persist)
        {
            log.WriteLine("dry run   : settings untouched");
            return found.ReadyCount > 0 ? 0 : 4;
        }

        string settingsPath = LauncherSettings.ResolveSettingsPath();
        var settings = LauncherSettings.Load(settingsPath);
        settings.Normalize();

        // accounts.json / skins live under the data root, so switching roots would
        // otherwise sign the user out. Same rule as the UI: never overwrite.
        var previous = new GamePaths(settings.DataRoot, settings.IsolateByVersion);
        try
        {
            string targetAccounts = Path.Combine(found.Root, "accounts.json");
            if (File.Exists(previous.AccountsFile) && !File.Exists(targetAccounts))
            {
                File.Copy(previous.AccountsFile, targetAccounts);
                log.WriteLine("carried   : accounts.json");
            }
            else if (File.Exists(targetAccounts))
            {
                log.WriteLine("kept      : accounts.json already present in the target");
            }
        }
        catch (Exception ex)
        {
            log.WriteLine("carry     : skipped (" + ex.Message + ")");
        }

        settings.DataRoot = found.Root;
        settings.IsolateByVersion = false;
        settings.Save(settingsPath);

        log.WriteLine($"settings  : {settingsPath}");
        log.WriteLine($"dataRoot  : {settings.DataRoot}");
        log.WriteLine($"isolate   : {settings.IsolateByVersion}");
        log.WriteLine("DONE");

        return found.ReadyCount > 0 ? 0 : 4;
    }


    /// <summary>
    /// Measures the paths that decide whether the launcher feels fast: version sweep,
    /// plan build, existence check, loader metadata. Numbers go to the log so a later
    /// change can be compared instead of argued about.
    /// </summary>
    public static async Task<int> BenchAsync(string? versionId, string logPath, string? dataRoot, bool hashFiles = false)
    {
        await using var log = new StreamWriter(logPath, append: false) { AutoFlush = true };

        void Line(string s) => log.WriteLine(s);

        double SinceStart()
        {
            try { return (DateTime.Now - System.Diagnostics.Process.GetCurrentProcess().StartTime).TotalMilliseconds; }
            catch { return -1; }
        }

        Line("=== ECL bench ===");
        Line($"process uptime at bench start : {SinceStart():F0} ms");

        var sw = System.Diagnostics.Stopwatch.StartNew();

        var settings = LauncherSettings.Load(LauncherSettings.ResolveSettingsPath());
        settings.Normalize();
        if (!string.IsNullOrWhiteSpace(dataRoot)) settings.DataRoot = dataRoot;
        Line($"settings load               : {sw.ElapsedMilliseconds} ms");

        sw.Restart();
        var paths = new GamePaths(settings.DataRoot, settings.IsolateByVersion);
        paths.EnsureLayout();
        Line($"layout ensure               : {sw.ElapsedMilliseconds} ms");
        Line($"data root                   : {paths.Root}");

        sw.Restart();
        var probes = InstalledVersionScanner.Probe(paths);
        long probeMs = sw.ElapsedMilliseconds;
        Line($"version probe ({probes.Count,2} dirs)     : {probeMs} ms");

        sw.Restart();
        var installed = InstalledVersionScanner.Scan(paths);
        Line($"version scan ({installed.Count,2} usable)   : {sw.ElapsedMilliseconds} ms");

        sw.Restart();
        InstalledVersionScanner.SaveCatalog(paths, probes);
        Line($"catalog write               : {sw.ElapsedMilliseconds} ms");

        string? target = versionId ?? installed.FirstOrDefault()?.Id;
        if (target == null)
        {
            Line("no installed version - skipping plan benchmarks");
        }
        else
        {
            Line($"target version              : {target}");

            using var api = new ApiClient(settings, paths.Root);
            var install = new InstallService(settings, paths, api);

            try
            {
                sw.Restart();
                var (plan, _) = await install.BuildPlanFromLocalAsync(target, null, CancellationToken.None)
                                              .ConfigureAwait(false);
                Line($"plan build ({plan.Files.Count,4} files)   : {sw.ElapsedMilliseconds} ms");

                var verifier = new VersionVerifier(paths, settings, api);

                sw.Restart();
                var check = await verifier.QuickCheckAsync(target, CancellationToken.None).ConfigureAwait(false);
                Line($"quick check ({check.Checked,4} files)    : {sw.ElapsedMilliseconds} ms" +
                     $"   missing={check.Missing}");

                // Hashing 700 MB of assets is dominated by antivirus on-access scans
                // (minutes, not seconds), so it only runs when explicitly asked for.
                if (hashFiles)
                {
                    sw.Restart();
                    var report = await verifier.VerifyAsync(target, null, null, CancellationToken.None)
                                               .ConfigureAwait(false);
                    Line($"full verify w/ hashes       : {sw.ElapsedMilliseconds} ms   {report.Summary}");
                }
                else
                {
                    Line("full verify w/ hashes       : skipped (add --hash)");
                }
            }
            catch (Exception ex)
            {
                // Keep the numbers gathered so far; a missing version is a bench input
                // problem, not a reason to lose the timings above.
                Line($"plan/verify skipped         : {ex.GetType().Name}: {ex.Message}");
            }
        }

        var process = System.Diagnostics.Process.GetCurrentProcess();
        Line($"managed heap                : {GC.GetTotalMemory(false) / 1024.0 / 1024.0:F1} MB");
        Line($"working set                 : {process.WorkingSet64 / 1024.0 / 1024.0:F1} MB");
        Line($"threads                     : {process.Threads.Count}");
        Line("=== bench done ===");
        return 0;
    }

    /// <summary>Writes the diagnostic zip and prints where it went.</summary>
    public static int Diagnose(string outDir, string logPath, string? dataRoot)
    {
        using var log = new StreamWriter(logPath, append: false) { AutoFlush = true };

        var settings = LauncherSettings.Load(LauncherSettings.ResolveSettingsPath());
        settings.Normalize();
        if (!string.IsNullOrWhiteSpace(dataRoot)) settings.DataRoot = dataRoot;

        var paths = new GamePaths(settings.DataRoot, settings.IsolateByVersion);

        try
        {
            string zip = Services.DiagnosticBundle.Create(paths, settings, outDir);
            log.WriteLine("bundle: " + zip);
            log.WriteLine("size  : " + new FileInfo(zip).Length / 1024 + " KB");
            return 0;
        }
        catch (Exception ex)
        {
            log.WriteLine("FAILED: " + ex.GetType().Name + ": " + ex.Message);
            return 1;
        }
    }

    /// <summary>
    /// Crops a skin PNG to the 8x8 face and prints what came out. Diagnoses
    /// "头像不显示" without guessing: either the file is not a skin, or the pixels lie.
    /// </summary>
    public static int SkinHeadProbe(string pngPath, string logPath)
    {
        using var log = new StreamWriter(logPath, append: false) { AutoFlush = true };

        log.WriteLine($"source : {pngPath}");
        if (!File.Exists(pngPath))
        {
            log.WriteLine("FATAL  : file not found");
            return 3;
        }

        var face = Models.SkinHead.FromSkinFile(pngPath);
        if (face == null)
        {
            log.WriteLine($"FATAL  : {Models.SkinHead.LastError ?? "null"}");
            return 4;
        }

        log.WriteLine($"face   : {face.PixelWidth}x{face.PixelHeight} {face.Format}");

        var buffer = new byte[face.PixelWidth * face.PixelHeight * 4];
        face.CopyPixels(buffer, face.PixelWidth * 4, 0);
        log.WriteLine($"px(0,0): b={buffer[0]} g={buffer[1]} r={buffer[2]} a={buffer[3]}");
        log.WriteLine($"px(7,7): b={buffer[^4]} g={buffer[^3]} r={buffer[^2]} a={buffer[^1]}");
        return 0;
    }

    /// <summary>
    /// Prints the loader build list exactly as the install page fetches it, including
    /// which host answered. Diagnoses "加载器版本获取失败" without the UI.
    /// </summary>
    public static async Task<int> LoaderMetaAsync(string kind, string mcVersion, string logPath, string? dataRoot)
    {
        await using var log = new StreamWriter(logPath, append: false) { AutoFlush = true };
        void Write(string s) => log.WriteLine($"[{DateTime.Now:HH:mm:ss.fff}] {s}");

        var settings = LauncherSettings.Load(LauncherSettings.ResolveSettingsPath());
        settings.Normalize();
        if (!string.IsNullOrWhiteSpace(dataRoot)) settings.DataRoot = dataRoot;
        var paths = new GamePaths(settings.DataRoot, settings.IsolateByVersion);

        Write($"loader   : {kind}");
        Write($"minecraft: {mcVersion}");
        Write($"mirror   : {settings.UseMirror} ({settings.MirrorBase})");

        var progress = new Progress<string>(Write);

        try
        {
            switch (kind.ToLowerInvariant())
            {
                case "fabric":
                {
                    var fabric = new FabricInstaller(paths, useMirror: settings.UseMirror);
                    var list = await fabric.GetLoadersAsync(mcVersion, CancellationToken.None, progress)
                                           .ConfigureAwait(false);
                    Write($"loaders  : {list.Count}");
                    foreach (var l in list.Take(5)) Write($"  {l.Version} stable={l.Stable}");
                    return list.Count > 0 ? 0 : 4;
                }
                case "quilt":
                {
                    var quilt = new QuiltInstaller(paths, useMirror: settings.UseMirror);
                    var list = await quilt.GetLoadersAsync(mcVersion, CancellationToken.None, progress)
                                          .ConfigureAwait(false);
                    Write($"loaders  : {list.Count}");
                    foreach (var l in list.Take(5)) Write($"  {l.Version}");
                    return list.Count > 0 ? 0 : 4;
                }
                default:
                    Write($"FATAL: unknown loader '{kind}' (fabric|quilt)");
                    return 3;
            }
        }
        catch (Exception ex)
        {
            Write($"FATAL: {ex.GetType().Name}: {ex.Message}");
            return 5;
        }
    }

    /// <summary>
    /// Sweeps <c>versions/</c>, prints what it found and rewrites
    /// <c>eclversions.config</c>. Same code path the UI runs on start-up.
    /// </summary>
    public static int ScanVersions(string logPath, string? dataRoot)
    {
        using var log = new StreamWriter(logPath, append: false) { AutoFlush = true };

        var settings = LauncherSettings.Load(LauncherSettings.ResolveSettingsPath());
        settings.Normalize();
        if (!string.IsNullOrWhiteSpace(dataRoot)) settings.DataRoot = dataRoot;

        var paths = new GamePaths(settings.DataRoot, settings.IsolateByVersion);
        paths.EnsureLayout();

        var probes = InstalledVersionScanner.Probe(paths);
        InstalledVersionScanner.SaveCatalog(paths, probes);

        log.WriteLine($"data root : {paths.Root}");
        log.WriteLine($"versions  : {paths.VersionsDir}");
        log.WriteLine($"scanned   : {probes.Count}");
        foreach (var p in probes)
            log.WriteLine($"  [{p.Health,-11}] {p.Id,-34} kind={p.Kind,-8} base={p.BaseVersion,-8} " +
                          $"loader={p.LoaderVersion,-14} mods={p.ModCount} jar={p.HasJar} ecl={p.InstalledByEcl}");
        log.WriteLine($"catalog   : {InstalledVersionScanner.CatalogPath(paths)}");
        return 0;
    }

    /// <summary>
    /// Downloads the Temurin JRE a given Minecraft version needs, into
    /// <c>&lt;data root&gt;/runtime/java-&lt;major&gt;</c>. This is exactly what the
    /// launcher does automatically on first play, exposed for verification.
    /// </summary>

    private static readonly string[] LoaderKinds = { "forge", "fabric", "quilt", "optifine", "neoforge" };

    /// <summary>
    /// True when the version folder and its json exist. CLI commands take a version id
    /// from the user, so a typo has to read as "not installed", not as a crash.
    /// </summary>
    private static bool EnsureVersionInstalled(GamePaths paths, string versionId, Action<string> write)
    {
        if (File.Exists(paths.VersionJson(versionId))) return true;

        write($"版本 {versionId} 未安装");
        write($"  期望的版本清单：{paths.VersionJson(versionId)}");
        write("  用 --scan-versions 查看已安装的版本");
        return false;
    }

    /// <summary>
    /// Re-runs the install plan for an installed version. The native downloader skips
    /// files that already hash correctly, so this fetches exactly what is missing.
    /// </summary>
    public static async Task<int> RepairAsync(string versionId, string logPath, string? dataRoot, int? threads)
    {
        await using var log = new StreamWriter(logPath, append: false) { AutoFlush = true };
        void Write(string s) => log.WriteLine($"[{DateTime.Now:HH:mm:ss.fff}] {s}");

        var settings = LauncherSettings.Load(LauncherSettings.ResolveSettingsPath());
        settings.Normalize();
        if (!string.IsNullOrWhiteSpace(dataRoot)) settings.DataRoot = dataRoot;
        if (threads is > 0) settings.DownloadThreads = threads.Value;

        var paths = new GamePaths(settings.DataRoot, settings.IsolateByVersion);
        paths.EnsureLayout();

        if (!EnsureVersionInstalled(paths, versionId, Write)) return 1;

        using var api = new ApiClient(settings, paths.Root);
        var verifier = new VersionVerifier(paths, settings, api);

        try
        {
            int total = await verifier.RepairAsync(versionId, null, new Progress<string>(Write),
                CancellationToken.None).ConfigureAwait(false);
            Write($"RESULT: plan covered {total} files");
            return 0;
        }
        catch (Exception ex)
        {
            Write("RESULT: failed - " + ex.GetType().Name + ": " + ex.Message);
            return 2;
        }
    }

    /// <summary>
    /// Requests a real device code from Microsoft and prints it, then stops. Proves the
    /// sign-in endpoint is reachable and the client id is accepted, without needing a
    /// human to type the code.
    /// </summary>
    /// <param name="waitSeconds">
    /// 0 = request the code and stop. &gt;0 = keep polling that long so a human can
    /// actually enter it, which is the only way to exercise the full chain.
    /// </param>
    public static async Task<int> RequestDeviceCodeAsync(string logPath, int waitSeconds = 0)
    {
        await using var log = new StreamWriter(logPath, append: false) { AutoFlush = true };
        void Write(string s) => log.WriteLine($"[{DateTime.Now:HH:mm:ss.fff}] {s}");

        var settings = LauncherSettings.Load(LauncherSettings.ResolveSettingsPath());
        settings.Normalize();

        using var auth = new MicrosoftAuthService(settings.MsClientId);
        Write($"client id : {settings.MsClientId} (legacy={auth.UsesLegacyLiveClientId})");

        var got = new TaskCompletionSource<bool>();
        var prompt = new Progress<MicrosoftAuthService.DeviceCodePrompt>(p =>
        {
            Write($"USER CODE : {p.UserCode}");
            Write($"VERIFY AT : {p.VerificationUri}");
            Write($"EXPIRES   : {p.ExpiresAt.ToLocalTime():yyyy-MM-dd HH:mm:ss}");
            got.TrySetResult(true);
        });

        int budgetSeconds = waitSeconds > 0 ? waitSeconds + 30 : 25;
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(budgetSeconds));
        var login = auth.DeviceCodeLoginAsync(new Progress<string>(Write), prompt, cts.Token);

        var finished = await Task.WhenAny(got.Task, Task.Delay(20000)).ConfigureAwait(false);
        bool gotCode = finished == got.Task && got.Task.Result;

        if (waitSeconds <= 0)
        {
            cts.Cancel();
            try { await login.ConfigureAwait(false); } catch { /* cancelled on purpose */ }

            if (gotCode) { Write("RESULT: device code issued"); return 0; }
            Write("RESULT: no device code (see messages above)");
            return 1;
        }

        if (!gotCode)
        {
            Write("RESULT: no device code (see messages above)");
            return 1;
        }

        Write($"WAITING  : 请现在到浏览器输入上面的代码（最多 {waitSeconds} 秒）");

        try
        {
            var account = await login.ConfigureAwait(false);
            Write($"RESULT: logged in as {account.Name}");
            Write($"UUID    : {account.Uuid}");
            Write($"TYPE    : {account.TypeLabel}");
            Write($"TOKEN   : {(string.IsNullOrEmpty(account.AccessToken) ? "(none)" : "present")}");
            return 0;
        }
        catch (OperationCanceledException)
        {
            Write("RESULT: cancelled or timed out");
            return 3;
        }
        catch (Exception ex)
        {
            Write("RESULT: failed - " + ex.GetType().Name + ": " + ex.Message);
            return 2;
        }
    }

    /// <summary>
    /// Verifies every file of an installed version against its json, then prints the
    /// report. Exit code 1 when anything is missing or corrupt.
    /// </summary>
    public static async Task<int> VerifyVersionAsync(string versionId, string logPath, string? dataRoot)
    {
        await using var log = new StreamWriter(logPath, append: false) { AutoFlush = true };
        void Write(string s) => log.WriteLine($"[{DateTime.Now:HH:mm:ss.fff}] {s}");

        var settings = LauncherSettings.Load(LauncherSettings.ResolveSettingsPath());
        settings.Normalize();
        if (!string.IsNullOrWhiteSpace(dataRoot)) settings.DataRoot = dataRoot;

        var paths = new GamePaths(settings.DataRoot, settings.IsolateByVersion);
        paths.EnsureLayout();

        if (!EnsureVersionInstalled(paths, versionId, Write)) return 1;

        using var api = new ApiClient(settings, paths.Root);
        var verifier = new VersionVerifier(paths, settings, api);

        try
        {
            var report = await verifier.VerifyAsync(versionId,
                new Progress<string>(Write), null, CancellationToken.None).ConfigureAwait(false);

            Write($"checked : {report.Checked}");
            Write($"ok      : {report.Ok}");
            Write($"bytes   : {report.Bytes / 1024 / 1024} MB");
            Write($"issues  : {report.Issues.Count} (missing {report.Missing}, corrupt {report.Corrupt})");
            foreach (var issue in report.Issues.Take(30))
                Write($"  [{issue.KindLabel}] {issue.ShortPath} {issue.Detail}");
            Write(report.Healthy ? "RESULT: healthy" : "RESULT: problems found");
            return report.Healthy ? 0 : 1;
        }
        catch (Exception ex)
        {
            Write("FAILED: " + ex.GetType().Name + ": " + ex.Message);
            return 2;
        }
    }

    /// <summary>
    /// Lists the mods of one version and reports missing dependencies. Exit code 1
    /// when something is unresolved, so a script can gate on it.
    /// </summary>
    public static int CheckMods(string versionId, string logPath, string? dataRoot)
    {
        using var log = new StreamWriter(logPath, append: false) { AutoFlush = true };

        var settings = LauncherSettings.Load(LauncherSettings.ResolveSettingsPath());
        settings.Normalize();
        if (!string.IsNullOrWhiteSpace(dataRoot)) settings.DataRoot = dataRoot;

        var paths = new GamePaths(settings.DataRoot, settings.IsolateByVersion);
        if (!EnsureVersionInstalled(paths, versionId, log.WriteLine)) return 1;

        var svc = new ModService(paths);
        var mods = svc.List(versionId);

        log.WriteLine($"mods dir : {paths.ModsDir(versionId)}");
        log.WriteLine($"count    : {mods.Count}");
        foreach (var m in mods)
        {
            string deps = string.Join(", ", m.Depends.Select(d => d.Label));
            log.WriteLine($"  {(m.IsEnabled ? "+" : "-")} {m.FileName,-40} id={m.ModId,-24} " +
                          $"platform={m.Platform,-8} depends=[{deps}]");
        }

        var missing = ModService.FindMissingDependencies(mods);
        log.WriteLine("--- missing ---");
        if (missing.Count == 0)
        {
            log.WriteLine("(none)");
            return 0;
        }

        foreach (var item in missing) log.WriteLine("  " + item);
        return 1;
    }

    public static async Task<int> EnsureJavaAsync(int requiredMajor, string logPath, string? dataRoot)
    {
        await using var log = new StreamWriter(logPath, append: false) { AutoFlush = true };
        void Write(string s) => log.WriteLine($"[{DateTime.Now:HH:mm:ss.fff}] {s}");

        var settings = LauncherSettings.Load(LauncherSettings.ResolveSettingsPath());
        settings.Normalize();
        if (!string.IsNullOrWhiteSpace(dataRoot)) settings.DataRoot = dataRoot;

        var paths = new GamePaths(settings.DataRoot, settings.IsolateByVersion);
        paths.EnsureLayout();

        int major = JavaDownloader.TargetMajorFor(requiredMajor);
        Write($"data root  : {paths.Root}");
        Write($"required   : Java {requiredMajor} -> install Java {major}");

        try
        {
            string? javaw = await JavaDownloader.EnsureAsync(major, paths,
                new Progress<string>(Write), CancellationToken.None).ConfigureAwait(false);

            if (javaw == null || !File.Exists(javaw))
            {
                Write("FAILED: javaw.exe missing after install");
                return 1;
            }

            Write($"OK: {javaw}");
            Write($"size: {new FileInfo(javaw).Length / 1024} KB");
            return 0;
        }
        catch (Exception ex)
        {
            Write("FAILED: " + ex.GetType().Name + ": " + ex.Message);
            return 1;
        }
    }

    public static async Task<int> LaunchAsync(string versionId, string logPath, string? dataRoot, int seconds)
    {
        await using var log = new StreamWriter(logPath, append: false) { AutoFlush = true };
        void Write(string s) => log.WriteLine($"[{DateTime.Now:HH:mm:ss.fff}] {s}");

        var settings = LauncherSettings.Load(LauncherSettings.ResolveSettingsPath());
        settings.Normalize();
        if (!string.IsNullOrWhiteSpace(dataRoot)) settings.DataRoot = dataRoot;
        var paths = new GamePaths(settings.DataRoot, settings.IsolateByVersion);

        if (LaunchService.MemoryWarning(settings) is { } memWarn)
            Write("警告：" + memWarn);

        try
        {
            // Same pre-flight as the UI: an incomplete profile used to reach Java and
            // die with NoClassDefFoundError, which read as a launcher bug.
            using (var api = new ApiClient(settings, paths.Root))
            {
                var verifier = new VersionVerifier(paths, settings, api);
                var check = await verifier.QuickCheckAsync(versionId, CancellationToken.None).ConfigureAwait(false);
                Write($"pre-flight: {check.Checked} files, {check.Missing} missing");

                if (!check.Healthy)
                {
                    Write($"pre-flight: repairing {check.Missing} file(s)" +
                          (check.MissingBytes > 0 ? $" (~{check.MissingBytes / 1024.0 / 1024.0:F0} MB)" : ""));
                    await verifier.RepairAsync(versionId, null, new Progress<string>(Write), CancellationToken.None)
                                  .ConfigureAwait(false);

                    var again = await verifier.QuickCheckAsync(versionId, CancellationToken.None).ConfigureAwait(false);
                    Write($"pre-flight: after repair {again.Missing} still missing");
                    if (!again.Healthy)
                    {
                        Write($"FATAL: {again.Missing} file(s) could not be repaired, e.g. {again.FirstSample}");
                        return 7;
                    }
                }
            }

            var resolved = await VersionResolver.ResolveAsync(versionId, paths, null, CancellationToken.None)
                                                .ConfigureAwait(false);

            var java = await EnsureJavaAsync(settings, paths,
                resolved.Detail.JavaVersion?.MajorVersion ?? 8, Write, CancellationToken.None)
                .ConfigureAwait(false);
            if (java == null) { Write("no java runtime"); return 5; }

            var account = Account.CreateOffline(settings.UserName);
            var launcher = new LaunchService(settings, paths);
            var process = launcher.Launch(new LaunchService.LaunchRequest(versionId, resolved, account, java),
                line => Write("  " + line), line => Write("! " + line), out string cmd);
            Write("spawned: " + cmd);

            var deadline = DateTime.UtcNow.AddSeconds(seconds);
            while (DateTime.UtcNow < deadline && !process.HasExited)
                await Task.Delay(200).ConfigureAwait(false);

            if (process.HasExited)
            {
                Write($"game exited early with code {process.ExitCode}");
                return process.ExitCode == 0 ? 0 : 6;
            }

            Write($"still running after {seconds}s - treating as successful start");
            try { process.Kill(entireProcessTree: true); } catch { }
            Write("process terminated by the smoke test");
            return 0;
        }
        catch (Exception ex)
        {
            Write("FAILED: " + ex.GetType().Name + ": " + ex.Message);
            return 1;
        }
    }
}
