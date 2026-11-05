// SPDX-License-Identifier: MPL-2.0
// Copyright (c) 2026 ChenchenMC <https://github.com/ChenchenMC-cn/EternityCraftLauncher>
using System.Collections.ObjectModel;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Windows;
using System.Windows.Threading;
using ECL.Models;
using ECL.Services;

namespace ECL.ViewModels;

/// <summary>
/// Shell view model: navigation, accounts, installed versions and launching.
/// Install-page state lives in the partial class <c>MainViewModel.Install.cs</c>.
/// </summary>
public sealed partial class MainViewModel : ObservableObject
{
    private readonly StringBuilder _logBuffer = new();
    private readonly DispatcherTimer _logTimer;

    private LauncherSettings _settings;
    private GamePaths _paths;
    private AccountStore _accounts = null!;
    private ApiClient? _api;
    private MicrosoftAuthService? _msAuth;
    private CancellationTokenSource? _cts;

    // Skin support. The service caches PNGs; the server makes the chosen one visible
    // to the game through a local Yggdrasil endpoint.
    private Models.SkinService? _skinService;
    private Services.LocalSkinServer? _skinServer;
    private Models.SkinInfo? _currentSkin;

    public MainViewModel(string? dataRootOverride = null)
    {
        _settings = LauncherSettings.Load(LauncherSettings.ResolveSettingsPath());
        if (!string.IsNullOrWhiteSpace(dataRootOverride)) _settings.DataRoot = dataRootOverride;
        _paths = new GamePaths(_settings.DataRoot, _settings.IsolateByVersion);

        NavigateCommand = new RelayCommand(p => ActivePage = int.TryParse(p?.ToString(), out int i) ? i : 0);
        RefreshCommand = new AsyncRelayCommand(() => RefreshVersionsAsync(), () => !IsBusy);
        InstallCommand = new AsyncRelayCommand(InstallSelectedAsync,
            () => _selectedVersion != null && !IsBusy);
        LaunchCommand = new AsyncRelayCommand(LaunchSelectedAsync, () => !IsBusy && !IsGameRunning && CurrentAccount != null);
        AddOfflineAccountCommand = new RelayCommand(AddOfflineAccount, () => !string.IsNullOrWhiteSpace(OfflineAccountName));
        AddMicrosoftAccountCommand = new AsyncRelayCommand(SignInMicrosoftAsync, () => !IsBusy);
        CompletePasteLoginCommand = new AsyncRelayCommand(CompletePasteLoginAsync,
            () => !IsBusy && !string.IsNullOrWhiteSpace(PasteCallbackUrl));
        RemoveAccountCommand = new RelayCommand(p => RemoveAccount(p as Account), p => p is Account);
        SelectAccountCommand = new RelayCommand(p => SelectAccount(p as Account), p => p is Account);
        UseAccountCommand = new RelayCommand(p => SelectAccount(p as Account), p => p is Account);
        OpenFolderCommand = new RelayCommand(OpenDataFolder);
        BrowseDataRootCommand = new RelayCommand(BrowseDataRoot);
        AdoptExistingInstallCommand = new RelayCommand(AdoptExistingInstall, () => _existingInstall != null);
        BrowseForInstallCommand = new RelayCommand(BrowseForInstall);
        SaveSettingsCommand = new RelayCommand(SaveSettings);
        ShowCrashReportCommand = new RelayCommand(ErrorReporter.ShowCrashReport);
        ShowNonFatalReportCommand = new RelayCommand(ErrorReporter.ShowNonFatalReport);
        RedetectJavaCommand = new AsyncRelayCommand(DetectJavaAsync, () => !IsBusy);
        BrowseJavaCommand = new RelayCommand(BrowseJava);
        ExportDiagnosticsCommand = new RelayCommand(ExportDiagnostics);
        CancelCommand = new RelayCommand(() => _cts?.Cancel(), () => IsBusy);
        RefreshInstalledCommand = new RelayCommand(RefreshInstalled);
        RefreshModsCommand = new RelayCommand(RefreshMods);
        ToggleModCommand = new RelayCommand(p => ToggleMod(p as ModEntry), p => p is ModEntry);
        DeleteModCommand = new RelayCommand(p => DeleteMod(p as ModEntry), p => p is ModEntry);
        ImportModCommand = new RelayCommand(ImportMod, () => SelectedInstalled != null);
        OpenModsFolderCommand = new RelayCommand(OpenModsFolder, () => SelectedInstalled != null);
        InstallOptiFineModCommand = new AsyncRelayCommand(InstallOptiFineModAsync,
            () => CanInstallOptiFineMod && !IsBusy);
        SearchMarketCommand = new AsyncRelayCommand(SearchMarketAsync,
            () => CanSearchMods && !IsSearching);
        InstallMarketModCommand = new AsyncRelayCommand(InstallMarketModAsync,
            _ => CanSearchMods && !IsMarketBusy);
        ToggleMarketCommand = new RelayCommand(() => IsMarketOpen = !IsMarketOpen);
        OpenMsPortalCommand = new RelayCommand(() => OpenUrl("https://portal.azure.com/#blade/Microsoft_AAD_RegisteredApps/ApplicationsListBlade"));
        OpenMcReviewCommand = new RelayCommand(() => OpenUrl("https://aka.ms/mce-reviewappid"));
        OpenXboxCommand = new RelayCommand(() => OpenUrl("https://www.xbox.com/play"));

        // App.LauncherFamily already honours the --theme test hook, so the settings page
        // previews the same family the palette was built from.
        _themeFamilyIndex = (int)App.LauncherFamily;
        _uiLanguageIndex = _settings.UiLanguage switch { "zh" => 1, "en" => 2, _ => 0 };
        _themeAccentIndex = (int)Themes.ThemeManager.ParseAccent(_settings.ThemeAccent);

        _logTimer = new DispatcherTimer(DispatcherPriority.Background)
        {
            Interval = TimeSpan.FromMilliseconds(180)
        };
        _logTimer.Tick += (_, _) => FlushLog();
        _logTimer.Start();

        // Command failures land here instead of escaping as async-void crashes.
        AsyncRelayCommand.Failed += OnCommandFailed;

        ClearFinishedTasksCommand = new RelayCommand(ClearFinishedTasks,
            () => Tasks.Any(t => t.IsFinished));

        InitVersionPicker();
        InitDependencyPrompt();
        InitVersionMaintenance();
        InitDeviceCodeCommands();
        InitSkinCommands();

        BuildLanguageOptions();
    }

    private void OnCommandFailed(Exception ex)
    {
        ErrorText = ex.Message;
        StatusText = "操作失败";
        Log("操作失败：" + ex);
    }

    // ================================================================ pages ===

    private int _activePage;
    public int ActivePage
    {
        get => _activePage;
        set
        {
            if (!SetProperty(ref _activePage, value)) return;
            OnPropertyChanged(nameof(IsHomePage));
            OnPropertyChanged(nameof(IsInstallPage));
            OnPropertyChanged(nameof(IsModsPage));
            OnPropertyChanged(nameof(IsAccountsPage));
            OnPropertyChanged(nameof(IsSettingsPage));
            OnPropertyChanged(nameof(IsLogPage));
            if (value == 2) RefreshMods();
        }
    }

    public bool IsHomePage => ActivePage == 0;
    public bool IsInstallPage => ActivePage == 1;
    public bool IsModsPage => ActivePage == 2;
    public bool IsAccountsPage => ActivePage == 3;
    public bool IsSettingsPage => ActivePage == 4;
    public bool IsLogPage => ActivePage == 5;

    // ============================================================= accounts ===

    public ObservableCollection<Account> Accounts { get; } = new();

    private Account? _currentAccount;
    public Account? CurrentAccount
    {
        get => _currentAccount;
        private set
        {
            if (!SetProperty(ref _currentAccount, value)) return;
            OnPropertyChanged(nameof(AccountName));
            OnPropertyChanged(nameof(AccountTypeLabel));
            OnPropertyChanged(nameof(AccountInitial));
            OnPropertyChanged(nameof(AccountBadgeColor));
            OnPropertyChanged(nameof(HasAccount));
            (LaunchCommand as AsyncRelayCommand)?.RaiseCanExecuteChanged();

            // Cosmetic and network-bound: never let it delay account switching.
            _ = RefreshAccountAvatarAsync();
        }
    }

    private System.Windows.Media.ImageSource? _accountAvatar;

    /// <summary>The 8x8 face of the signed-in account, or null (the circle falls back
    /// to the initial letter).</summary>
    public System.Windows.Media.ImageSource? AccountAvatar
    {
        get => _accountAvatar;
        private set
        {
            if (!SetProperty(ref _accountAvatar, value)) return;
            OnPropertyChanged(nameof(HasAccountAvatar));
        }
    }

    public bool HasAccountAvatar => _accountAvatar != null;

    /// <summary>
    /// Loads the account's face: cached PNG first, then the Mojang profile. A failure
    /// here costs nothing — the circle keeps showing the initial.
    /// </summary>
    private async Task RefreshAccountAvatarAsync()
    {
        var account = _currentAccount;
        AccountAvatar = null;

        if (account == null) return;

        try
        {
            string cache = Path.Combine(_paths.Root, "skins", "heads", account.Uuid + ".png");
            if (File.Exists(cache))
            {
                if (LoadImage(cache) is { } cached)
                {
                    AccountAvatar = cached;
                    return;
                }

                Log("头像缓存读不出来（会重新拉取）：" + cache);
            }

            if (!account.IsMicrosoft || _skinService == null) return;
            if (account.AccessToken is not { Length: > 0 } token) return;

            var skin = await _skinService.FromMojangAsync(token, account.Name, CancellationToken.None)
                                        .ConfigureAwait(true);
            if (skin?.SkinPath == null) return;

            if (Models.SkinHead.SaveFace(skin.SkinPath, cache) is { } saved &&
                LoadImage(saved) is { } face)
            {
                AccountAvatar = face;
                Log($"已载入正版皮肤头像：{account.Name}");
            }
        }
        catch (Exception ex)
        {
            // Cosmetic only: a 401 here just means the token went stale.
            Log("读取头像失败（不影响使用）：" + ex.Message);
        }
    }

    private static System.Windows.Media.ImageSource? LoadImage(string path)
    {
        try
        {
            // BitmapImage + StreamSource returned null here for a PNG written by our
            // own encoder; the decoder route is the one the self-test exercises.
            using var stream = File.OpenRead(path);
            var decoder = System.Windows.Media.Imaging.BitmapDecoder.Create(stream,
                System.Windows.Media.Imaging.BitmapCreateOptions.PreservePixelFormat,
                System.Windows.Media.Imaging.BitmapCacheOption.OnLoad);

            System.Windows.Media.Imaging.BitmapSource frame = decoder.Frames[0];
            frame.Freeze();
            return frame;
        }
        catch
        {
            return null;
        }
    }

    public bool HasAccount => _currentAccount != null;
    public string AccountName => _currentAccount?.Name ?? "无账户";
    public string AccountTypeLabel => _currentAccount?.TypeLabel ?? "请先添加账户";
    public string AccountInitial => _currentAccount?.Initial ?? "?";
    public string AccountBadgeColor => _currentAccount?.IsMicrosoft == true ? "#2BB8FF" : "#3DDC97";

    private string _offlineAccountName = "";
    public string OfflineAccountName
    {
        get => _offlineAccountName;
        set
        {
            if (!SetProperty(ref _offlineAccountName, value)) return;
            (AddOfflineAccountCommand as RelayCommand)?.RaiseCanExecuteChanged();
        }
    }

    private bool _isSigningIn;
    public bool IsSigningIn
    {
        get => _isSigningIn;
        private set => SetProperty(ref _isSigningIn, value);
    }

    // ============================================================ installed ===

    public ObservableCollection<InstalledVersion> InstalledVersions { get; } = new();

    private InstalledVersion? _selectedInstalled;
    public InstalledVersion? SelectedInstalled
    {
        get => _selectedInstalled;
        set
        {
            if (!SetProperty(ref _selectedInstalled, value)) return;
            foreach (var v in InstalledVersions) v.IsSelected = ReferenceEquals(v, value);
            OnPropertyChanged(nameof(HasSelection));
            OnPropertyChanged(nameof(LaunchButtonText));
            OnPropertyChanged(nameof(SelectedVersionSummary));
            OnPropertyChanged(nameof(SelectedVersionJvmArgs));
            OnPropertyChanged(nameof(SelectedVersionJvmHint));
            VerifySummary = "";
            VerifyIssues.Clear();
            OnPropertyChanged(nameof(HasVerifyResult));
            OnPropertyChanged(nameof(HasVerifyIssues));
            RaiseMaintenanceState();

            // A picker opened for the previous version lists the wrong loader's builds.
            if (IsVersionPickerOpen) CloseVersionPicker();
            OnPropertyChanged(nameof(ModsVersionTitle));
            OnPropertyChanged(nameof(ModsVersionSub));
            (LaunchCommand as AsyncRelayCommand)?.RaiseCanExecuteChanged();
            (ImportModCommand as RelayCommand)?.RaiseCanExecuteChanged();
            (OpenModsFolderCommand as RelayCommand)?.RaiseCanExecuteChanged();
            RefreshMods();
        }
    }

    public bool HasSelection => _selectedInstalled != null;
    public bool HasInstalled => InstalledVersions.Count > 0;
    public bool HasNoInstalled => InstalledVersions.Count == 0;

    // ------------------------------------------- existing install discovery ---

    private Services.ExistingInstall? _existingInstall;

    /// <summary>
    /// True when a Minecraft layout elsewhere on the machine is worth offering.
    /// Only computed while the list is empty: somebody with versions installed is
    /// not looking for another root, and the scan parses every profile json.
    /// </summary>
    public bool HasExistingInstall => _existingInstall != null;

    /// <summary>The directory the launcher is actually reading. Shown when empty.</summary>
    public string DataRootDisplay => _paths.Root;

    /// <summary>Path plus counts, e.g. <c>C:\...\.minecraft（12 个版本，12 个可启动）</c>.</summary>
    public string ExistingInstallText => _existingInstall is { } e
        ? string.Format(Services.Localization.T("发现已有 Minecraft 目录：{0}"), e.Root)
        : "";

    /// <summary>
    /// Second line: how many profiles are usable.
    /// </summary>
    public string ExistingInstallDetail => _existingInstall is { } e
        ? string.Format(Services.Localization.T("{0} 个版本，其中 {1} 个可启动"), e.VersionCount, e.ReadyCount)
        : "";

    /// <summary>The directory the launcher is reading, spelled out next to the empty state.</summary>
    public string DataRootHint =>
        string.Format(Services.Localization.T("当前查找目录：{0}"), _paths.Root);

    public string LaunchButtonText => IsGameRunning ? "游戏运行中"
        : _selectedInstalled == null ? Services.Localization.T("选择一个版本") : Services.Localization.T("开始游戏");

    /// <summary>
    /// JVM arguments for the version currently selected on the home page. Empty
    /// means "inherit the global setting"; the value is stored per version id so
    /// switching versions never carries another build's flags along.
    /// </summary>
    public string SelectedVersionJvmArgs
    {
        get
        {
            if (_selectedInstalled is not { } v) return "";
            return _settings.VersionJvmArgs.TryGetValue(v.Id, out var args) ? args : "";
        }
        set
        {
            if (_selectedInstalled is not { } v) return;
            string trimmed = (value ?? "").Trim();

            string current = _settings.VersionJvmArgs.TryGetValue(v.Id, out var existing) ? existing : "";
            if (string.Equals(current, trimmed, StringComparison.Ordinal)) return;

            if (trimmed.Length == 0) _settings.VersionJvmArgs.Remove(v.Id);
            else _settings.VersionJvmArgs[v.Id] = trimmed;

            OnPropertyChanged();
            OnPropertyChanged(nameof(SelectedVersionJvmHint));

            // Persist immediately: this box has no separate save button, so losing
            // it on a crash would be silently confusing. Report a failed write —
            // "设置看起来保存了但其实没有" is the kind of bug nobody can diagnose.
            try
            {
                _settings.Save(LauncherSettings.ResolveSettingsPath());
            }
            catch (Exception ex)
            {
                ErrorText = "JVM 参数已生效，但写入 settings.json 失败：" + ex.Message;
                Log("保存设置失败：" + ex);
            }

            // No log line per keystroke: the box writes on every change now, so logging
            // here would bury everything else. Clearing it is worth one line.
            if (trimmed.Length == 0) Log($"已清除 {v.Id} 的专属 JVM 参数");
        }
    }

    public string SelectedVersionJvmHint
    {
        get
        {
            if (_selectedInstalled is not { } v) return Services.Localization.T("先选择一个版本");
            bool hasOwn = _settings.VersionJvmArgs.TryGetValue(v.Id, out var own) &&
                          !string.IsNullOrWhiteSpace(own);
            if (hasOwn) return Services.Localization.T("只对这个版本生效，追加在全局参数之后");
            return string.IsNullOrWhiteSpace(_settings.ExtraJvmArgs)
                ? Services.Localization.T("留空 = 不使用额外 JVM 参数")
                : string.Format(Services.Localization.T("留空 = 继承全局：{0}"), _settings.ExtraJvmArgs);
        }
    }

    public string SelectedVersionSummary
    {
        get
        {
            if (_selectedInstalled == null) return Services.Localization.T("还没有可启动的版本");
            return $"{_selectedInstalled.DisplayName}  ·  {_selectedInstalled.SubText}";
        }
    }

    // =============================================================== status ===

    private string _statusText = "准备就绪";
    public string StatusText
    {
        get => _statusText;
        set => SetProperty(ref _statusText, value);
    }

    private string _stageText = "";
    public string StageText
    {
        get => _stageText;
        set => SetProperty(ref _stageText, value);
    }

    private string _speedText = "";
    public string SpeedText
    {
        get => _speedText;
        set => SetProperty(ref _speedText, value);
    }

    private double _progressValue;
    public double ProgressValue
    {
        get => _progressValue;
        set => SetProperty(ref _progressValue, value);
    }

    private bool _isBusy;
    public bool IsBusy
    {
        get => _isBusy;
        set
        {
            if (!SetProperty(ref _isBusy, value)) return;
            OnPropertyChanged(nameof(IsIdle));
            (LaunchCommand as AsyncRelayCommand)?.RaiseCanExecuteChanged();
            (AddMicrosoftAccountCommand as AsyncRelayCommand)?.RaiseCanExecuteChanged();
            (RedetectJavaCommand as AsyncRelayCommand)?.RaiseCanExecuteChanged();
            (CancelCommand as RelayCommand)?.RaiseCanExecuteChanged();
            (InstallCommand as AsyncRelayCommand)?.RaiseCanExecuteChanged();
            (RefreshCommand as AsyncRelayCommand)?.RaiseCanExecuteChanged();

            // Verify/repair depend on !IsBusy; without this the "补全文件" button stays
            // clickable while a verification is running.
            RaiseMaintenanceState();
        }
    }

    public bool IsIdle => !IsBusy;

    private bool _isGameRunning;
    public bool IsGameRunning
    {
        get => _isGameRunning;
        set
        {
            if (!SetProperty(ref _isGameRunning, value)) return;
            OnPropertyChanged(nameof(LaunchButtonText));
            (LaunchCommand as AsyncRelayCommand)?.RaiseCanExecuteChanged();
        }
    }

    private string _errorText = "";
    public string ErrorText
    {
        get => _errorText;
        set { if (SetProperty(ref _errorText, value)) OnPropertyChanged(nameof(HasError)); }
    }

    public bool HasError => !string.IsNullOrEmpty(_errorText);

    private string _logText = "";
    private long _shownLength;

    public string LogText
    {
        get => _logText;
        private set => SetProperty(ref _logText, value);
    }

    // ============================================================== settings ===

    public int MemoryMb
    {
        get => _settings.MemoryMb;
        set
        {
            if (_settings.MemoryMb == value) return;
            _settings.MemoryMb = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(MemoryText));
        }
    }

    public string MemoryText => $"{_settings.MemoryMb} MB";

    public int DownloadThreads
    {
        get => _settings.DownloadThreads;
        set
        {
            if (_settings.DownloadThreads == value) return;
            _settings.DownloadThreads = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(ThreadsText));
        }
    }

    public string ThreadsText => $"{_settings.DownloadThreads} 个文件同时下";

    public bool UseMirror
    {
        get => _settings.UseMirror;
        set { if (_settings.UseMirror == value) return; _settings.UseMirror = value; OnPropertyChanged(); }
    }

    /// <summary>Managed download path instead of the native batch (see ManagedDownloader).</summary>
    public bool SafeDownload
    {
        get => _settings.SafeDownload;
        set { if (_settings.SafeDownload == value) return; _settings.SafeDownload = value; OnPropertyChanged(); }
    }

    public bool UseBootstrap
    {
        get => _settings.UseBootstrap;
        set { if (_settings.UseBootstrap == value) return; _settings.UseBootstrap = value; OnPropertyChanged(); }
    }

    public bool AutoDownloadJava
    {
        get => _settings.AutoDownloadJava;
        set { if (_settings.AutoDownloadJava == value) return; _settings.AutoDownloadJava = value; OnPropertyChanged(); }
    }

    /// <summary>Acrylic master opacity, as a percentage for the slider (70-100).</summary>
    public double AcrylicOpacityPercent
    {
        get => Math.Round(_settings.AcrylicOpacity * 100);
        set
        {
            double clamped = Math.Clamp(Math.Round(value), 70, 100);
            if (Math.Abs(clamped - Math.Round(_settings.AcrylicOpacity * 100)) < 0.01) return;
            _settings.AcrylicOpacity = clamped / 100.0;
            OnPropertyChanged();
            OnPropertyChanged(nameof(AcrylicOpacityHint));
        }
    }

    public string AcrylicOpacityHint => $"{Math.Round(_settings.AcrylicOpacity * 100):0}%";

    /// <summary>
    /// Drives the opacity slider's visibility. Read from the radio index, not from
    /// settings: clicking the acrylic card must reveal the slider immediately, and
    /// settings only learn about the choice on save.
    /// </summary>
    public bool IsAcrylicFamily => _themeFamilyIndex == (int)Themes.ThemeFamily.Acrylic;

    public bool IsolateByVersion
    {
        get => _settings.IsolateByVersion;
        set { if (_settings.IsolateByVersion == value) return; _settings.IsolateByVersion = value; OnPropertyChanged(); }
    }

    public string DataRoot
    {
        get => _settings.DataRoot;
        set { if (_settings.DataRoot == value) return; _settings.DataRoot = value; OnPropertyChanged(); }
    }

    public string MsClientId
    {
        get => _settings.MsClientId;
        set
        {
            // Clearing the field now really clears it: there is no built-in id to fall
            // back to (see MicrosoftAuthService.DefaultClientId), and pretending
            // otherwise only hid the "you must register an app" requirement.
            string v = (value ?? "").Trim();
            if (_settings.MsClientId == v) return;
            _settings.MsClientId = v;
            OnPropertyChanged();
            OnPropertyChanged(nameof(UsingLegacyClientId));
            OnPropertyChanged(nameof(MsClientIdHint));
            OnPropertyChanged(nameof(NeedsClientId));
            RebuildAuthService();
        }
    }

    /// <summary>
    /// True only for the 2013 Live id, which cannot use loopback. Any registered
    /// app (including the project's own default) takes the PKCE path.
    /// </summary>
    /// <summary>Drives the "go fill it in" button: true only when sign-in cannot run.</summary>
    public bool NeedsClientId => string.IsNullOrWhiteSpace(_settings.MsClientId);

    public bool UsingLegacyClientId => string.Equals(
        _settings.MsClientId, MicrosoftAuthService.LegacyLiveClientId, StringComparison.OrdinalIgnoreCase);

    private string _pasteCallbackUrl = "";
    /// <summary>
    /// The oauth20_desktop.srf URL the user copies back. Only the legacy id needs
    /// this; a registered app comes back on loopback automatically.
    /// </summary>
    public string PasteCallbackUrl
    {
        get => _pasteCallbackUrl;
        set
        {
            if (!SetProperty(ref _pasteCallbackUrl, value)) return;
            (CompletePasteLoginCommand as AsyncRelayCommand)?.RaiseCanExecuteChanged();
        }
    }

    private string _liveAuthorizeState = "";
    private string _deviceCode = "";
    /// <summary>The 8-character code the user types at microsoft.com/link.</summary>
    public string DeviceCode
    {
        get => _deviceCode;
        private set
        {
            if (!SetProperty(ref _deviceCode, value)) return;
            OnPropertyChanged(nameof(ShowDeviceCode));
        }
    }

    private string _deviceCodeUri = "https://www.microsoft.com/link";
    public string DeviceCodeUri
    {
        get => _deviceCodeUri;
        private set => SetProperty(ref _deviceCodeUri, value);
    }

    private string _deviceCodeStatus = "";
    public string DeviceCodeStatus
    {
        get => _deviceCodeStatus;
        private set => SetProperty(ref _deviceCodeStatus, value);
    }

    public bool ShowDeviceCode => _deviceCode.Length > 0;

    public System.Windows.Input.ICommand CopyDeviceCodeCommand { get; private set; } = null!;
    public System.Windows.Input.ICommand OpenDeviceCodePageCommand { get; private set; } = null!;

    private void InitDeviceCodeCommands()
    {
        CopyDeviceCodeCommand = new RelayCommand(() =>
        {
            if (_deviceCode.Length == 0) return;
            try
            {
                System.Windows.Clipboard.SetText(_deviceCode);
                StatusText = $"已复制代码 {_deviceCode}";
            }
            catch (Exception ex)
            {
                // Another process can hold the clipboard open; the code is on screen
                // and selectable, so this is not fatal.
                ErrorText = "复制失败，请手动选中代码：" + ex.Message;
            }
        });

        OpenDeviceCodePageCommand = new RelayCommand(() =>
        {
            try
            {
                System.Diagnostics.Process.Start(
                    new System.Diagnostics.ProcessStartInfo(_deviceCodeUri) { UseShellExecute = true });
            }
            catch (Exception ex)
            {
                ErrorText = "无法打开浏览器，请手动访问 " + _deviceCodeUri + "：" + ex.Message;
            }
        });
    }

    public bool ShowPasteBox => _liveAuthorizeState.Length > 0 && UsingLegacyClientId;

    // --- clipboard auto-fill -------------------------------------------------
    // The built-in client id cannot use a loopback redirect, so the result of the
    // sign-in is the URL of a blank page. Users either pasted it into the wrong
    // place or never found it at all ("登录过程还是太繁琐了/有些傻子不会"), so the
    // launcher now reads the clipboard itself.

    private DispatcherTimer? _callbackWatch;
    private DateTime _callbackWatchDeadline;

    private void StartCallbackWatch()
    {
        StopCallbackWatch();

        // Bounded: a watcher that lives forever would keep reading the clipboard and
        // could hijack the next URL the user copies for something else entirely.
        _callbackWatchDeadline = DateTime.UtcNow.AddMinutes(10);

        _callbackWatch = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1.5) };
        _callbackWatch.Tick += (_, _) => TryAutoFillCallback();
        _callbackWatch.Start();
    }

    private void StopCallbackWatch()
    {
        _callbackWatch?.Stop();
        _callbackWatch = null;
    }

    private void TryAutoFillCallback()
    {
        if (!ShowPasteBox)
        {
            StopCallbackWatch();
            return;
        }

        if (DateTime.UtcNow > _callbackWatchDeadline)
        {
            StopCallbackWatch();
            Log("登录窗口已超时，剪贴板自动识别已停止（可手动粘贴地址后点完成登录）。");
            return;
        }

        try
        {
            if (!Clipboard.ContainsText()) return;

            string text = Clipboard.GetText().Trim();
            if (text.Length < 24 || text.Length > 4000) return;

            // Must look like the OAuth redirect, not some other URL the user copied.
            bool looksRight =
                text.Contains("code=", StringComparison.OrdinalIgnoreCase) &&
                (text.Contains("oauth20_desktop.srf", StringComparison.OrdinalIgnoreCase) ||
                 text.Contains("login.live.com", StringComparison.OrdinalIgnoreCase) ||
                 text.Contains("login.microsoftonline.com", StringComparison.OrdinalIgnoreCase) ||
                 text.StartsWith("http", StringComparison.OrdinalIgnoreCase));

            if (!looksRight) return;
            if (string.Equals(text, PasteCallbackUrl, StringComparison.Ordinal)) return;

            PasteCallbackUrl = text;
            Log("已从剪贴板读到微软回调地址，自动完成登录…");

            if (CompletePasteLoginCommand.CanExecute(null))
            {
                StopCallbackWatch();
                CompletePasteLoginCommand.Execute(null);
            }
        }
        catch
        {
            // Another process holding the clipboard open is normal; try again next tick.
        }
    }

    public string MsClientIdHint =>
        string.IsNullOrWhiteSpace(_settings.MsClientId)
            ? "还没填 client_id：正版登录需要你自己的 Azure 应用（设置 → 微软账户，README 有 7 步说明）"
            : UsingLegacyClientId
                ? "点上面的按钮 → 在浏览器登录 → 回到启动器会自动完成（识别不到再手动贴地址）"
                : "已配置 Azure 应用：登录会自动回跳";

    public ObservableCollection<JavaRuntime> JavaRuntimes { get; } = new();

    /// <summary>One row in the in-game language picker ("自动" plus every MC language).</summary>
    public sealed record LanguageOption(string Code, string Display);

    public ObservableCollection<LanguageOption> GameLanguageOptions { get; } = new();

    /// <summary>
    /// Language handed to the game as <c>--lang</c>. "auto" means "follow Windows";
    /// the resolved code is shown next to it so the choice is not a mystery.
    /// </summary>
    public string GameLanguage
    {
        get => _settings.GameLanguage;
        set
        {
            string v = string.IsNullOrWhiteSpace(value) ? Services.McLanguages.AutoCode : value;
            if (_settings.GameLanguage == v) return;
            _settings.GameLanguage = v;
            OnPropertyChanged();
            OnPropertyChanged(nameof(GameLanguageHint));
        }
    }

    public string GameLanguageHint
    {
        get
        {
            if (string.Equals(_settings.GameLanguage, Services.McLanguages.AutoCode,
                    StringComparison.OrdinalIgnoreCase))
            {
                string code = Services.McLanguages.DetectSystem();
                string name = Services.McLanguages.Find(code)?.Name ?? code;
                return $"跟随系统（{name} · {code}）";
            }

            var pick = Services.McLanguages.Find(_settings.GameLanguage);
            return pick == null
                ? $"启动时传 --lang {_settings.GameLanguage}"
                : $"启动时传 --lang {pick.Code}";
        }
    }

    private void BuildLanguageOptions()
    {
        GameLanguageOptions.Clear();

        string systemCode = Services.McLanguages.DetectSystem();
        string systemName = Services.McLanguages.Find(systemCode)?.Name ?? systemCode;
        GameLanguageOptions.Add(new LanguageOption(Services.McLanguages.AutoCode,
            $"自动 · 跟随系统（{systemName}）"));

        // Sorted by native name so 简体中文 sits where a Chinese reader expects it.
        foreach (var lang in Services.McLanguages.All.OrderBy(l => l.Name, StringComparer.CurrentCulture))
            GameLanguageOptions.Add(new LanguageOption(lang.Code, lang.Display));
    }

    public string JavaHint
    {
        get
        {
            var best = JavaRuntimes.FirstOrDefault();
            return best == null ? Services.Localization.T("未检测到 Java 运行时") : best.Display;
        }
    }

    /// <summary>
    /// Shown only when it carries information: no Java found, or an override is active.
    /// Printing "找不到 Java 就点..." directly under a detected "Java 21" read like a bug.
    /// </summary>
    public string JavaOverrideHint
    {
        get
        {
            if (!string.IsNullOrWhiteSpace(_settings.JavaPath))
                return "已手动指定：" + _settings.JavaPath + "（点「重新检测」可清掉这条覆盖）";

            return JavaRuntimes.Count == 0
                ? @"还没找到 Java：点「指定 java.exe」选一个（通常在 <Java 目录>\bin\java.exe）。"
                : "";
        }
    }

    /// <summary>Collapses the hint row when there is nothing to say.</summary>
    public bool HasJavaOverrideHint => JavaOverrideHint.Length > 0;

    // ============================================================== theme ====

    public string[] ThemeFamilyLabels => Themes.ThemeManager.FamilyLabels;
    public string[] ThemeAccentLabels => Themes.ThemeManager.AccentLabels;

    private int _uiLanguageIndex;   // 0 = auto, 1 = 中文, 2 = English

    /// <summary>Radio-card index for the interface language picker.</summary>
    public int UiLanguageIndex
    {
        get => _uiLanguageIndex;
        set
        {
            if (_uiLanguageIndex == value) return;
            _uiLanguageIndex = value;
            OnPropertyChanged();
        }
    }

    private int _themeFamilyIndex = (int)Themes.ThemeFamily.Original;   // set from settings in the ctor

    public int ThemeFamilyIndex
    {
        get => _themeFamilyIndex;
        set
        {
            if (!SetProperty(ref _themeFamilyIndex, value)) return;
            OnPropertyChanged(nameof(ThemeSwatches));   // swatch colours follow the family
            OnPropertyChanged(nameof(IsAcrylicFamily));
            if (ThemeAccentIndex >= ThemeSwatches.Length) ThemeAccentIndex = 0;
        }
    }

    private int _themeAccentIndex;
    public int ThemeAccentIndex
    {
        get => _themeAccentIndex;
        set => SetProperty(ref _themeAccentIndex, value);
    }

    /// <summary>Hex colours for the five accent swatches (depend on the family).</summary>
    public string[] ThemeSwatches => Themes.ThemeManager.AccentSwatches(
        Themes.ThemeManager.FamilyIds[Math.Clamp(_themeFamilyIndex, 0, 2)]);

    public string AppVersion => AppInfo.Version;

    /// <summary>Title-bar badge, which is set in caps: BETA 1.0 FIX 2.</summary>
    public string AppVersionBadge => AppInfo.Version.ToUpperInvariant();
    public string GameDirectory => _paths.Root;

    // ============================================================== commands ===

    public System.Windows.Input.ICommand NavigateCommand { get; }
    public System.Windows.Input.ICommand LaunchCommand { get; }
    public System.Windows.Input.ICommand AddOfflineAccountCommand { get; }
    public System.Windows.Input.ICommand AddMicrosoftAccountCommand { get; }
    public System.Windows.Input.ICommand CompletePasteLoginCommand { get; }
    public System.Windows.Input.ICommand RemoveAccountCommand { get; }
    public System.Windows.Input.ICommand SelectAccountCommand { get; }
    public System.Windows.Input.ICommand UseAccountCommand { get; }
    public System.Windows.Input.ICommand OpenFolderCommand { get; }
    public System.Windows.Input.ICommand BrowseDataRootCommand { get; }
    public System.Windows.Input.ICommand AdoptExistingInstallCommand { get; }
    public System.Windows.Input.ICommand BrowseForInstallCommand { get; }
    public System.Windows.Input.ICommand SaveSettingsCommand { get; }

    public System.Windows.Input.ICommand ShowCrashReportCommand { get; }
    public System.Windows.Input.ICommand ShowNonFatalReportCommand { get; }

    public bool CanShowCrashReport => ErrorReporter.CrashViewerAvailable;
    public bool CanShowNonFatalReport => ErrorReporter.NonFatalViewerAvailable;
    public System.Windows.Input.ICommand RedetectJavaCommand { get; }
    public System.Windows.Input.ICommand BrowseJavaCommand { get; }
    public System.Windows.Input.ICommand ExportDiagnosticsCommand { get; }
    public System.Windows.Input.ICommand CancelCommand { get; }
    public System.Windows.Input.ICommand RefreshInstalledCommand { get; }
    public System.Windows.Input.ICommand RefreshModsCommand { get; }
    public System.Windows.Input.ICommand ToggleModCommand { get; }
    public System.Windows.Input.ICommand DeleteModCommand { get; }
    public System.Windows.Input.ICommand ImportModCommand { get; }
    public System.Windows.Input.ICommand OpenModsFolderCommand { get; }
    public System.Windows.Input.ICommand InstallOptiFineModCommand { get; }
    public System.Windows.Input.ICommand SearchMarketCommand { get; }
    public System.Windows.Input.ICommand InstallMarketModCommand { get; }
    public System.Windows.Input.ICommand ToggleMarketCommand { get; }
    public System.Windows.Input.ICommand OpenMsPortalCommand { get; }
    public System.Windows.Input.ICommand OpenMcReviewCommand { get; }
    public System.Windows.Input.ICommand OpenXboxCommand { get; }

    // ================================================================= init ===

    public async Task InitializeAsync()
    {
        _paths.EnsureLayout();
        _settings.Normalize();

        // Materialise the file on first run only. Saving unconditionally would persist
        // a --data override into the real config, so the next ordinary launch would
        // silently use the test directory instead of the user's install.
        if (!File.Exists(LauncherSettings.ResolveSettingsPath()))
            _settings.Save(LauncherSettings.ResolveSettingsPath());

        _accounts = new AccountStore(_paths.AccountsFile);
        var current = _accounts.EnsureDefault();

        // Restore a previously chosen skin and bring the local server up early: the
        // game may be launched at any moment afterwards.
        try
        {
            _skinService = new Models.SkinService(_paths.Root);
            _currentSkin = _skinService.LoadSaved();
            RefreshSkinState();
            if (_currentSkin?.SkinPath != null && File.Exists(_currentSkin.SkinPath))
            {
                byte[] png = await File.ReadAllBytesAsync(_currentSkin.SkinPath).ConfigureAwait(true);
                byte[]? cape = _currentSkin.CapePath != null && File.Exists(_currentSkin.CapePath)
                    ? await File.ReadAllBytesAsync(_currentSkin.CapePath).ConfigureAwait(true)
                    : null;

                _skinServer = new Services.LocalSkinServer(
                    Path.Combine(_paths.Root, "skin-server.pem"));
                _skinServer.SetSkin("", "Player", png, _currentSkin.Slim, cape);
                _skinServer.Start();
                Log($"皮肤服务已启动：{_skinServer.ApiRoot}（{_currentSkin.Label}）");
            }
        }
        catch (Exception ex)
        {
            Log("皮肤功能初始化失败（不影响启动）：" + ex.Message);
        }
        RefreshAccounts(current.Id);

        // The setter kicks this off too, but awaiting here guarantees the face is on
        // screen before the window paints (a --shot run would otherwise catch it empty).
        await RefreshAccountAvatarAsync().ConfigureAwait(true);

        RebuildAuthService();
        _api = new ApiClient(_settings, _paths.Root);

        Log($"ECL {AppVersion} · native: {(NativeCore.IsAvailable(out var nv) ? nv : "不可用")}");
        Log($"数据目录：{_paths.Root}");
        Log($"账户：{_accounts.Accounts.Count} 个，当前 {current.Name}（{current.TypeLabel}）");

        RefreshInstalled();
        await RefreshVersionsAsync().ConfigureAwait(true);
        _ = DetectJavaAsync();
    }

    private void RebuildAuthService()
    {
        _msAuth?.Dispose();
        _msAuth = new MicrosoftAuthService(_settings.MsClientId);
    }

    // ============================================================== accounts ===

    private void RefreshAccounts(string? selectId = null)
    {
        Accounts.Clear();
        foreach (var a in _accounts.Accounts) Accounts.Add(a);

        var target = selectId != null ? _accounts.Find(selectId) : _accounts.Current;
        foreach (var a in Accounts) a.IsCurrent = ReferenceEquals(a, target);
        CurrentAccount = target;
        OnPropertyChanged(nameof(UsingLegacyClientId));
        OnPropertyChanged(nameof(MsClientIdHint));
    }

    private void SelectAccount(Account? account)
    {
        if (account == null) return;
        _accounts.Select(account.Id);
        CurrentAccount = account;

        // Move the "使用中" badge as well. Selecting used to only touch
        // CurrentAccount, so the list still highlighted the old row and the click
        // felt dead even though the launcher had switched.
        foreach (var a in Accounts) a.IsCurrent = ReferenceEquals(a, account);

        StatusText = $"已切换到 {account.Name}";
        Log($"切换账户：{account.Name}（{account.TypeLabel}）");
    }

    private void AddOfflineAccount()
    {
        string name = OfflineAccountName.Trim();
        if (name.Length == 0) return;
        if (name.Length > 16)
        {
            ErrorText = "离线账户名不能超过 16 个字符。";
            return;
        }

        var acc = Account.CreateOffline(name);
        _accounts.Add(acc);
        _accounts.Select(acc.Id);
        RefreshAccounts(acc.Id);
        OfflineAccountName = "";
        StatusText = $"已添加离线账户 {name}";
        Log($"新增离线账户：{name}（{acc.Uuid}）");
    }

    private async Task SignInMicrosoftAsync()
    {
        if (_msAuth == null)
        {
            ErrorText = "认证服务尚未就绪，请稍后重试。";
            return;
        }

        ErrorText = "";
        ActivePage = 3;

        if (!_msAuth.IsConfigured)
        {
            ErrorText = "没有 client_id，正版登录用不了：点旁边的「去填 client_id」，或在「设置 → 微软账户」里填自己的 Azure 应用 ID。";
            StatusText = "需要配置 client_id";
            Log("正版登录被拦下：settings.msClientId 为空。");
            return;
        }

        if (_msAuth.UsesLegacyLiveClientId)
        {
            // Only oauth20_authorize.srf runs the full "Sign in to Minecraft" consent
            // for this client id; microsoft.com/link (the device-code page) never does,
            // so the device flow always ends in invalid_grant. The code has to come
            // back through the clipboard instead.
            var (url, state) = _msAuth.CreateLiveAuthorizeRequest();
            _liveAuthorizeState = state;
            PasteCallbackUrl = "";
            OnPropertyChanged(nameof(ShowPasteBox));
            StatusText = "在浏览器里登录，回到启动器会自动完成";
            Log("已打开微软官方登录页（Sign in to Minecraft）。");
            Log("登录后浏览器会跳到空白页 —— 按 Ctrl+L 再 Ctrl+C 复制地址，启动器会自己识别。");

            try
            {
                System.Diagnostics.Process.Start(
                    new System.Diagnostics.ProcessStartInfo(url) { UseShellExecute = true });
            }
            catch (Exception ex)
            {
                ErrorText = "无法自动打开浏览器：" + ex.Message;
                Log("请手动访问：" + url);
            }

            // Most people never realise the blank page's address bar *is* the result.
            // Watch the clipboard so copying it is enough -- no paste, no button.
            StartCallbackWatch();

            await Task.CompletedTask.ConfigureAwait(true);
            return;
        }

        // Registered app: authorization code + PKCE through a loopback listener,
        // so the browser comes straight back to the launcher.
        IsBusy = true;
        IsSigningIn = true;
        _cts = new CancellationTokenSource();
        try
        {
            StatusText = "等待浏览器完成微软登录…";
            Log("已打开系统浏览器，登录完成后会自动回到启动器。");
            var account = await _msAuth.InteractiveLoginAsync(
                    new Progress<string>(Log), null, _cts.Token)
                .ConfigureAwait(true);

            _accounts.Add(account);
            RefreshAccounts(account.Id);
            StatusText = $"已登录：{account.Name}";
            ActivePage = 0;
        }
        catch (OperationCanceledException)
        {
            StatusText = "登录已取消";
        }
        catch (Exception ex)
        {
            ErrorText = ex.Message;
            StatusText = "微软登录失败";
            Log("登录失败：" + ex.Message);
        }
        finally
        {
            IsSigningIn = false;
            IsBusy = false;
            _cts?.Dispose();
            _cts = null;
        }
    }

    /// <summary>
    /// Screenshot hook: renders the paste-back panel with sample text without
    /// launching a browser (used by the UI regression capture).
    /// </summary>
    /// <summary>Screenshot hook: fills the task menu with one of each state.</summary>
    public void PrepareTaskShot()
    {
        void Add(string title, string detail, TaskState state)
        {
            Tasks.Add(new TaskItem(title, new RelayCommand(() => { }))
            {
                Detail = detail,
                State = state
            });
        }

        Add("安装 1.20.1 + Forge", "下载 Forge 安装器（第 1 次 · 镜像）：1.20.1-47.4.28", TaskState.Running);
        Add("启动 1.20.1-forge-47.4.28", "已启动，游戏运行中", TaskState.Done);
        Add("安装 1.19.4 + Fabric", "Java 包校验和不符，已放弃", TaskState.Failed);
        Add("下载 Java 17", "已取消", TaskState.Cancelled);

        NotifyTaskState();
    }

    public void PrepareLoginShot()
    {
        _liveAuthorizeState = "shot";
        PasteCallbackUrl = "https://login.live.com/oauth20_desktop.srf?code=M.R3_BAY.example&lc=1033";
        OnPropertyChanged(nameof(ShowPasteBox));
        StatusText = "在浏览器里完成登录，然后把地址栏网址粘贴到下方";
    }

    /// <summary>Redeems the callback URL the user pasted from the browser.</summary>
    private async Task CompletePasteLoginAsync()
    {
        if (_msAuth == null || _liveAuthorizeState.Length == 0) return;

        ErrorText = "";
        IsBusy = true;
        IsSigningIn = true;
        try
        {
            StatusText = "正在完成登录…";
            var account = await _msAuth.CompleteLiveAuthorizeAsync(
                    PasteCallbackUrl, _liveAuthorizeState, new Progress<string>(Log), CancellationToken.None)
                .ConfigureAwait(true);

            _accounts.Add(account);
            RefreshAccounts(account.Id);
            StatusText = $"已登录：{account.Name}";
            _liveAuthorizeState = "";
            PasteCallbackUrl = "";
            OnPropertyChanged(nameof(ShowPasteBox));
            ActivePage = 0;
        }
        catch (Exception ex)
        {
            ErrorText = ex.Message;
            StatusText = "登录失败";
            Log("登录失败：" + ex.Message);
            MicrosoftAuthService.TraceLogin("paste-login: " + ex.GetType().Name + ": " + ex.Message);
        }
        finally
        {
            IsSigningIn = false;
            IsBusy = false;
            // Either way the watcher has done its job; a stale one would fire on the
            // next unrelated copy.
            StopCallbackWatch();
        }
    }

    private void RemoveAccount(Account? account)
    {
        if (account == null) return;
        if (IsGameRunning)
        {
            ErrorText = "游戏运行中，无法删除账户。";
            return;
        }

        _accounts.Remove(account.Id);
        RefreshAccounts();
        StatusText = $"已删除账户 {account.Name}";
        Log($"删除账户：{account.Name}");
    }

    // ============================================================ installed ===

    public void RefreshInstalled()
    {
        try
        {
            RefreshInstalledCore();
        }
        catch (Exception ex)
        {
            // A locked or corrupt folder used to abort InitializeAsync, leaving the
            // launcher with an empty list and no explanation.
            Log("读取已安装版本失败：" + ex.Message);
            ErrorText = "读取已安装版本失败：" + ex.Message;
        }
    }

    private void RefreshInstalledCore()
    {
        string? keep = _selectedInstalled?.Id;

        // One sweep feeds both the UI and eclversions.config, so a profile another
        // launcher left behind is recognised and recorded, not silently ignored.
        var probes = InstalledVersionScanner.Probe(_paths);
        InstalledVersionScanner.SaveCatalog(_paths, probes);

        InstalledVersions.Clear();
        foreach (var v in InstalledVersionScanner.Scan(_paths))
            InstalledVersions.Add(v);

        int extra = probes.Count(p => p.Health != "ready");
        if (extra > 0)
            Log($"版本探测：{probes.Count} 个目录，{probes.Count - extra} 个可用，{extra} 个不完整（见 eclversions.config）");

        OnPropertyChanged(nameof(HasInstalled));
        OnPropertyChanged(nameof(HasNoInstalled));
        RefreshExistingInstall();

        var target = InstalledVersions.FirstOrDefault(v => v.Id == keep)
                     ?? InstalledVersions.FirstOrDefault(v => v.Id == _settings.LastVersion)
                     ?? InstalledVersions.FirstOrDefault();
        SelectedInstalled = target;
        if (target != null) _settings.LastVersion = target.Id;
    }

    private DateTime _discoveryAt = DateTime.MinValue;
    private string _discoveryRoot = "";

    private void RefreshExistingInstall()
    {
        // Scanning parses every profile json of every candidate, so it only runs when
        // the list is empty -- i.e. exactly when the user is staring at "no versions".
        // It is also memoised: RefreshInstalled fires on every install/settings/refresh,
        // and re-parsing a 20-profile .minecraft each time both costs time and fills the
        // log with the same "发现已有目录" line.
        bool sameRoot = string.Equals(_discoveryRoot, _paths.Root, StringComparison.OrdinalIgnoreCase);
        bool fresh = (DateTime.UtcNow - _discoveryAt).TotalSeconds < 60;

        if (InstalledVersions.Count == 0 && !(sameRoot && fresh))
        {
            _existingInstall = Services.ExistingInstallFinder.Discover(_paths.Root).FirstOrDefault();
            _discoveryAt = DateTime.UtcNow;
            _discoveryRoot = _paths.Root;

            if (_existingInstall is { } found)
                Log($"发现已有目录：{found.Root}（{found.VersionCount} 个版本，{found.ReadyCount} 个可启动）");
        }
        else if (InstalledVersions.Count > 0)
        {
            _existingInstall = null;
        }

        OnPropertyChanged(nameof(HasExistingInstall));
        OnPropertyChanged(nameof(ExistingInstallText));
        OnPropertyChanged(nameof(ExistingInstallDetail));
        OnPropertyChanged(nameof(DataRootHint));
        OnPropertyChanged(nameof(DataRootDisplay));
        (AdoptExistingInstallCommand as RelayCommand)?.RaiseCanExecuteChanged();
    }

    // ================================================================= play ===

    private async Task LaunchSelectedAsync()
    {
        if (_selectedInstalled == null)
        {
            ActivePage = 1;
            return;
        }

        ErrorText = "";
        _cts = new CancellationTokenSource();
        IsBusy = true;
        var task = BeginTask($"启动 {_selectedInstalled.DisplayName}");

        try
        {
            var account = await EnsureFreshAccountAsync(_cts.Token).ConfigureAwait(true);
            if (account == null)
            {
                EndTask(task, TaskState.Cancelled, "没有可用账户");
                return;
            }

            StatusText = $"正在启动 {_selectedInstalled.DisplayName}…";
            ProgressValue = 0;
            StageText = "";

            // Advisory only: -Xmx is a ceiling, not a reservation, so a big value still
            // starts. Warn and carry on rather than blocking a launch that would work.
            if (LaunchService.MemoryWarning(_settings) is { } memoryWarning)
            {
                Log("警告：" + memoryWarning);
                StatusText = "内存分配偏大，见日志";
            }

            // 1.20.2+ dies at start-up with an offline account (see LaunchService.OfflineBlocked).
            // Say so before the crash instead of letting it look like a launcher bug.
            if (LaunchService.OfflineBlocked(account, _selectedInstalled.BaseVersion))
            {
                ErrorText = "1.20.2 及更新的版本在离线账户下会在启动时崩溃（游戏内部认证失败，与本启动器无关）。" +
                            "请改用微软正版账户登录，或选择 1.20.1 及更早的版本。";
                Log("警告：1.20.2+ 离线账户启动会因游戏内部 401 崩溃");
                EndTask(task, TaskState.Failed, "离线账户不支持 1.20.2+");
                return;
            }

            await LaunchCoreAsync(_selectedInstalled.Id, account, _cts.Token).ConfigureAwait(true);
            EndTask(task, TaskState.Done, "已启动，游戏运行中");
        }
        catch (OperationCanceledException)
        {
            StatusText = "已取消";
            EndTask(task, TaskState.Cancelled, "已取消");
        }
        catch (Exception ex)
        {
            ErrorText = ex.Message;
            StatusText = "启动失败";
            Log("启动失败：" + ex);

            // Launch failures are exactly what the non-fatal viewer is for: the
            // launcher keeps running, but something concrete went wrong.
            ErrorReporter.ReportNonFatal("launch", ex.Message, ex.ToString());
            EndTask(task, TaskState.Failed, ex.Message);
        }
        finally
        {
            IsBusy = false;
            _cts?.Dispose();
            _cts = null;
        }
    }

    /// <summary>
    /// Sweeps the version's file list and downloads whatever is missing before the
    /// JVM is spawned. Silent when everything is in place (one file-info pass).
    /// </summary>
    private async Task EnsureFilesPresentAsync(string versionId, CancellationToken ct)
    {
        if (_api == null) return;

        var verifier = new VersionVerifier(_paths, _settings, _api);

        QuickCheck check;
        try
        {
            check = await verifier.QuickCheckAsync(versionId, ct).ConfigureAwait(true);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // The usual reason a manifest cannot be read is a copied folder that has
            // the modded profile but not the vanilla parent it inherits from.
            throw new InvalidOperationException(
                $"读不出这个版本的文件清单（{ex.Message}）。" +
                "常见原因是父版本（例如原版 1.20.1）的 json 不在 versions 目录里 —— " +
                "先装原版，或在「维护」里重新安装这个版本。", ex);
        }

        if (check.Healthy)
        {
            Log($"启动前检查：{check.Checked} 个文件齐全");
            return;
        }

        Log($"启动前检查：缺 {check.Missing} / {check.Checked} 个文件" +
            (check.MissingBytes > 0 ? $"（约 {check.MissingBytes / 1024.0 / 1024.0:F0} MB）" : "") +
            "，开始补齐");
        StatusText = string.Format(Services.Localization.T("补齐 {0} 个缺失文件…"), check.Missing);
        StageText = Services.Localization.T("补齐游戏文件");

        var progress = SafeProgress.Create<InstallProgress>(p =>
        {
            StageText = p.Stage;
            ProgressValue = p.Download.Fraction;
            SpeedText = FormatSpeed(p.Download.BytesPerSecond);
            StatusText = $"{p.Download.FilesDone} / {p.Download.FilesTotal} 个文件";
        }, ex => Log("进度更新失败（不影响补齐）：" + ex.Message));

        try
        {
            await verifier.RepairAsync(versionId, progress, new Progress<string>(Log), ct)
                          .ConfigureAwait(true);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            throw new InvalidOperationException($"缺失的文件补齐失败：{ex.Message}", ex);
        }

        var again = await verifier.QuickCheckAsync(versionId, ct).ConfigureAwait(true);
        if (!again.Healthy)
        {
            throw new InvalidOperationException(
                $"还缺 {again.Missing} 个文件（例如 {again.FirstSample}）。" +
                "请检查网络后在「维护」里点「补全文件」，或重新安装这个版本。");
        }

        Log($"补齐完成：{again.Checked} 个文件齐全");
    }

    /// <summary>Refreshes the Microsoft session when it is about to expire.</summary>
    private async Task<Account?> EnsureFreshAccountAsync(CancellationToken ct)
    {
        var account = CurrentAccount;
        if (account == null)
        {
            ErrorText = "还没有选择账户。";
            ActivePage = 3;
            return null;
        }

        if (!account.IsMicrosoft || !account.TokenExpired) return account;

        if (_msAuth == null) return account;
        var refreshed = await _msAuth.RefreshAsync(account, new Progress<string>(Log), ct).ConfigureAwait(true);
        if (refreshed == null)
        {
            ErrorText = "微软登录已过期，请在「账户」页重新登录。";
            ActivePage = 3;
            return null;
        }

        _accounts.Update(refreshed);
        RefreshAccounts(refreshed.Id);
        return refreshed;
    }

    private async Task LaunchCoreAsync(string versionId, Account account, CancellationToken ct)
    {
        // Missing libraries/assets must be repaired *before* Java starts: the game
        // dies in the first second otherwise and the user reports a crash instead of
        // an incomplete install.
        await EnsureFilesPresentAsync(versionId, ct).ConfigureAwait(true);

        var resolved = await VersionResolver.ResolveAsync(versionId, _paths, new Progress<string>(Log), ct)
                                            .ConfigureAwait(true);

        var java = await EnsureJavaAsync(resolved.Detail.JavaVersion?.MajorVersion ?? 8, ct).ConfigureAwait(true);
        if (java == null) throw new InvalidOperationException(
            "找不到能用的 Java（这个版本至少要 Java 17）。到「设置 → Java」下载一个，或者点「指定 java.exe」指过去。");

        var launcher = new LaunchService(_settings, _paths);
        var formatter = new MinecraftLogFormatter();
        var formatLock = new object();

        void EmitGame(string line)
        {
            lock (formatLock)
            {
                string? text = formatter.Format(line);
                if (!string.IsNullOrEmpty(text)) AppendLog(text);
            }
        }

        // Hand the skin to the game as a java agent. Failure here must never block
        // launching — worst case the player sees their default skin.
        string? skinAgent = null;
        if (_skinServer != null && _currentSkin?.SkinPath != null)
        {
            try
            {
                string jar = await Services.AuthlibInjector.EnsureAsync(
                    AppContext.BaseDirectory, new Progress<string>(Log),
                    _cts?.Token ?? CancellationToken.None).ConfigureAwait(true);

                skinAgent = Services.AuthlibInjector.BuildAgentArg(jar, _skinServer.ApiRoot);
                Log($"已启用自定义皮肤：{_currentSkin.Label}（{_skinServer.ApiRoot}）");
            }
            catch (Exception ex)
            {
                Log("皮肤注入失败，游戏仍会启动，只是没有自定义皮肤：" + ex.Message);
                Services.ErrorReporter.ReportNonFatal("skin", "authlib-injector 不可用", ex.ToString());
            }
        }

        var process = launcher.Launch(
            new LaunchService.LaunchRequest(versionId, resolved, account, java, skinAgent),
            EmitGame, EmitGame, out string cmd);

        Log("启动命令：" + Redact(cmd));
        IsGameRunning = true;
        StatusText = $"{_selectedInstalled?.DisplayName ?? versionId} 运行中（Java {java.Info.Major}，{account.Name}）";

        process.Exited += (_, _) =>
        {
            int code = -1;
            try { code = process.ExitCode; } catch { }
            Application.Current?.Dispatcher.BeginInvoke(() =>
            {
                IsGameRunning = false;
                StatusText = code == 0 ? "游戏已退出" : $"游戏退出，代码 {code}";
                if (code != 0) ErrorText = $"游戏异常退出（代码 {code}），详情见日志页。";
                Log($"游戏退出，返回码 {code}");
            });
        };

        await Task.CompletedTask.ConfigureAwait(true);
    }

    private async Task<JavaRuntime?> EnsureJavaAsync(int requiredMajor, CancellationToken ct)
    {
        var chosen = JavaLocator.Choose(JavaRuntimes, requiredMajor);
        if (chosen != null) return chosen;

        // Rescan once: the user may have installed a JDK since start-up.
        var found = await new JavaLocator(_settings).DiscoverAsync(null, ct).ConfigureAwait(true);
        JavaRuntimes.Clear();
        foreach (var j in found) JavaRuntimes.Add(j);
        chosen = JavaLocator.Choose(found, requiredMajor);
        OnPropertyChanged(nameof(JavaHint));
        OnPropertyChanged(nameof(JavaOverrideHint));
        OnPropertyChanged(nameof(HasJavaOverrideHint));

        if (chosen != null) return chosen;

        // Auto-download the right Temurin JRE when none is installed (and allowed).
        if (!_settings.AutoDownloadJava) return null;

        int target = JavaDownloader.TargetMajorFor(requiredMajor);
        StageText = $"下载 Java {target}（自动）";
        StatusText = $"缺少 Java {target}，正在自动下载…";
        Log($"未找到 Java {requiredMajor}，自动下载 Temurin Java {target}");

        try
        {
            string? javaw = await JavaDownloader.EnsureAsync(target, _paths, new Progress<string>(Log), ct)
                                                 .ConfigureAwait(true);
            if (javaw == null)
            {
                Log($"Java {target} 下载失败");
                return null;
            }

            found = await new JavaLocator(_settings).DiscoverAsync(null, ct).ConfigureAwait(true);
            JavaRuntimes.Clear();
            foreach (var j in found) JavaRuntimes.Add(j);
            OnPropertyChanged(nameof(JavaHint));
            chosen = JavaLocator.Choose(found, requiredMajor);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            Log($"Java 自动下载失败：{ex.Message}");
        }
        finally
        {
            StageText = "";
        }

        return chosen;
    }

    private static string Redact(string cmd)
    {
        var parts = cmd.Split(' ');
        for (int i = 0; i < parts.Length; i++)
            if (parts[i] == "--accessToken" && i + 1 < parts.Length) parts[i + 1] = "<redacted>";
        return string.Join(' ', parts);
    }

    // ============================================================ misc/plumb ===

    private void ExportDiagnostics()
    {
        try
        {
            string dir = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory), "ECL 诊断");

            string zip = Services.DiagnosticBundle.Create(_paths, _settings, dir);
            Log("诊断包：" + zip);
            StatusText = "诊断包已生成：" + zip;
            OpenUrl(dir);
        }
        catch (Exception ex)
        {
            ErrorText = "导出诊断包失败：" + ex.Message;
            Log("导出诊断包失败：" + ex);
        }
    }

    private void BrowseJava()
    {
        try
        {
            var dialog = new Microsoft.Win32.OpenFileDialog
            {
                Title = "选择 java.exe",
                Filter = "java.exe|java.exe|可执行文件 (*.exe)|*.exe",
                CheckFileExists = true
            };

            if (dialog.ShowDialog() != true) return;

            string picked = dialog.FileName;
            if (!Path.GetFileName(picked).Equals("java.exe", StringComparison.OrdinalIgnoreCase))
            {
                ErrorText = "请选择 bin 目录下的 java.exe。";
                return;
            }

            _settings.JavaPath = picked;
            _settings.Save(LauncherSettings.ResolveSettingsPath());
            Log("手动指定 Java：" + picked);
            OnPropertyChanged(nameof(JavaOverrideHint));
        OnPropertyChanged(nameof(HasJavaOverrideHint));
            _ = DetectJavaAsync();
        }
        catch (Exception ex)
        {
            ErrorText = ex.Message;
        }
    }

    private async Task DetectJavaAsync()
    {
        var locator = new JavaLocator(_settings);
        StatusText = "正在检测 Java…";
        var list = await locator.DiscoverAsync(new Progress<string>(Log), CancellationToken.None)
                                .ConfigureAwait(true);

        JavaRuntimes.Clear();
        foreach (var j in list) JavaRuntimes.Add(j);

        // The override hint depends on whether anything was found, so it has to be
        // re-raised here -- otherwise "还没找到 Java" stays on screen under a Java 21 row.
        OnPropertyChanged(nameof(JavaHint));
        OnPropertyChanged(nameof(JavaOverrideHint));
        OnPropertyChanged(nameof(HasJavaOverrideHint));

        // Interpolated: translate the template, then format, so the number still lands.
        StatusText = list.Count > 0
            ? string.Format(Services.Localization.T("检测到 {0} 个 Java 运行时"), list.Count)
            : Services.Localization.T("未检测到 Java，请安装 JDK/JRE");
        Log("Java 运行时：" + string.Join("、", list.Select(r => $"Java {r.Info.Major}")));
    }

    private void SaveSettings()
    {
        string oldFamily = _settings.ThemeFamily;
        string oldAccent = _settings.ThemeAccent;
        double oldAcrylic = _settings.AcrylicOpacity;

        string oldUiLanguage = _settings.UiLanguage;
        _settings.UiLanguage = UiLanguageIndex switch { 1 => "zh", 2 => "en", _ => "auto" };

        _settings.ThemeFamily = Themes.ThemeManager.FamilyIds[Math.Clamp(_themeFamilyIndex, 0, 2)];
        _settings.ThemeAccent = Themes.ThemeManager.AccentIds[Math.Clamp(_themeAccentIndex, 0, 4)];

        _settings.Normalize();
        _settings.Save(LauncherSettings.ResolveSettingsPath());

        // Language is resolved once at start-up and the visual tree is rewritten then,
        // so a change needs the same restart the theme does.
        if (!string.Equals(oldUiLanguage, _settings.UiLanguage, StringComparison.OrdinalIgnoreCase))
        {
            StatusText = "界面语言已保存，正在重启…";
            Log($"界面语言：{oldUiLanguage} → {_settings.UiLanguage}，重启生效");
            RestartLauncher();
            return;
        }

        bool themeChanged = !string.Equals(oldFamily, _settings.ThemeFamily, StringComparison.OrdinalIgnoreCase) ||
                            !string.Equals(oldAccent, _settings.ThemeAccent, StringComparison.OrdinalIgnoreCase);

        // The acrylic opacity bakes into the same brushes, so it needs the same restart.
        if (!themeChanged && _settings.ThemeFamily == "acrylic" &&
            Math.Abs(oldAcrylic - _settings.AcrylicOpacity) > 0.001)
        {
            StatusText = "毛玻璃不透明度已保存，正在重启…";
            Log($"毛玻璃不透明度：{oldAcrylic * 100:0}% → {_settings.AcrylicOpacity * 100:0}%，重启生效");
            RestartLauncher();
            return;
        }
        if (themeChanged)
        {
            // Styles bake the palette in at parse time, so a theme switch is a
            // process restart rather than a live swap.
            StatusText = "主题已保存，正在重启…";
            Log($"主题切换：{oldFamily}/{oldAccent} → {_settings.ThemeFamily}/{_settings.ThemeAccent}，重启生效");
            RestartLauncher();
            return;
        }

        // Resolve the same way GamePaths does. A bare GetFullPath("data") would use the
        // working directory, so portable installs would look "changed" on every save and
        // rebuild the path objects (and re-scan mods) for nothing.
        string resolvedRoot = Path.GetFullPath(Path.IsPathRooted(_settings.DataRoot)
            ? _settings.DataRoot
            : Path.Combine(AppContext.BaseDirectory, _settings.DataRoot));

        bool rootChanged = !string.Equals(_paths.Root, resolvedRoot, StringComparison.OrdinalIgnoreCase);
        bool isolationChanged = _paths.IsolateByVersion != _settings.IsolateByVersion;
        if (rootChanged || isolationChanged)
        {
            _paths = new GamePaths(_settings.DataRoot, _settings.IsolateByVersion);
            _mods = null;
            _paths.EnsureLayout();
            _api?.Dispose();
            _api = new ApiClient(_settings, _paths.Root);
            _accounts = new AccountStore(_paths.AccountsFile);
            RefreshAccounts(_accounts.EnsureDefault().Id);
            OnPropertyChanged(nameof(GameDirectory));
            Log(isolationChanged ? $"版本隔离：{(_paths.IsolateByVersion ? "开启" : "关闭")}" : "数据目录已切换：" + _paths.Root);
            RefreshInstalled();
            RefreshMods();
        }

        RebuildAuthService();
        StatusText = "设置已保存";
        OnPropertyChanged(nameof(UsingLegacyClientId));
        OnPropertyChanged(nameof(MsClientIdHint));
    }

    private void BrowseDataRoot()
    {
        try
        {
            var dialog = new Microsoft.Win32.OpenFolderDialog
            {
                Title = "选择游戏数据目录（.minecraft 等效）",
                InitialDirectory = Directory.Exists(DataRoot) ? DataRoot : _paths.Root
            };
            if (dialog.ShowDialog() == true)
                DataRoot = dialog.FolderName;
        }
        catch (Exception ex)
        {
            ErrorText = ex.Message;
        }
    }

    /// <summary>
    /// Switches the data root to an existing install found by
    /// <see cref="Services.ExistingInstallFinder"/>.
    ///
    /// <para>Version isolation has to go with it: the profiles in that folder have
    /// no <c>versions/&lt;id&gt;/game</c>, so with isolation on the launcher would
    /// create an empty game directory and the user's <c>mods</c>, <c>saves</c> and
    /// <c>options.txt</c> would look lost even though they are right there.</para>
    /// </summary>
    private void AdoptExistingInstall()
    {
        if (_existingInstall is { } found) Adopt(found);
    }

    /// <summary>
    /// Fallback for installs the automatic scan cannot know about (<c>D:\Games\.minecraft</c>).
    /// Accepts the game root, its <c>versions</c> folder, or a single profile folder.
    /// </summary>
    private void BrowseForInstall()
    {
        try
        {
            var dialog = new Microsoft.Win32.OpenFolderDialog
            {
                Title = Services.Localization.T("选择已有的 .minecraft 目录"),
                InitialDirectory = _paths.Root
            };

            if (dialog.ShowDialog() != true) return;

            var found = Services.ExistingInstallFinder.Inspect(dialog.FolderName);
            if (found == null)
            {
                ErrorText = Services.Localization.T("这个目录里没有找到版本（应有 versions/版本/版本.json）");
                Log($"目录不可用：{dialog.FolderName}");
                return;
            }

            Adopt(found);
        }
        catch (Exception ex)
        {
            ErrorText = ex.Message;
        }
    }

    /// <summary>
    /// Moves the launcher's own per-root files (accounts, selected skin) into a
    /// newly adopted root. Existing files in the target always win: the folder may
    /// already belong to another launcher or another ECL install.
    /// </summary>
    private void CarryOverLauncherData(string targetRoot)
    {
        try
        {
            string accounts = Path.Combine(_paths.Root, "accounts.json");
            string accountsTarget = Path.Combine(targetRoot, "accounts.json");

            if (File.Exists(accounts) && !File.Exists(accountsTarget))
            {
                File.Copy(accounts, accountsTarget);
                Log("已迁移账户：accounts.json");
            }

            string skins = Path.Combine(_paths.Root, "skins");
            string skinsTarget = Path.Combine(targetRoot, "skins");

            if (Directory.Exists(skins) && !Directory.Exists(skinsTarget))
            {
                Directory.CreateDirectory(skinsTarget);
                foreach (string file in Directory.EnumerateFiles(skins, "*", SearchOption.TopDirectoryOnly))
                    File.Copy(file, Path.Combine(skinsTarget, Path.GetFileName(file)), overwrite: false);
                Log("已迁移皮肤：skins/");
            }
        }
        catch (Exception ex)
        {
            // A read-only target must not block the switch; the user can sign in again.
            Log("迁移启动器数据失败：" + ex.Message);
        }
    }

    private void Adopt(Services.ExistingInstall found)
    {
        string message =
            string.Format(Services.Localization.T("把数据目录切换到 {0} 吗？"), found.Root) + "\n\n" +
            string.Format(Services.Localization.T("里面有 {0} 个版本，{1} 个可以直接启动。"),
                found.VersionCount, found.ReadyCount) + "\n" +
            Services.Localization.T("切换后会关闭「版本隔离」，原目录的 mods、saves、options.txt 继续沿用。");

        var answer = MessageBox.Show(
            System.Windows.Application.Current?.MainWindow!, message,
            Services.Localization.T("使用已有 Minecraft 目录"),
            MessageBoxButton.OKCancel, MessageBoxImage.Question);

        if (answer != MessageBoxResult.OK) return;

        // accounts.json and skins/ live under the data root, so switching roots
        // would silently sign the user out. Carry them over unless the target
        // already has its own -- never overwrite somebody else's files.
        CarryOverLauncherData(found.Root);

        IsolateByVersion = false;
        DataRoot = found.Root;

        // Persists, swaps _paths/_api/_accounts and re-scans the version list.
        SaveSettings();
        Log($"接管已有目录：{found.Root}（{found.VersionCount} 个版本）");
        StatusText = string.Format(
            Services.Localization.T("已切换到 {0}"), found.Root);
    }

    private void OpenDataFolder()
    {
        try
        {
            Directory.CreateDirectory(_paths.Root);
            OpenUrl(_paths.Root);
        }
        catch (Exception ex)
        {
            ErrorText = ex.Message;
        }
    }

    private void RestartLauncher()
    {
        try
        {
            string? exe = Environment.ProcessPath;
            if (string.IsNullOrEmpty(exe))
            {
                ErrorText = "找不到启动器可执行文件，主题将在下次手动启动时生效。";
                return;
            }
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(exe) { UseShellExecute = true });
            Application.Current?.Shutdown();
        }
        catch (Exception ex)
        {
            ErrorText = "重启失败：" + ex.Message + "（主题已保存，下次启动生效）";
        }
    }

    private static void OpenUrl(string url)
    {
        bool isPath = !url.StartsWith("http", StringComparison.OrdinalIgnoreCase);

        if (isPath)
        {
            // ShellExecute on a missing folder pops Windows' own
            // "找不到指定的文件" dialog, which reads as a launcher bug. Create the
            // folder first, then let explorer.exe take it (it copes with spaces and
            // characters that trip the shell verb).
            try
            {
                if (!Directory.Exists(url) && !File.Exists(url))
                    Directory.CreateDirectory(url);
            }
            catch
            {
                // Read-only or invalid path: fall through and let explorer complain.
            }

            try
            {
                if (Directory.Exists(url))
                {
                    System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
                    {
                        FileName = "explorer.exe",
                        Arguments = "\"" + url + "\"",
                        UseShellExecute = true
                    });
                    return;
                }
            }
            catch
            {
                // fall through to the plain shell call
            }
        }

        try
        {
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
            {
                FileName = url,
                UseShellExecute = true
            });
        }
        catch
        {
            // shell refused; nothing useful to do
        }
    }

    // ================================================================ tasks ===

    /// <summary>Newest first, capped at 20 so a long session stays readable.</summary>
    public ObservableCollection<TaskItem> Tasks { get; } = new();

    public bool HasTasks => Tasks.Count > 0;

    public int RunningTaskCount => Tasks.Count(t => t.IsRunning);

    public bool HasRunningTask => RunningTaskCount > 0;

    public string TaskSummary => RunningTaskCount switch
    {
        0 => "最近任务",
        1 => "1 个任务进行中",
        _ => $"{RunningTaskCount} 个任务进行中"
    };

    private TaskItem? _currentTask;

    /// <summary>
    /// Registers a long-running operation in the task menu. The caller flips its
    /// state when the work ends; cancelling goes through the same CTS the operation
    /// already awaits.
    /// </summary>
    private TaskItem BeginTask(string title)
    {
        var task = new TaskItem(title, new RelayCommand(() => _cts?.Cancel()));
        _currentTask = task;
        Tasks.Insert(0, task);

        while (Tasks.Count > 20) Tasks.RemoveAt(Tasks.Count - 1);

        NotifyTaskState();
        return task;
    }

    private void EndTask(TaskItem task, TaskState state, string? detail = null)
    {
        task.State = state;
        if (!string.IsNullOrWhiteSpace(detail)) task.Detail = detail;
        if (ReferenceEquals(_currentTask, task)) _currentTask = null;
        NotifyTaskState();
    }

    private void NotifyTaskState()
    {
        OnPropertyChanged(nameof(HasTasks));
        OnPropertyChanged(nameof(RunningTaskCount));
        OnPropertyChanged(nameof(HasRunningTask));
        OnPropertyChanged(nameof(TaskSummary));
        (ClearFinishedTasksCommand as RelayCommand)?.RaiseCanExecuteChanged();
    }

    public System.Windows.Input.ICommand ClearFinishedTasksCommand { get; private set; } = null!;

    private void ClearFinishedTasks()
    {
        for (int i = Tasks.Count - 1; i >= 0; i--)
        {
            if (Tasks[i].IsFinished) Tasks.RemoveAt(i);
        }
        NotifyTaskState();
    }

    public void Log(string message)
    {
        // One choke point: every log line passes here, so translating the stable
        // fragments of the already-interpolated text covers all ~75 call sites.
        message = Services.Localization.TranslateLog(message);

        AppendLog($"[{DateTime.Now:HH:mm:ss}] {message}");

        // The task row shows the newest log line — that is the cheapest honest
        // progress indicator, since downloads already log their byte counts.
        var task = _currentTask;
        if (task is not { IsRunning: true }) return;

        string detail = message.Length <= 90 ? message : message[..90] + "…";
        var dispatcher = System.Windows.Application.Current?.Dispatcher;
        if (dispatcher == null || dispatcher.CheckAccess()) task.Detail = detail;
        else dispatcher.BeginInvoke(() => task.Detail = detail);
    }

    private void AppendLog(string line)
    {
        lock (_logBuffer)
        {
            _logBuffer.AppendLine(line);
            if (_logBuffer.Length > 400_000)
            {
                _logBuffer.Remove(0, 200_000);

                // After trimming, the length could coincidentally equal the last
                // flushed length; FlushLog would then skip the update and the UI
                // would keep showing stale text forever. -1 forces a rebuild.
                _shownLength = -1;
            }
        }
    }

    private void FlushLog()
    {
        lock (_logBuffer)
        {
            if (_logBuffer.Length == 0 || _shownLength == _logBuffer.Length) return;

            int from = (int)_shownLength;
            _shownLength = _logBuffer.Length;
            LogText = _logBuffer.ToString();

            // Mirror the new tail to disk. Until now the launcher log existed only in
            // memory, so a misbehaving run left nothing to look at -- and "日志有问题"
            // could not be checked after the fact either.
            PersistLogSlice(from);
        }
    }

    private string? _uiLogPath;
    private int _uiLogBytes;

    private void PersistLogSlice(int from)
    {
        try
        {
            _uiLogPath ??= Path.Combine(_paths.LogsDir, "ecl-ui.log");
            if (_uiLogBytes == 0)
            {
                Directory.CreateDirectory(_paths.LogsDir);

                // Rotate once at 8 MB so a long session cannot fill the disk.
                if (File.Exists(_uiLogPath) && new FileInfo(_uiLogPath).Length > 8 * 1024 * 1024)
                {
                    string old = _uiLogPath + ".1";
                    File.Copy(_uiLogPath, old, overwrite: true);
                    File.WriteAllText(_uiLogPath, "");
                }

                _uiLogBytes = File.Exists(_uiLogPath) ? (int)Math.Min(int.MaxValue, new FileInfo(_uiLogPath).Length) : 0;
            }

            string slice = _logBuffer.ToString(from, _logBuffer.Length - from);
            File.AppendAllText(_uiLogPath, slice);
            _uiLogBytes += slice.Length;
        }
        catch
        {
            // Diagnostics must never break the UI.
        }
    }

    internal static string FormatSpeed(double bytesPerSecond)
    {
        if (bytesPerSecond <= 0) return "";
        if (bytesPerSecond > 1024 * 1024) return $"{bytesPerSecond / 1024 / 1024:F2} MB/s";
        return $"{bytesPerSecond / 1024:F0} KB/s";
    }
}
