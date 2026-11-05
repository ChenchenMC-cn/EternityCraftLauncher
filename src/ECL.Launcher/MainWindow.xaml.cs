// SPDX-License-Identifier: MPL-2.0
// Copyright (c) 2026 ChenchenMC <https://github.com/ChenchenMC-cn/EternityCraftLauncher>
using System.Windows;
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
        InitializeComponent();
        DataContext = _vm;
        Loaded += OnLoaded;
        StateChanged += (_, _) => UpdateMaximizeGlyph();

        // Keep the highlighted version visible when the list is reloaded and the
        // selection lands far down (e.g. resuming 1.20.1 while 26.x sits on top).
        VersionList.SelectionChanged += (_, _) =>
        {
            if (VersionList.SelectedItem is not { } item) return;
            // The list is rebuilt (Clear + Add) right before the selection lands,
            // so scrolling must wait until layout has produced containers.
            Dispatcher.BeginInvoke(new Action(() =>
            {
                VersionList.UpdateLayout();
                VersionList.ScrollIntoView(item);
            }), System.Windows.Threading.DispatcherPriority.Loaded);
        };
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
                _ = Dispatcher.BeginInvoke(new Action(() => TaskToggle.IsChecked = true),
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
                        if (TaskPopup.Child is System.Windows.FrameworkElement content &&
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
        SettingsScroll.ScrollToVerticalOffset(pixels);
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
