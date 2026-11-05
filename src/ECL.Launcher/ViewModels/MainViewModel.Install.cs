// SPDX-License-Identifier: MPL-2.0
// Copyright (c) 2026 ChenchenMC <https://github.com/ChenchenMC-cn/EternityCraftLauncher>
using System.Collections.ObjectModel;
using System.IO;
using System.Text.Json;
using ECL.Models;
using ECL.Services;

namespace ECL.ViewModels;

/// <summary>
/// Install page: browse vanilla versions, pick a loader (vanilla / Fabric / Quilt /
/// Forge / OptiFine), then run the matching pipeline.
/// </summary>
public sealed partial class MainViewModel
{
    private readonly List<VersionItem> _allVersions = new();
    private readonly List<OptiFineBuild> _optifineBuilds = new();
    private readonly List<OptiFineBuild> _forgeOptiFineBuilds = new();
    private List<ForgeBuild> _forgeBuilds = new();
    private string? _latestRelease;

    public ObservableCollection<VersionItem> Versions { get; } = new();
    public ObservableCollection<string> LoaderVersions { get; } = new();

    /// <summary>OptiFine editions available for the Forge profile being installed.</summary>
    public ObservableCollection<string> ForgeOptiFineBuilds { get; } = new();

    public System.Windows.Input.ICommand RefreshCommand { get; }
    public System.Windows.Input.ICommand InstallCommand { get; }

    // ------------------------------------------------------------- selection ---

    private VersionItem? _selectedVersion;
    public VersionItem? SelectedVersion
    {
        get => _selectedVersion;
        set
        {
            if (!SetProperty(ref _selectedVersion, value)) return;
            OnPropertyChanged(nameof(HasVersionSelection));
            OnPropertyChanged(nameof(InstallButtonText));
            OnPropertyChanged(nameof(SelectedVersionTitle));
            OnPropertyChanged(nameof(SelectedVersionMeta));
            (InstallCommand as AsyncRelayCommand)?.RaiseCanExecuteChanged();
            _ = ReloadLoaderVersionsAsync();
        }
    }

    public bool HasVersionSelection => _selectedVersion != null;
    public string SelectedVersionTitle => _selectedVersion?.Id ?? "未选择版本";

    public string SelectedVersionMeta
    {
        get
        {
            if (_selectedVersion == null) return "从左侧选择一个 Minecraft 版本";
            var parts = new List<string> { _selectedVersion.TypeLabel, _selectedVersion.ReleaseText };
            if (_selectedVersion.IsInstalled) parts.Add("原版已安装");
            return string.Join("  ·  ", parts);
        }
    }

    private bool _showSnapshots;
    public bool ShowSnapshots
    {
        get => _showSnapshots;
        set
        {
            if (!SetProperty(ref _showSnapshots, value)) return;
            ApplyVersionFilter();
        }
    }

    // ---------------------------------------------------------------- loader ---

    private int _loaderIndex;
    public int LoaderIndex
    {
        get => _loaderIndex;
        set
        {
            if (!SetProperty(ref _loaderIndex, value)) return;
            OnPropertyChanged(nameof(IsVanillaLoader));
            OnPropertyChanged(nameof(IsFabricLoader));
            OnPropertyChanged(nameof(IsQuiltLoader));
            OnPropertyChanged(nameof(IsForgeLoader));
            OnPropertyChanged(nameof(IsOptiFineLoader));
            OnPropertyChanged(nameof(ShowLoaderPicker));
            OnPropertyChanged(nameof(LoaderHint));
            OnPropertyChanged(nameof(InstallButtonText));
            OnPropertyChanged(nameof(ShowForgeOptiFineList));
            _ = ReloadLoaderVersionsAsync();
        }
    }

    private string _loaderSupportHint = "";

    /// <summary>Set when the loader answered with an empty list: "Fabric 不支持 1.8.9".</summary>
    public string LoaderSupportHint
    {
        get => _loaderSupportHint;
        private set
        {
            if (!SetProperty(ref _loaderSupportHint, value)) return;
            OnPropertyChanged(nameof(HasLoaderSupportHint));
        }
    }

    public bool HasLoaderSupportHint => _loaderSupportHint.Length > 0;

    private bool _installOptiFineWithForge = true;
    /// <summary>Forge only: also drop OptiFine into the profile's mods folder.</summary>
    public bool InstallOptiFineWithForge
    {
        get => _installOptiFineWithForge;
        set
        {
            if (!SetProperty(ref _installOptiFineWithForge, value)) return;
            OnPropertyChanged(nameof(ShowForgeOptiFineList));
            OnPropertyChanged(nameof(InstallButtonText));
            // The Forge build list itself is filtered by this flag.
            if (_selectedVersion != null && IsForgeLoader) _ = ReloadLoaderVersionsAsync();
        }
    }

    public bool ShowForgeOptiFineList =>
        IsForgeLoader && InstallOptiFineWithForge && ForgeOptiFineBuilds.Count > 0;

    private string? _selectedForgeOptiFineBuild;
    public string? SelectedForgeOptiFineBuild
    {
        get => _selectedForgeOptiFineBuild;
        set
        {
            if (!SetProperty(ref _selectedForgeOptiFineBuild, value)) return;
            OnPropertyChanged(nameof(InstallButtonText));
            OnPropertyChanged(nameof(ForgeOptiFineDetail));
        }
    }

    public string ForgeOptiFineDetail => _selectedForgeOptiFineBuild == null
        ? ""
        : _forgeOptiFineBuilds.FirstOrDefault(b => b.Edition == _selectedForgeOptiFineBuild)?.FileName ?? "";

    public bool IsVanillaLoader => LoaderIndex == 0;
    public bool IsFabricLoader => LoaderIndex == 1;
    public bool IsQuiltLoader => LoaderIndex == 2;
    public bool IsForgeLoader => LoaderIndex == 3;
    public bool IsOptiFineLoader => LoaderIndex == 4;
    public bool ShowLoaderPicker => LoaderIndex != 0;

    public string LoaderHint => LoaderIndex switch
    {
        1 => Services.Localization.T("Fabric：轻量、启动快，模组生态最大。"),
        2 => Services.Localization.T("Quilt：Fabric 的分支，兼容多数 Fabric 模组。"),
        3 => Services.Localization.T("Forge：需要运行官方安装器打补丁，耗时最长，模组最多。"),
        4 => Services.Localization.T("OptiFine：光影与性能优化，作为独立版本安装（不挂 Forge）。"),
        _ => Services.Localization.T("官方原版，不带任何加载器。")
    };

    private string? _selectedLoaderVersion;
    public string? SelectedLoaderVersion
    {
        get => _selectedLoaderVersion;
        set
        {
            if (!SetProperty(ref _selectedLoaderVersion, value)) return;
            OnPropertyChanged(nameof(InstallButtonText));
            OnPropertyChanged(nameof(SelectedLoaderDetail));
        }
    }

    public string SelectedLoaderDetail
    {
        get
        {
            if (LoaderIndex != 4 || _selectedLoaderVersion == null) return "";
            var build = _optifineBuilds.FirstOrDefault(b => b.Edition == _selectedLoaderVersion);
            return build?.FileName ?? "";
        }
    }

    private bool _isLoadingLoaders;
    public bool IsLoadingLoaders
    {
        get => _isLoadingLoaders;
        private set => SetProperty(ref _isLoadingLoaders, value);
    }

    public string InstallButtonText
    {
        get
        {
            if (_selectedVersion == null) return "选择版本";
            return LoaderIndex switch
            {
                1 => "安装 Fabric",
                2 => "安装 Quilt",
                3 => InstallOptiFineWithForge && SelectedForgeOptiFineBuild != null
                    ? Services.Localization.T("安装 Forge + OptiFine")
                    : Services.Localization.T("安装 Forge"),
                4 => Services.Localization.T("安装 OptiFine"),
                _ => _selectedVersion.IsInstalled ? Services.Localization.T("重新安装原版") : Services.Localization.T("安装原版")
            };
        }
    }

    // ------------------------------------------------------------ version list ---

    private async Task RefreshVersionsAsync()
    {
        if (_api == null) return;
        IsBusy = true;
        ErrorText = "";
        StatusText = "正在获取版本列表…";
        try
        {
            var manifest = await _api.GetManifestAsync(new Progress<string>(Log),
                _cts?.Token ?? CancellationToken.None).ConfigureAwait(true);

            _allVersions.Clear();
            foreach (var entry in manifest.Versions.Where(v => v.Type is "release" or "snapshot"))
                _allVersions.Add(new VersionItem(entry));

            ApplyVersionFilter();
            StatusText = $"已加载 {_allVersions.Count} 个版本";

            SelectedVersion = Versions.FirstOrDefault(v => v.Id == _settings.LastVersion)
                              ?? Versions.FirstOrDefault(v => v.Id == manifest.Latest?.Release)
                              ?? Versions.FirstOrDefault();
            _latestRelease = manifest.Latest?.Release;
        }
        catch (Exception ex)
        {
            ErrorText = ex.Message;
            StatusText = "版本列表加载失败";
            Log("版本列表加载失败：" + ex.Message);
        }
        finally
        {
            IsBusy = false;
        }
    }

    private void ApplyVersionFilter()
    {
        string? keep = _selectedVersion?.Id;
        Versions.Clear();

        var visible = new List<VersionItem>();
        foreach (var v in _allVersions)
        {
            if (!_showSnapshots && !v.IsRelease) continue;
            v.IsInstalled = File.Exists(_paths.VersionJar(v.Id)) &&
                            File.Exists(Path.Combine(_paths.VersionDir(v.Id), ".installed"));
            visible.Add(v);
        }

        // Installed versions float to the top: the one the player used last is
        // then always visible without scrolling past a few hundred new builds.
        foreach (var v in visible.Where(v => v.IsInstalled)) Versions.Add(v);
        foreach (var v in visible.Where(v => !v.IsInstalled)) Versions.Add(v);

        if (keep != null)
        {
            var again = Versions.FirstOrDefault(v => v.Id == keep);
            if (again != null) { SelectedVersion = again; return; }
        }
        if (_selectedVersion == null)
            SelectedVersion = Versions.FirstOrDefault(v => v.Id == _latestRelease) ?? Versions.FirstOrDefault();
    }

    // ------------------------------------------------------------ loader list ---

    private async Task ReloadLoaderVersionsAsync()
    {
        LoaderVersions.Clear();
        _optifineBuilds.Clear();
        SelectedLoaderVersion = null;
        LoaderSupportHint = "";
        // A successful load must clear the banner from a previous failed attempt.
        if (ErrorText.StartsWith("加载器版本列表没取到", StringComparison.Ordinal) ||
            ErrorText.StartsWith("无法获取加载器版本列表", StringComparison.Ordinal))
            ErrorText = "";
        if (_selectedVersion == null || LoaderIndex == 0) return;

        IsLoadingLoaders = true;
        string mcVersion = _selectedVersion.Id;
        try
        {
            switch (LoaderIndex)
            {
                case 1:
                {
                    var fabric = new FabricInstaller(_paths, useMirror: _settings.UseMirror);
                    var loaders = await fabric.GetLoadersAsync(mcVersion, CancellationToken.None)
                                              .ConfigureAwait(true);
                    foreach (var l in loaders.Take(40)) LoaderVersions.Add(l.Version);
                    SelectedLoaderVersion = LoaderVersions.FirstOrDefault();
                    LoaderSupportHint = LoaderVersions.Count == 0
                        ? $"Fabric 不支持 Minecraft {mcVersion}（Fabric 从 1.14 起才有 loader）"
                        : "";
                    break;
                }
                case 2:
                {
                    var quilt = new QuiltInstaller(_paths, useMirror: _settings.UseMirror);
                    var loaders = await quilt.GetLoadersAsync(mcVersion, CancellationToken.None)
                                             .ConfigureAwait(true);
                    // Stable builds first, then the betas, so the default pick is sane.
                    foreach (var l in loaders.Where(l => QuiltInstaller.IsStable(l.Version)).Take(20))
                        LoaderVersions.Add(l.Version);
                    foreach (var l in loaders.Where(l => !QuiltInstaller.IsStable(l.Version)).Take(20))
                        LoaderVersions.Add(l.Version);
                    SelectedLoaderVersion = LoaderVersions.FirstOrDefault();
                    LoaderSupportHint = LoaderVersions.Count == 0
                        ? $"Quilt 不支持 Minecraft {mcVersion}"
                        : "";
                    break;
                }
                case 3:
                {
                    var forge = new ForgeInstaller(_paths, null, _settings.UseMirror ? _settings.MirrorBase : null);
                    var builds = await forge.GetVersionsAsync(mcVersion, CancellationToken.None)
                                            .ConfigureAwait(true);

                    if (InstallOptiFineWithForge)
                    {
                        // OptiFine breaks on Forge 47.4+ (see ForgeInstaller.IsOptiFineCompatible),
                        // so hide those builds instead of letting the user pick a crash.
                        var safe = builds.Where(b => ForgeInstaller.IsOptiFineCompatible(b.Display)).ToList();
                        int dropped = builds.Count - safe.Count;
                        if (dropped > 0)
                            Log($"已排除 {dropped} 个与 OptiFine 不兼容的 Forge 构建（47.4+ 已知冲突）");
                        builds = safe;
                    }

                    _forgeBuilds = builds;
                    foreach (var b in builds.Take(40)) LoaderVersions.Add(b.Display);
                    var recommended = await forge.GetRecommendedAsync(mcVersion, CancellationToken.None)
                                                 .ConfigureAwait(true);
                    SelectedLoaderVersion = recommended != null && LoaderVersions.Contains(recommended)
                        ? recommended
                        : LoaderVersions.FirstOrDefault();
                    LoaderSupportHint = LoaderVersions.Count == 0
                        ? $"Forge 没有为 Minecraft {mcVersion} 提供构建"
                        : "";
                    await ReloadForgeOptiFineAsync(mcVersion).ConfigureAwait(true);
                    break;
                }
                case 4:
                {
                    var opti = new OptiFineInstaller(_paths);
                    var builds = await opti.GetBuildsAsync(mcVersion, CancellationToken.None).ConfigureAwait(true);
                    _optifineBuilds.AddRange(builds);
                    foreach (var b in builds) LoaderVersions.Add(b.Edition);
                    SelectedLoaderVersion = LoaderVersions.FirstOrDefault();
                    LoaderSupportHint = LoaderVersions.Count == 0
                        ? $"OptiFine 没有为 Minecraft {mcVersion} 提供构建（去 optifine.net 确认一下）"
                        : "";
                    break;
                }
            }
        }
        catch (Exception ex)
        {
            // A transient timeout used to leave both a red banner and a scary log line
            // even after the next attempt succeeded ("明明无法获取，过一会又出来了").
            Log("加载器列表这次没取到（切版本或点重新检测会自动重试）：" + ex.Message);
            ErrorText = "加载器版本列表没取到，可以再点一次「重新检测」。" + ex.Message;
            LoaderSupportHint = "";
        }
        finally
        {
            IsLoadingLoaders = false;
        }
    }

    private async Task ReloadForgeOptiFineAsync(string mcVersion)
    {
        ForgeOptiFineBuilds.Clear();
        _forgeOptiFineBuilds.Clear();
        SelectedForgeOptiFineBuild = null;
        if (!InstallOptiFineWithForge)
        {
            OnPropertyChanged(nameof(ShowForgeOptiFineList));
            return;
        }

        try
        {
            var builds = await new OptiFineInstaller(_paths).GetBuildsAsync(mcVersion, CancellationToken.None)
                                                             .ConfigureAwait(true);
            _forgeOptiFineBuilds.AddRange(builds);
            foreach (var b in builds) ForgeOptiFineBuilds.Add(b.Edition);
            SelectedForgeOptiFineBuild = ForgeOptiFineBuilds.FirstOrDefault();
            if (ForgeOptiFineBuilds.Count == 0)
                Log($"OptiFine 没有为 {mcVersion} 提供构建，将只安装 Forge");
        }
        catch (Exception ex)
        {
            Log("OptiFine 构建列表获取失败：" + ex.Message);
        }

        OnPropertyChanged(nameof(ShowForgeOptiFineList));
        OnPropertyChanged(nameof(InstallButtonText));
    }

    // --------------------------------------------------------------- install ---

    private async Task InstallSelectedAsync()
    {
        if (_selectedVersion == null || _api == null) return;

        ErrorText = "";
        _cts = new CancellationTokenSource();
        IsBusy = true;
        string mcVersion = _selectedVersion.Id;
        string[] loaderNames = { "", "Fabric", "Quilt", "Forge", "OptiFine" };
        string loaderName = LoaderIndex is > 0 and < 5 ? loaderNames[LoaderIndex] : "";
        var task = BeginTask(loaderName.Length == 0 ? $"安装 {mcVersion}" : $"安装 {mcVersion} + {loaderName}");

        try
        {
            var install = new InstallService(_settings, _paths, _api);
            // SafeProgress: a throw in here lands on the dispatcher, outside the
            // try/catch below, and used to pop the crash dialog mid-install.
            var progress = SafeProgress.Create<InstallProgress>(p =>
            {
                StageText = p.Stage;
                ProgressValue = p.Download.Fraction;
                SpeedText = FormatSpeed(p.Download.BytesPerSecond);
                StatusText = $"{p.Download.FilesDone} / {p.Download.FilesTotal} 个文件";
            }, ex => Log("进度更新失败（不影响安装）：" + ex.Message));

            // ---- step 1: vanilla base ----
            if (!install.IsInstalled(mcVersion))
            {
                Log($"开始安装原版 {mcVersion}");
                var (plan, _) = await install.BuildPlanAsync(mcVersion, _selectedVersion.Url,
                    new Progress<string>(Log), _cts.Token).ConfigureAwait(true);
                await install.ExecuteAsync(plan, progress, new Progress<string>(Log), _cts.Token)
                             .ConfigureAwait(true);
                install.MarkInstalled(mcVersion);
                Log($"原版 {mcVersion} 安装完成");
            }
            else
            {
                Log($"原版 {mcVersion} 已安装，跳过");
            }

            string targetId = mcVersion;

            // ---- step 2: loader ----
            switch (LoaderIndex)
            {
                case 1:
                    targetId = await InstallProfileLoaderAsync("fabric", install, mcVersion, progress)
                        .ConfigureAwait(true);
                    break;
                case 2:
                    targetId = await InstallProfileLoaderAsync("quilt", install, mcVersion, progress)
                        .ConfigureAwait(true);
                    break;
                case 3:
                    targetId = await InstallForgeAsync(install, mcVersion, progress).ConfigureAwait(true);
                    break;
                case 4:
                    targetId = await InstallOptiFineAsync(mcVersion).ConfigureAwait(true);
                    break;
            }

            install.MarkInstalled(targetId, mcVersion);
            RefreshInstalled();
            var installed = InstalledVersions.FirstOrDefault(v => v.Id == targetId);
            if (installed != null) SelectedInstalled = installed;

            StageText = "";
            SpeedText = "";
            ProgressValue = 0;
            StatusText = $"安装完成：{targetId}";
            Log($"安装完成：{targetId}");
            EndTask(task, TaskState.Done, $"完成：{targetId}");
            ActivePage = 0;
        }
        catch (OperationCanceledException)
        {
            StatusText = "安装已取消";
            Log("安装被取消");
            EndTask(task, TaskState.Cancelled, "已取消");
        }
        catch (Exception ex)
        {
            ErrorText = ex.Message;
            StatusText = "安装失败";
            Log("安装失败：" + ex);
            EndTask(task, TaskState.Failed, ex.Message);
        }
        finally
        {
            IsBusy = false;
            _cts?.Dispose();
            _cts = null;
        }
    }

    /// <summary>Fabric and Quilt share the "meta publishes a ready profile" pipeline.</summary>
    private async Task<string> InstallProfileLoaderAsync(string kind, InstallService install,
        string mcVersion, IProgress<InstallProgress> progress)
    {
        StageText = kind == "quilt" ? "准备 Quilt" : "准备 Fabric";
        string versionId;

        if (kind == "quilt")
        {
            var quilt = new QuiltInstaller(_paths, useMirror: _settings.UseMirror);
            versionId = await quilt.InstallProfileAsync(mcVersion, SelectedLoaderVersion,
                new Progress<string>(Log), _cts!.Token).ConfigureAwait(true);
        }
        else
        {
            var fabric = new FabricInstaller(_paths, useMirror: _settings.UseMirror);
            versionId = await fabric.InstallProfileAsync(mcVersion, SelectedLoaderVersion,
                new Progress<string>(Log), _cts!.Token).ConfigureAwait(true);
        }

        Log($"规划 {kind} 依赖（继承自 {mcVersion}）");
        var (plan, _) = await install.BuildPlanFromLocalAsync(versionId, new Progress<string>(Log), _cts.Token)
                                     .ConfigureAwait(true);
        await install.ExecuteAsync(plan, progress, new Progress<string>(Log), _cts.Token).ConfigureAwait(true);
        return versionId;
    }

    private async Task<string> InstallForgeAsync(InstallService install, string mcVersion,
        IProgress<InstallProgress> progress)
    {
        if (string.IsNullOrEmpty(SelectedLoaderVersion))
            throw new InvalidOperationException("没有可用的 Forge 版本。");

        if (InstallOptiFineWithForge && !ForgeInstaller.IsOptiFineCompatible(SelectedLoaderVersion))
            throw new InvalidOperationException(
                $"Forge {SelectedLoaderVersion} 与 OptiFine 有已知冲突（启动即崩）。" +
                "请改选 47.3 或更早的构建，或者关掉「同时装 OptiFine」");

        int required = await GetRequiredJavaAsync(mcVersion).ConfigureAwait(true);
        var java = await EnsureJavaAsync(Math.Max(required, 17), _cts!.Token).ConfigureAwait(true);
        if (java == null)
            throw new InvalidOperationException("Forge 安装器需要 Java 17 或更高，请先安装。");

        var build = _forgeBuilds.FirstOrDefault(b => b.Display == SelectedLoaderVersion)
                    ?? new ForgeBuild(SelectedLoaderVersion, SelectedLoaderVersion);

        StageText = "运行 Forge 安装器";
        var forge = new ForgeInstaller(_paths, null, _settings.UseMirror ? _settings.MirrorBase : null);
        string versionId = await forge.InstallAsync(mcVersion, build, java.JavaExe,
            new Progress<string>(Log), _cts.Token).ConfigureAwait(true);

        // OptiFine rides along as a mod: no installer run, just a jar in mods/.
        if (InstallOptiFineWithForge && SelectedForgeOptiFineBuild != null)
        {
            var ofBuild = _forgeOptiFineBuilds.FirstOrDefault(b => b.Edition == SelectedForgeOptiFineBuild);
            if (ofBuild != null)
            {
                StageText = $"把 OptiFine {ofBuild.Edition} 加入 mods";
                try
                {
                    var opti = new OptiFineInstaller(_paths);
                    string dest = await opti.InstallAsForgeModAsync(mcVersion, ofBuild.FileName, versionId,
                        new Progress<string>(Log), _cts!.Token).ConfigureAwait(true);
                    Log("OptiFine 已作为模组安装：" + dest);
                }
                catch (Exception ex)
                {
                    // Forge itself is installed and usable; do not throw away that
                    // work because the optional extra failed.
                    Log("OptiFine 模组安装失败（Forge 本身可用）：" + ex.Message);
                    ErrorText = "Forge 装好了，但 OptiFine 没装上：" + ex.Message;
                }
            }
        }

        // Modern installers fetch their own libraries; the legacy 1.7.x path does
        // not, and re-planning is idempotent (existing files are skipped by size),
        // so always reconcile here.
        StageText = "补齐 Forge 依赖";
        var (forgePlan, _) = await install.BuildPlanFromLocalAsync(versionId, new Progress<string>(Log),
            _cts!.Token).ConfigureAwait(true);
        await install.ExecuteAsync(forgePlan, progress, new Progress<string>(Log), _cts.Token)
                     .ConfigureAwait(true);

        return versionId;
    }

    private async Task<string> InstallOptiFineAsync(string mcVersion)
    {
        if (string.IsNullOrEmpty(SelectedLoaderVersion))
            throw new InvalidOperationException("没有可用的 OptiFine 构建。");

        var build = _optifineBuilds.FirstOrDefault(b => b.Edition == SelectedLoaderVersion)
                    ?? throw new InvalidOperationException("找不到选中的 OptiFine 构建，请刷新后重试。");

        int required = await GetRequiredJavaAsync(mcVersion).ConfigureAwait(true);
        var java = await EnsureJavaAsync(Math.Max(required, 8), _cts!.Token).ConfigureAwait(true);
        if (java == null)
            throw new InvalidOperationException("装 OptiFine 需要 Java，先到「设置 → Java」准备一个。");

        StageText = "下载并安装 OptiFine";
        var optifine = new OptiFineInstaller(_paths);
        return await optifine.InstallAsync(mcVersion, build.FileName, java.JavaExe,
            new Progress<string>(Log), _cts.Token).ConfigureAwait(true);
    }

    private async Task<int> GetRequiredJavaAsync(string versionId)
    {
        try
        {
            string path = _paths.VersionJson(versionId);
            if (!File.Exists(path)) return 8;
            string json = await File.ReadAllTextAsync(path).ConfigureAwait(true);
            using var doc = JsonDocument.Parse(json);
            if (doc.RootElement.TryGetProperty("javaVersion", out var jv) &&
                jv.TryGetProperty("majorVersion", out var mv) && mv.TryGetInt32(out int major))
                return major;
        }
        catch
        {
            // fall through
        }
        return 8;
    }
}
