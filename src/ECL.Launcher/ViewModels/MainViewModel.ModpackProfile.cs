// SPDX-License-Identifier: MPL-2.0
// Copyright (c) 2026 ChenchenMC <https://github.com/ChenchenMC-cn/EternityCraftLauncher>
using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using ECL.Services;

namespace ECL.ViewModels;

/// <summary>
/// Profile creation for modpacks.
///
/// <para>A pack declares what it needs (Minecraft version + loader). If that profile already
/// exists it is reused; otherwise this builds it the same way the install page does —
/// vanilla base first, then the loader profile, then the libraries and assets it inherits.
/// The alternative (a second implementation inside ModpackService) would drift from the
/// install page, which is the thing this whole file exists to avoid.</para>
/// </summary>
public sealed partial class MainViewModel
{
    private static bool LoaderMatches(LoaderKind kind, string packLoader) => packLoader switch
    {
        "fabric" => kind == LoaderKind.Fabric,
        "quilt" => kind == LoaderKind.Quilt,
        "forge" => kind == LoaderKind.Forge,
        _ => true
    };

    /// <summary>
    /// Returns the profile id the pack should install into, creating it when missing.
    /// Throws with an actionable message when creation is impossible (unknown MC version,
    /// no Java for the Forge installer, unsupported loader).
    /// </summary>
    /// <summary>True when a profile the pack can use is already installed.</summary>
    private InstalledVersion? FindModpackProfile(ModpackIndex index) =>
        InstalledVersions.FirstOrDefault(v =>
            string.Equals(v.BaseVersion, index.Minecraft, StringComparison.OrdinalIgnoreCase) &&
            LoaderMatches(v.Kind, index.Loader) &&
            // Several Forge builds can exist for one MC version; any of them will do.
            (index.Loader != "forge" || v.Id.Contains("-forge-", StringComparison.OrdinalIgnoreCase)));

    private async Task<string> EnsureModpackProfileAsync(ModpackIndex index, CancellationToken ct)
    {
        // 1. reuse an existing profile that already satisfies the pack
        var existing = FindModpackProfile(index);

        if (existing != null)
        {
            Log($"整合包沿用已有档案：{existing.Id}");
            return existing.Id;
        }

        Log($"整合包需要新档案：Minecraft {index.Minecraft} / {index.Loader}");
        StageText = $"准备 {index.Minecraft} {index.Loader}";
        var install = new InstallService(_settings, _paths, _api);
        var ctProgress = SafeProgress.Create<InstallProgress>(p =>
        {
            ProgressValue = p.Download.Fraction;
            StageText = p.Stage;
            StatusText = $"{p.Download.FilesDone} / {p.Download.FilesTotal} 个文件";
        }, ex => Log("进度更新失败（不影响安装）：" + ex.Message));

        // 2. vanilla base — every loader profile inherits from it
        if (!install.IsInstalled(index.Minecraft))
        {
            var manifest = await _api.GetManifestAsync(new Progress<string>(Log), ct).ConfigureAwait(true);
            var entry = manifest.Versions.FirstOrDefault(v =>
                            string.Equals(v.Id, index.Minecraft, StringComparison.OrdinalIgnoreCase))
                        ?? throw new InvalidOperationException(
                            $"官方清单里没有 Minecraft {index.Minecraft}，这个整合包可能太旧或版本号有误。");

            Log($"安装原版 {index.Minecraft}");
            var (vanillaPlan, _) = await install.BuildPlanAsync(index.Minecraft, entry.Url,
                new Progress<string>(Log), ct).ConfigureAwait(true);
            await install.ExecuteAsync(vanillaPlan, ctProgress, new Progress<string>(Log), ct).ConfigureAwait(true);
        }

        // 3. loader profile
        string versionId = index.Loader switch
        {
            "fabric" => await new FabricInstaller(_paths, useMirror: _settings.UseMirror)
                .InstallProfileAsync(index.Minecraft, index.FabricLoader, new Progress<string>(Log), ct)
                .ConfigureAwait(true),

            "quilt" => await new QuiltInstaller(_paths, useMirror: _settings.UseMirror)
                .InstallProfileAsync(index.Minecraft, index.QuiltLoader, new Progress<string>(Log), ct)
                .ConfigureAwait(true),

            "forge" => await InstallForgeForPackAsync(install, index, ct).ConfigureAwait(true),

            _ => index.Minecraft
        };

        // 4. libraries + assets the loader profile inherits from vanilla
        Log($"补齐 {versionId} 的库与资源");
        var (loaderPlan, _) = await install.BuildPlanFromLocalAsync(versionId,
            new Progress<string>(Log), ct).ConfigureAwait(true);
        await install.ExecuteAsync(loaderPlan, ctProgress, new Progress<string>(Log), ct).ConfigureAwait(true);

        RefreshInstalled();
        Log($"档案已就绪：{versionId}");
        return versionId;
    }

    /// <summary>
    /// The Forge installer runs under a real JVM and writes the profile itself; there is no
    /// way around needing Java 17+ here.
    /// </summary>
    private async Task<string> InstallForgeForPackAsync(InstallService install, ModpackIndex index,
        CancellationToken ct)
    {
        int required = await GetRequiredJavaAsync(index.Minecraft).ConfigureAwait(true);
        var java = await EnsureJavaAsync(Math.Max(required, 17), ct).ConfigureAwait(true)
                   ?? throw new InvalidOperationException(
                       "Forge 安装器需要 Java 17 或更高，本机没有可用的运行时。请先到「设置」装一个 Java，再导入这个整合包。");

        var forge = new ForgeInstaller(_paths, null, _settings.UseMirror ? _settings.MirrorBase : null);

        // The pack names a build ("47.3.39"); the installer wants the full raw coordinate
        // ("1.20.1-47.3.39"), so resolve against the real build list instead of guessing.
        var builds = await forge.GetVersionsAsync(index.Minecraft, ct).ConfigureAwait(true);
        var build = builds.FirstOrDefault(b =>
                        !string.IsNullOrWhiteSpace(index.ForgeVersion) &&
                        b.Display.Contains(index.ForgeVersion, StringComparison.OrdinalIgnoreCase))
                    ?? builds.FirstOrDefault();

        if (build == null)
            throw new InvalidOperationException($"Forge 没有 Minecraft {index.Minecraft} 的可用构建。");

        StageText = $"安装 Forge {build.Display}";
        return await forge.InstallAsync(index.Minecraft, build, java.JavaExe,
            new Progress<string>(Log), ct).ConfigureAwait(true);
    }
}
