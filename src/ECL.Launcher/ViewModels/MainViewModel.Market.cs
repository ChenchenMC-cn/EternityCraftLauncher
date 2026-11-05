// SPDX-License-Identifier: MPL-2.0
// Copyright (c) 2026 ChenchenMC <https://github.com/ChenchenMC-cn/EternityCraftLauncher>
using System.Collections.ObjectModel;
using ECL.Services;

namespace ECL.ViewModels;

/// <summary>
/// Modrinth search &amp; one-click install, surfaced as an expandable panel on the
/// mods page. Only Fabric / Quilt / Forge profiles can load mods, so the panel is
/// gated on those kinds.
/// </summary>
public sealed partial class MainViewModel
{
    private ModrinthService? _market;

    private ModrinthService Market => _market ??= new ModrinthService(_paths);

    public ObservableCollection<ModrinthHit> MarketResults { get; } = new();

    public bool CanSearchMods =>
        _selectedInstalled is { Kind: LoaderKind.Fabric or LoaderKind.Quilt or LoaderKind.Forge };

    public string MarketFilterText => _selectedInstalled == null
        ? ""
        : $"游戏版本 {_selectedInstalled.BaseVersion}" +
          (ModrinthService.LoaderCategory(_selectedInstalled.Kind) is { } l ? $"  ·  {l}" : "");

    public string MarketUnavailableHint => _selectedInstalled == null
        ? Services.Localization.T("先选择一个版本")
        : "原版和 OptiFine 单装版本不能加载模组，请改用 Fabric / Quilt / Forge 版本";

    private bool _isMarketOpen;
    public bool IsMarketOpen
    {
        get => _isMarketOpen;
        set
        {
            if (!SetProperty(ref _isMarketOpen, value)) return;
            OnPropertyChanged(nameof(MarketToggleLabel));
        }
    }

    public string MarketToggleLabel => IsMarketOpen ? Services.Localization.T("收起市场") : Services.Localization.T("从 Modrinth 安装");

    private string _modSearchQuery = "";
    public string ModSearchQuery
    {
        get => _modSearchQuery;
        set => SetProperty(ref _modSearchQuery, value);
    }

    private bool _isSearching;
    public bool IsSearching
    {
        get => _isSearching;
        private set => SetProperty(ref _isSearching, value);
    }

    private bool _isMarketBusy;
    public bool IsMarketBusy
    {
        get => _isMarketBusy;
        private set
        {
            if (!SetProperty(ref _isMarketBusy, value)) return;

            // Without this the install buttons stay enabled while a download runs,
            // so a second click starts a parallel download into the same folder.
            (InstallVersionCommand as AsyncRelayCommand)?.RaiseCanExecuteChanged();
            (InstallMarketModCommand as AsyncRelayCommand)?.RaiseCanExecuteChanged();
        }
    }

    public bool HasMarketResults => MarketResults.Count > 0;
    public bool HasNoMarketResults => !HasMarketResults && !IsSearching;

    private string _marketStatus = "";
    public string MarketStatus
    {
        get => _marketStatus;
        private set => SetProperty(ref _marketStatus, value);
    }

    // ------------------------------------------------------------- commands ---

    public async Task SearchMarketAsync()
    {
        if (_selectedInstalled == null || !CanSearchMods)
        {
            ErrorText = "当前版本不能加载模组（需要 Fabric / Quilt / Forge）。";
            return;
        }

        string query = ModSearchQuery.Trim();
        if (query.Length == 0) return;

        ErrorText = "";
        IsSearching = true;
        MarketStatus = "正在搜索 Modrinth…";
        MarketResults.Clear();
        OnPropertyChanged(nameof(HasMarketResults));
        OnPropertyChanged(nameof(HasNoMarketResults));

        try
        {
            var hits = await Market.SearchAsync(query, _selectedInstalled.BaseVersion,
                ModrinthService.LoaderCategory(_selectedInstalled.Kind),
                ct: CancellationToken.None).ConfigureAwait(true);

            foreach (var h in hits) MarketResults.Add(h);
            MarketStatus = hits.Count == 0 ? "没有结果，换个关键词试试" : $"找到 {hits.Count} 个结果";
            Log($"Modrinth 搜索「{query}」：{hits.Count} 个结果");
        }
        catch (Exception ex)
        {
            ErrorText = "搜索失败：" + ex.Message;
            MarketStatus = "搜索失败";
            Log("Modrinth 搜索失败：" + ex.Message);
        }
        finally
        {
            IsSearching = false;
            OnPropertyChanged(nameof(HasMarketResults));
            OnPropertyChanged(nameof(HasNoMarketResults));
        }
    }

    private async Task InstallMarketModAsync(object? parameter)
    {
        if (parameter is not ModrinthHit hit || _selectedInstalled == null) return;

        var id = _selectedInstalled.Id;
        var kind = _selectedInstalled.Kind;
        var gameVersion = _selectedInstalled.BaseVersion;

        ErrorText = "";
        IsMarketBusy = true;
        StatusText = $"正在下载 {hit.Title}…";
        StageText = hit.Title;
        ProgressValue = 0;
        SpeedText = "";

        using var cts = new CancellationTokenSource();
        try
        {
            var entry = await Market.InstallAsync(id, hit.Slug, gameVersion,
                ModrinthService.LoaderCategory(kind),
                SafeProgress.Create<(long Done, long Total)>(p =>
                {
                    ProgressValue = p.Total > 0 ? (double)p.Done / p.Total : 0;
                    SpeedText = FormatSpeed(0);   // size-known files only; live rate not measured
                    StatusText = p.Total > 0
                        ? $"{p.Done / 1024.0 / 1024.0:F1} / {p.Total / 1024.0 / 1024.0:F1} MB"
                        : $"{p.Done / 1024.0 / 1024.0:F1} MB";
                }, ex => Log("进度更新失败（不影响下载）：" + ex.Message)), cts.Token).ConfigureAwait(true);

            RefreshMods();
            StageText = "";
            SpeedText = "";
            ProgressValue = 0;
            StatusText = $"已安装 {entry.Title}";
            MarketStatus = $"已安装 {entry.Title}（{entry.PlatformLabel}）";
            Log($"Modrinth 安装完成：{entry.FileName}（{entry.PlatformLabel}）");
        }
        catch (Exception ex)
        {
            ErrorText = "安装失败：" + ex.Message;
            MarketStatus = "安装失败";
            Log("Modrinth 安装失败：" + ex.Message);
        }
        finally
        {
            IsMarketBusy = false;
            StageText = "";
            cts.Dispose();
        }
    }

    /// <summary>Re-raises the market bindings after the selection or mod list changes.</summary>
    private void RaiseMarketState()
    {
        OnPropertyChanged(nameof(CanSearchMods));
        OnPropertyChanged(nameof(MarketFilterText));
        OnPropertyChanged(nameof(MarketUnavailableHint));
        (SearchMarketCommand as AsyncRelayCommand)?.RaiseCanExecuteChanged();
    }

    // ========================================================= version picker ===

    /// <summary>Visible rows of the version picker (filtered by <see cref="ShowPrerelease"/>).</summary>
    public ObservableCollection<ModrinthVersion> MarketVersions { get; } = new();

    private List<ModrinthVersion> _allMarketVersions = new();

    private ModrinthHit? _pickedProject;
    public ModrinthHit? PickedProject
    {
        get => _pickedProject;
        private set
        {
            if (!SetProperty(ref _pickedProject, value)) return;
            OnPropertyChanged(nameof(PickerTitle));
            OnPropertyChanged(nameof(HasPickedProject));
        }
    }

    public string PickerTitle => _pickedProject == null
        ? "选择版本"
        : $"{_pickedProject.Title} · 选择版本";

    public bool HasPickedProject => _pickedProject != null;

    private bool _isVersionPickerOpen;
    public bool IsVersionPickerOpen
    {
        get => _isVersionPickerOpen;
        private set => SetProperty(ref _isVersionPickerOpen, value);
    }

    /// <summary>Off by default: stable builds are what people actually want.</summary>
    private bool _showPrerelease;
    public bool ShowPrerelease
    {
        get => _showPrerelease;
        set
        {
            if (!SetProperty(ref _showPrerelease, value)) return;
            ApplyMarketVersionFilter();
        }
    }

    public bool HasMarketVersions => MarketVersions.Count > 0;
    public bool HasNoMarketVersions => !HasMarketVersions && !IsMarketBusy;

    private string _pickerStatus = "";
    public string PickerStatus
    {
        get => _pickerStatus;
        private set => SetProperty(ref _pickerStatus, value);
    }

    public System.Windows.Input.ICommand OpenVersionPickerCommand { get; private set; } = null!;
    public System.Windows.Input.ICommand CloseVersionPickerCommand { get; private set; } = null!;
    public System.Windows.Input.ICommand InstallVersionCommand { get; private set; } = null!;

    private void InitVersionPicker()
    {
        OpenVersionPickerCommand = new AsyncRelayCommand(p => OpenVersionPickerAsync(p as ModrinthHit));
        CloseVersionPickerCommand = new RelayCommand(CloseVersionPicker);
        InstallVersionCommand = new AsyncRelayCommand(p => InstallVersionAsync(p as ModrinthVersion),
            p => p is ModrinthVersion { HasFile: true } && !IsMarketBusy);
    }

    /// <summary>Screenshot hook: fills the picker with sample rows (no network).</summary>
    public void PreparePickerShot()
    {
        PickedProject = new ModrinthHit("sodium", "sodium", "Sodium", "jellysquid3",
            210_000_000, "现代渲染优化模组，大幅提升帧率", "2026-09-01");

        ModrinthFile File(string v) => new($"https://example.invalid/{v}.jar", $"sodium-{v}.jar", 1_234_567, true);

        _allMarketVersions = new List<ModrinthVersion>
        {
            new("a1", "Sodium 0.6.9", "mc1.20.1-0.6.9", "release",
                new[] { "1.20.1" }, new[] { "fabric" }, 3_210_000, "2026-08-20", File("0.6.9")),
            new("a2", "Sodium 0.6.5", "mc1.20.1-0.6.5", "release",
                new[] { "1.20.1" }, new[] { "fabric" }, 2_950_000, "2026-06-11", File("0.6.5")),
            new("a3", "Sodium 0.6.0-beta.3", "mc1.20.1-0.6.0-beta.3", "beta",
                new[] { "1.20.1" }, new[] { "fabric" }, 41_000, "2026-04-02", File("0.6.0-beta.3")),
            new("a4", "Sodium 0.5.11", "mc1.20.1-0.5.11", "release",
                new[] { "1.20.1", "1.20" }, new[] { "fabric", "quilt" }, 1_880_000, "2026-01-15", File("0.5.11"))
        };

        ApplyMarketVersionFilter();
        ShowPrerelease = false;
        IsVersionPickerOpen = true;
        PickerStatus = $"{_allMarketVersions.Count} 个版本（3 个正式版）";
    }

    /// <summary>Screenshot hook: shows the dependency prompt with sample entries.</summary>
    public void PrepareDependencyShot()
    {
        MissingDependencies.Clear();
        MissingDependencies.Add(new MissingDependency("cloth-config", ">=11.0.0", "Sodium"));
        MissingDependencies.Add(new MissingDependency("fabric-api", "", "Iris Shaders"));
        MissingDependencies.Add(new MissingDependency("yet_another_config_lib_v3", ">=3.5.0", "Zoomify"));

        OnPropertyChanged(nameof(DependencyPromptTitle));
        IsDependencyPromptOpen = true;
    }

    // ===================================================== dependency prompt ===

    public ObservableCollection<MissingDependency> MissingDependencies { get; } = new();

    private bool _isDependencyPromptOpen;
    public bool IsDependencyPromptOpen
    {
        get => _isDependencyPromptOpen;
        private set => SetProperty(ref _isDependencyPromptOpen, value);
    }

    public string DependencyPromptTitle => MissingDependencies.Count == 1
        ? "缺少 1 个前置模组"
        : $"缺少 {MissingDependencies.Count} 个前置模组";

    public System.Windows.Input.ICommand DismissDependencyPromptCommand { get; private set; } = null!;
    public System.Windows.Input.ICommand ResolveDependencyCommand { get; private set; } = null!;

    private void InitDependencyPrompt()
    {
        DismissDependencyPromptCommand = new RelayCommand(() => IsDependencyPromptOpen = false);

        // Jump straight into the market with the mod id pre-filled and the version
        // picker open, so resolving one dependency is two clicks.
        ResolveDependencyCommand = new AsyncRelayCommand(p => ResolveDependencyAsync(p as MissingDependency));
    }

    private async Task ResolveDependencyAsync(MissingDependency? missing)
    {
        if (missing == null || _selectedInstalled == null) return;

        IsDependencyPromptOpen = false;
        IsMarketOpen = true;
        ModSearchQuery = missing.ModId;

        await SearchMarketAsync().ConfigureAwait(true);

        var first = MarketResults.FirstOrDefault();
        if (first != null) await OpenVersionPickerAsync(first).ConfigureAwait(true);
        else MarketStatus = $"没搜到「{missing.ModId}」，可能需要手动下载";
    }

    /// <summary>
    /// After an install, re-check and offer a prompt instead of letting the user
    /// discover the gap as a crash at launch.
    /// </summary>
    private async Task ShowDependencyPromptAsync()
    {
        await Task.Yield();

        var missing = ModService.FindMissingDependenciesDetailed(Mods.ToList());
        if (missing.Count == 0) return;

        MissingDependencies.Clear();
        foreach (var m in missing) MissingDependencies.Add(m);

        OnPropertyChanged(nameof(DependencyPromptTitle));
        IsDependencyPromptOpen = true;
        Log("安装后检查：缺少 " + string.Join("；", missing.Select(m => m.Detail)));
    }

    private int _pickerRequestId;

    /// <summary>The version the picker's list was built for; guards against installing
    /// into a different version after the selection changed underneath it.</summary>
    private string? _pickerVersionId;

    private void CloseVersionPicker()
    {
        IsVersionPickerOpen = false;
        PickedProject = null;
        _pickerVersionId = null;
        MarketVersions.Clear();
        _allMarketVersions.Clear();
        PickerStatus = "";
        OnPropertyChanged(nameof(HasMarketVersions));
        OnPropertyChanged(nameof(HasNoMarketVersions));
    }

    private async Task OpenVersionPickerAsync(ModrinthHit? hit)
    {
        if (hit == null || _selectedInstalled == null) return;

        // Clicking two projects in quick succession must not let the slower request
        // overwrite the faster one's list.
        int request = ++_pickerRequestId;
        string pickedVersionId = _selectedInstalled.Id;

        PickedProject = hit;
        _pickerVersionId = pickedVersionId;
        IsVersionPickerOpen = true;
        PickerStatus = "正在读取版本列表…";
        MarketVersions.Clear();
        _allMarketVersions.Clear();
        OnPropertyChanged(nameof(HasMarketVersions));
        OnPropertyChanged(nameof(HasNoMarketVersions));

        try
        {
            var versions = await Market.ListVersionsAsync(hit.Slug,
                _selectedInstalled.BaseVersion,
                ModrinthService.LoaderCategory(_selectedInstalled.Kind)).ConfigureAwait(true);

            if (request != _pickerRequestId) return;   // a newer click won

            _allMarketVersions = versions;
            ApplyMarketVersionFilter();

            PickerStatus = versions.Count == 0
                ? $"没有适配 {_selectedInstalled.BaseVersion} 的版本"
                : $"{versions.Count} 个版本（{versions.Count(v => v.IsRelease)} 个正式版）";
            Log($"Modrinth「{hit.Title}」：{versions.Count} 个可选版本");
        }
        catch (Exception ex)
        {
            PickerStatus = "读取失败：" + ex.Message;
            Log("读取版本列表失败：" + ex.Message);
        }
    }

    private void ApplyMarketVersionFilter()
    {
        MarketVersions.Clear();
        foreach (var v in _allMarketVersions.Where(v => ShowPrerelease || v.IsRelease))
            MarketVersions.Add(v);

        OnPropertyChanged(nameof(HasMarketVersions));
        OnPropertyChanged(nameof(HasNoMarketVersions));
        (InstallVersionCommand as AsyncRelayCommand)?.RaiseCanExecuteChanged();
    }

    private async Task InstallVersionAsync(ModrinthVersion? version)
    {
        if (version?.File == null || _selectedInstalled == null) return;

        // The list belongs to whatever version was selected when the picker opened.
        // If that changed (keyboard focus can move behind the overlay), installing
        // would drop a jar for the wrong loader into the new version's mods folder.
        if (_pickerVersionId != null && _pickerVersionId != _selectedInstalled.Id)
        {
            ErrorText = "版本已经切换，请重新打开版本列表";
            CloseVersionPicker();
            return;
        }

        var id = _selectedInstalled.Id;
        string title = PickedProject?.Title ?? version.Name;

        ErrorText = "";
        IsMarketBusy = true;
        StatusText = $"正在下载 {title} {version.VersionNumber}…";
        StageText = $"{title} {version.VersionNumber}";
        ProgressValue = 0;
        SpeedText = "";

        using var cts = new CancellationTokenSource();
        try
        {
            var entry = await Market.DownloadIntoModsAsync(version.File, id,
                new Progress<(long Done, long Total)>(p =>
                {
                    ProgressValue = p.Total > 0 ? (double)p.Done / p.Total : 0;
                    StatusText = p.Total > 0
                        ? $"{p.Done / 1024.0 / 1024.0:F1} / {p.Total / 1024.0 / 1024.0:F1} MB"
                        : $"{p.Done / 1024.0 / 1024.0:F1} MB";
                }), cts.Token).ConfigureAwait(true);

            RefreshMods();
            StageText = "";
            SpeedText = "";
            ProgressValue = 0;

            StatusText = $"已安装 {entry.Title} {version.VersionNumber}";
            MarketStatus = $"已安装 {entry.Title}（{version.VersionNumber}）";
            Log($"Modrinth 安装完成：{entry.FileName}（{version.VersionNumber}）");

            // The freshly installed jar may pull in prerequisites; ask before the
            // user finds out at launch time. Never let this break the install result.
            try
            {
                await ShowDependencyPromptAsync().ConfigureAwait(true);
            }
            catch (Exception depEx)
            {
                Log("依赖检查失败：" + depEx.Message);
            }
        }
        catch (Exception ex)
        {
            ErrorText = "安装失败：" + ex.Message;
            MarketStatus = "安装失败";
            Log("Modrinth 安装失败：" + ex.Message);
        }
        finally
        {
            IsMarketBusy = false;
            StageText = "";
            cts.Dispose();
        }
    }
}
