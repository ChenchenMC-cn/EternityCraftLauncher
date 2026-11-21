// SPDX-License-Identifier: MPL-2.0
// Copyright (c) 2026 ChenchenMC <https://github.com/ChenchenMC-cn/EternityCraftLauncher>
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using ECL.ViewModels;

namespace ECL;

public partial class MainWindow : Window
{
    private readonly MainViewModel _vm;
    private string? _acrylicStatus;

    public MainWindow() : this(null)
    {
    }

    public MainWindow(string? dataRoot)
    {
        _vm = new MainViewModel(dataRoot);
        App.Mark("view model ready");

        InitializeComponent();
        App.Mark("xaml inflated");

        DataContext = _vm;
        App.Mark("data context bound");
        Loaded += OnLoaded;
        StateChanged += (_, _) => UpdateMaximizeGlyph();

        // Controls created *after* the first localization pass -- list containers from a
        // data template, a page realised on navigation -- never get translated by a
        // one-shot walk. Re-run it whenever content appears, coalesced to one pass.
        _vm.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName != nameof(MainViewModel.ActivePage)) return;
            EnsurePageTemplate(_vm.ActivePage);
            RequestLocalize();
        };
        EnsurePageTemplate(_vm.ActivePage);
        _vm.Versions.CollectionChanged += (_, _) => RequestLocalize();
        _vm.InstalledVersions.CollectionChanged += (_, _) => RequestLocalize();
        _vm.MarketResults.CollectionChanged += (_, _) => RequestLocalize();

        if (App.ShotConfirm)
            Loaded += (_, _) => _vm.ShowConfirmPreview();

        // VersionList now lives inside InstallPageTemplate, so its wiring happens in
        // BindInstallNames() when that page is first materialized.
    }

    /// <summary>
    /// Instantiates a deferred page the first time it is opened.
    ///
    /// <para>The mods and accounts pages used to be built during start-up even though most
    /// sessions never look at them. Their markup lives in a DataTemplate now; assigning it
    /// here is what actually creates the controls, and doing it once keeps scroll position
    /// and control state alive across page switches.</para>
    /// </summary>
    private void EnsurePageTemplate(int page)
    {
        switch (page)
        {
            case 0: Defer(HomeHost, "HomePageTemplate", BindHomeNames); break;
            case 1: Defer(InstallHost, "InstallPageTemplate", BindInstallNames); break;
            case 2: Defer(ModsHost, "ModsPageTemplate", null); break;
            case 3: Defer(AccountsHost, "AccountsPageTemplate", null); break;
            case 4: Defer(SettingsHost, "SettingsPageTemplate", BindSettingsNames); break;
            case 5: Defer(LogHost, "LogPageTemplate", BindLogNames); break;
        }
    }

    /// <summary>
    /// Assigns the template once, then hands the freshly created controls to a binder.
    /// Re-entrant calls are cheap and must not re-bind (the handlers would double up).
    /// </summary>
    private void Defer(ContentControl host, string templateKey, Action? bindNames)
    {
        if (host.ContentTemplate != null) return;

        Reveal(host);
        host.ContentTemplate = (DataTemplate)FindResource(templateKey);

        if (bindNames != null)
        {
            // Try now (works when the page is opened after the window is live), and again
            // once the host loads, which is the first moment the template content exists
            // during start-up. Binders are idempotent, so the second call is a no-op.
            RoutedEventHandler? onLoaded = null;
            onLoaded = (_, _) =>
            {
                host.Loaded -= onLoaded;
                bindNames();
            };
            host.Loaded += onLoaded;
        }

        MaterializeDeferred(host);
        bindNames?.Invoke();
    }

    /// <summary>
    /// Finds a named control inside a deferred page's template.
    ///
    /// <para>Returns null instead of throwing: <see cref="FrameworkTemplate.FindName"/>
    /// raises "only valid on elements this template has been applied to" until the
    /// ContentPresenter has measured. During the constructor that is the normal state,
    /// and the Loaded hook in <see cref="Defer"/> retries.</para>
    /// </summary>
    private T? FindInTemplate<T>(ContentControl host, string name) where T : class
    {
        try { return host.ContentTemplate?.FindName(name, host) as T; }
        catch (InvalidOperationException) { return null; }
    }

    private void BindHomeNames()
    {
        // ErrorBar is styled purely in XAML today; kept here so a future binding has a home.
        _ = FindInTemplate<FrameworkElement>(HomeHost, "ErrorBar");
    }

    private void BindInstallNames()
    {
        if (_versionList != null) return;
        _versionList = FindInTemplate<ListView>(InstallHost, "VersionList");
        if (_versionList == null) return;

        // Keep the highlighted version visible when the list is reloaded and the
        // selection lands far down (e.g. resuming 1.20.1 while 26.x sits on top).
        _versionList.SelectionChanged += (_, _) =>
        {
            if (_versionList!.SelectedItem is not { } item) return;
            Dispatcher.BeginInvoke(new Action(() =>
            {
                _versionList!.UpdateLayout();
                _versionList.ScrollIntoView(item);
            }), System.Windows.Threading.DispatcherPriority.Loaded);
        };
    }

    private void BindSettingsNames()
    {
        if (_settingsScroll != null) return;
        _settingsScroll = FindInTemplate<ScrollViewer>(SettingsHost, "SettingsScroll");
    }

    private void BindLogNames()
    {
        _taskToggle ??= FindInTemplate<System.Windows.Controls.Primitives.ToggleButton>(LogHost, "TaskToggle");
        _taskPopup ??= FindInTemplate<System.Windows.Controls.Primitives.Popup>(LogHost, "TaskPopup");
    }

    /// <summary>
    /// Runs the first layout of a just-revealed deferred page, then localizes it.
    ///
    /// <para>Assigning ContentTemplate only marks the host dirty; the controls exist after
    /// the next measure. Translating before that finds nothing, which is exactly the bug
    /// the first version of this had (an all-Chinese mods page in an English UI).</para>
    /// </summary>
    private void MaterializeDeferred(FrameworkElement host)
    {
        // First pass is synchronous: the screenshot path (--shot-page N) switches page and
        // shuts down in the same tick, so a queued callback would never run.
        host.ApplyTemplate();
        host.UpdateLayout();
        int replaced = Services.Localization.Apply(this);

        // Second pass once the dispatcher goes idle. Some inlines of a freshly applied
        // template only appear after a full layout pass, and the walk above finishes
        // before that -- which is why a handful of Runs stayed Chinese. Idempotent.
        Dispatcher.BeginInvoke(new Action(() =>
        {
            Services.Localization.Apply(this);
            AuditPage(host);
        }), System.Windows.Threading.DispatcherPriority.Background);

        App.StartupLog($"[localize] deferred {host.Name}: replaced {replaced}");
    }

    /// <summary>Writes the strings a page still shows in Chinese, for --i18n-audit.</summary>
    private void AuditPage(FrameworkElement host)
    {
        if (!App.I18nAudit) return;

        var missing = Services.Localization.Audit(this);
        string line = $"[{DateTime.Now:HH:mm:ss}] {host.Name}: {missing.Count} untranslated\n" +
                      string.Join("\n", missing.ConvertAll(m =>
                          "    " + m.Replace("\n", " / ") + "   <<" +
                          (Services.Localization.LastAuditKinds.TryGetValue(m, out var k) ? k : "?") + ">>")) + "\n";
        try
        {
            System.IO.File.AppendAllText(
                System.IO.Path.Combine(AppContext.BaseDirectory, "i18n-audit.txt"), line);
        }
        catch { }
    }

    /// <summary>
    /// Depth-first search for the first descendant of a type. Used as a fallback when a
    /// template's name scope is not established yet (FindName returns null before the
    /// first measure of a freshly applied template).
    /// </summary>
    private static T? FindDescendant<T>(DependencyObject? root) where T : DependencyObject
    {
        if (root == null) return null;

        int count = System.Windows.Media.VisualTreeHelper.GetChildrenCount(root);
        for (int i = 0; i < count; i++)
        {
            var child = System.Windows.Media.VisualTreeHelper.GetChild(root, i);
            if (child is T hit) return hit;
            if (FindDescendant<T>(child) is { } deep) return deep;
        }

        return null;
    }

    /// <summary>
    /// Pulls the page-visibility binding through immediately.
    ///
    /// <para>Our ActivePage handler can run before the IsXxxPage binding has been updated,
    /// and a collapsed host never measures its template -- which is why the first attempt
    /// produced a visible-but-untranslated page.</para>
    /// </summary>
    private static void Reveal(FrameworkElement host) =>
        host.GetBindingExpression(System.Windows.UIElement.VisibilityProperty)?.UpdateTarget();

    // Controls from deferred pages. Resolved when the page is materialized, because
    // x:Name inside a DataTemplate is not addressable from code-behind directly.
    private System.Windows.Controls.ListView? _versionList;
    private System.Windows.Controls.ScrollViewer? _settingsScroll;
    private System.Windows.Controls.Primitives.ToggleButton? _taskToggle;
    private System.Windows.Controls.Primitives.Popup? _taskPopup;

    private bool _localizePending;

    /// <summary>
    /// Schedules one localization pass on the dispatcher queue. List rebuilds fire many
    /// collection events; without coalescing this would walk the tree dozens of times.
    /// </summary>
    private void RequestLocalize()
    {
        if (_localizePending || !Services.Localization.IsEnglish) return;
        _localizePending = true;

        Dispatcher.BeginInvoke(new Action(() =>
        {
            _localizePending = false;
            Services.Localization.Apply(this);
        }), System.Windows.Threading.DispatcherPriority.Background);
    }

    /// <summary>Tint colour as AABBGGRR; alpha scales with the palette opacity.</summary>
    private static uint AcrylicTint(double opacity)
    {
        double veil = Math.Clamp((1.0 - opacity) * 1.6, 0.04, 0.45);
        uint alpha = (uint)Math.Round(veil * 255) << 24;
        return alpha | 0x00140E0B;   // #0B0E14
    }

    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        Loaded -= OnLoaded;
        try
        {
            await _vm.InitializeAsync();
        }
        catch (Exception ex)
        {
            _vm.Log("INIT ERROR: " + ex);
            MessageBox.Show(ex.Message, "ECL 启动失败", MessageBoxButton.OK, MessageBoxImage.Error);
        }

        // Swap every static (non-bound) string for its English counterpart. Done after
        // InitializeAsync so list templates have produced their containers; anything not
        // in the dictionary stays as-is rather than turning blank.
        int localized = Services.Localization.Apply(this);
        _vm.Log(string.Format(
            Services.Localization.T("界面语言：{0}，替换 {1} 处，跳过绑定 {2} 处"),
            Services.Localization.Language, localized, Services.Localization.LastBound));

        if (App.LauncherFamily == Themes.ThemeFamily.Acrylic)
        {
            // The tint shrinks as the palette gets more solid: at 95% the surfaces
            // already hide the wallpaper, so a heavy veil would only muddy the hues.
            // At 70% it keeps the "frosted glass" look from turning into a mirror.
            _acrylicStatus ??= Services.WindowEffects.EnableAcrylic(
                new System.Windows.Interop.WindowInteropHelper(this).Handle,
                AcrylicTint(App.AcrylicOpacity));
            _vm.Log("毛玻璃效果：" + _acrylicStatus);

            // DWM effects are invisible to RenderTargetBitmap screenshots, so the
            // status goes to a temp file the smoke test can read.
            try
            {
                System.IO.File.WriteAllText(
                    System.IO.Path.Combine(System.IO.Path.GetTempPath(), "ecl-acrylic.log"),
                    $"{DateTime.Now:O} {_acrylicStatus}");
            }
            catch { /* diagnostics only */ }
        }

        if (App.ScreenshotPath != null)
        {
            if (App.ScreenshotVersion != null)
            {
                var target = _vm.InstalledVersions.FirstOrDefault(v => v.Id == App.ScreenshotVersion);
                if (target != null) _vm.SelectedInstalled = target;
            }
            else
            {
                // Screenshot mode only: pick a profile that has mods so the mods page
                // renders something meaningful instead of the empty state.
                var withMods = _vm.InstalledVersions.FirstOrDefault(v => v.ModCount > 0);
                if (withMods != null) _vm.SelectedInstalled = withMods;
            }

            if (App.ScreenshotLoader is { } loader) _vm.LoaderIndex = loader;
            if (App.ScreenshotMarket)
            {
                _vm.IsMarketOpen = true;
                _vm.ModSearchQuery = "sodium";
                _ = _vm.SearchMarketAsync();
            }
            if (App.ScreenshotLogin)
            {
                // UI regression hook: render the paste-back panel with sample text
                // instead of opening a real browser window.
                _vm.ActivePage = 3;
                _vm.PrepareLoginShot();
            }
            if (App.ScreenshotPicker)
            {
                _vm.ActivePage = 2;
                _vm.PreparePickerShot();
            }
            if (App.ScreenshotDeps)
            {
                _vm.ActivePage = 2;
                _vm.PrepareDependencyShot();
            }
            if (App.ScreenshotTask)
            {
                // Fill the task menu with one row per state, then leave it open so
                // the popup can be rendered separately (a Popup is its own window,
                // so it never appears in the main-window bitmap).
                _vm.PrepareTaskShot();
                EnsurePageTemplate(5);
                _ = Dispatcher.BeginInvoke(new Action(() => { if (_taskToggle != null) _taskToggle.IsChecked = true; }),
                    System.Windows.Threading.DispatcherPriority.Loaded);
            }
            ScheduleScreenshot(App.ScreenshotPath);
        }
    }

    /// <summary>Renders each page to a PNG so the UI can be inspected headlessly.</summary>
    private void ScheduleScreenshot(string path)
    {
        string dir = System.IO.Path.GetDirectoryName(path) ?? ".";
        string stem = System.IO.Path.GetFileNameWithoutExtension(path);
        string ext = System.IO.Path.GetExtension(path);
        if (string.IsNullOrEmpty(ext)) ext = ".png";

        int page = 0;
        var timer = new System.Windows.Threading.DispatcherTimer
        {
            // A targeted capture waits longer: the install page fetches Forge builds
            // and OptiFine editions over the network before it is worth photographing.
            Interval = TimeSpan.FromSeconds(App.ScreenshotPage is null ? 2.4 : 8.0)
        };

        void Save(string file)
        {
            try
            {
                var dpi = System.Windows.Media.VisualTreeHelper.GetDpi(this);
                var rtb = new System.Windows.Media.Imaging.RenderTargetBitmap(
                    (int)(ActualWidth * dpi.DpiScaleX), (int)(ActualHeight * dpi.DpiScaleY),
                    dpi.PixelsPerInchX, dpi.PixelsPerInchY,
                    System.Windows.Media.PixelFormats.Pbgra32);
                rtb.Render(this);

                var encoder = new System.Windows.Media.Imaging.PngBitmapEncoder();
                encoder.Frames.Add(System.Windows.Media.Imaging.BitmapFrame.Create(rtb));
                using var fs = System.IO.File.Create(file);
                encoder.Save(fs);
                _vm.Log("Screenshot saved to " + file);

                // Binding diagnostics for the sign-in panel: a screenshot alone
                // cannot prove the device code reached the view model.
                if (App.ScreenshotLogin)
                {
                    try
                    {
                        System.IO.File.WriteAllText(
                            System.IO.Path.Combine(System.IO.Path.GetTempPath(), "ecl-login-shot.txt"),
                            $"ShowPasteBox={_vm.ShowPasteBox}{Environment.NewLine}" +
                            $"StatusText={_vm.StatusText}{Environment.NewLine}");
                    }
                    catch { /* diagnostics only */ }
                }

                // The task popup is a separate HWND, so render its content separately.
                if (App.ScreenshotPicker)
            {
                _vm.ActivePage = 2;
                _vm.PreparePickerShot();
            }
            if (App.ScreenshotDeps)
            {
                _vm.ActivePage = 2;
                _vm.PrepareDependencyShot();
            }
            if (App.ScreenshotTask)
                {
                    try
                    {
                        EnsurePageTemplate(5);
                        if (_taskPopup?.Child is System.Windows.FrameworkElement content &&
                            content.ActualWidth > 0 && content.ActualHeight > 0)
                        {
                            var dpi2 = System.Windows.Media.VisualTreeHelper.GetDpi(content);
                            var prtb = new System.Windows.Media.Imaging.RenderTargetBitmap(
                                (int)(content.ActualWidth * dpi2.DpiScaleX),
                                (int)(content.ActualHeight * dpi2.DpiScaleY),
                                dpi2.PixelsPerInchX, dpi2.PixelsPerInchY,
                                System.Windows.Media.PixelFormats.Pbgra32);
                            prtb.Render(content);

                            var enc2 = new System.Windows.Media.Imaging.PngBitmapEncoder();
                            enc2.Frames.Add(System.Windows.Media.Imaging.BitmapFrame.Create(prtb));
                            string taskPath = System.IO.Path.Combine(
                                System.IO.Path.GetDirectoryName(file) ?? ".",
                                System.IO.Path.GetFileNameWithoutExtension(file) + "-tasks.png");
                            using var fs2 = System.IO.File.Create(taskPath);
                            enc2.Save(fs2);
                            _vm.Log("Task menu screenshot saved to " + taskPath);
                        }
                    }
                    catch (Exception ex)
                    {
                        _vm.Log("Task menu screenshot failed: " + ex.Message);
                    }
                }
            }
            catch (Exception ex)
            {
                _vm.Log("Screenshot failed: " + ex.Message);
            }
        }

        timer.Tick += (_, _) =>
        {
            if (App.ScreenshotPage is { } single)
            {
                timer.Stop();
                _vm.ActivePage = single;
                if (App.PackTab > 0) _vm.ShowPackTab(App.PackTab);
                UpdateLayout();

                // --shot-scroll: the settings page is taller than the window, and a
                // screenshot of the top half says nothing about the cards below it.
                if (App.ScreenshotScroll > 0) ScrollPageBy(App.ScreenshotScroll);

                Save(path);
                Application.Current?.Shutdown();
                return;
            }

            if (page > 5)
            {
                timer.Stop();
                // Headless capture: quit once every page has been rendered.
                Application.Current?.Shutdown();
                return;
            }
            _vm.ActivePage = page;
            UpdateLayout();
            Save(System.IO.Path.Combine(dir, $"{stem}_{page}{ext}"));
            page++;
        };
        timer.Start();
    }

    /// <summary>
    /// Scrolls the settings page for --shot-scroll captures. Deliberately targets the
    /// named scroller instead of hunting the visual tree: the first version grabbed a
    /// ListBox's inner viewer and produced a blank screenshot.
    /// </summary>
    private void ScrollPageBy(int pixels)
    {
        // The settings page is deferred now: make sure it exists before scrolling it,
        // otherwise --shot-scroll silently does nothing.
        EnsurePageTemplate(4);
        SettingsHost.UpdateLayout();
        _settingsScroll ??= FindInTemplate<ScrollViewer>(SettingsHost, "SettingsScroll")
                            ?? FindDescendant<ScrollViewer>(SettingsHost);
        _settingsScroll?.ScrollToVerticalOffset(pixels);
        UpdateLayout();
    }

    private void OnMinimize(object sender, RoutedEventArgs e) => WindowState = WindowState.Minimized;

    private void OnMaximize(object sender, RoutedEventArgs e)
        => WindowState = WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;

    private void OnClose(object sender, RoutedEventArgs e) => Close();

    private void UpdateMaximizeGlyph()
    {
        if (MaxButton != null)
            MaxButton.Content = WindowState == WindowState.Maximized ? "\uE923" : "\uE922";
    }

    protected override void OnMouseLeftButtonDown(MouseButtonEventArgs e)
    {
        // WindowChrome handles dragging inside the caption area; double click on
        // the custom title bar toggles maximize like a normal window would.
        if (e.ClickCount == 2 && e.GetPosition(this).Y < 48)
        {
            OnMaximize(this, new RoutedEventArgs());
            return;
        }
        base.OnMouseLeftButtonDown(e);
    }
}
