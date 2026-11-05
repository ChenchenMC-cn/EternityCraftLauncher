// SPDX-License-Identifier: MPL-2.0
// Copyright (c) 2026 ChenchenMC <https://github.com/ChenchenMC-cn/EternityCraftLauncher>
using System.Windows;
using System.Windows.Threading;
using ECL.Services;

namespace ECL;

public partial class App : Application
{
    public App()
    {
        // A previous edit to this file silently missed because the class declaration
        // is not indented; keep this line so a future miss is obvious in startup.log.
        StartupLog("App ctor: installing crash handlers");

        // Both hooks are needed. The dispatcher one fires for exceptions raised on the
        // UI thread (where nearly everything runs); the AppDomain one catches
        // background threads, where WPF cannot help and the process dies regardless.
        DispatcherUnhandledException += (_, e) =>
        {
            string path = ErrorReporter.WriteCrashFile(e.Exception, "UI 线程");
            StartupLog("unhandled UI exception -> " + path);

            // Stop the default "has stopped working" dialog from racing ours.
            e.Handled = true;

            ErrorReporter.ShowCrashReport();
            Shutdown(1);
        };

        AppDomain.CurrentDomain.UnhandledException += (_, e) =>
        {
            string path = ErrorReporter.WriteCrashFile(e.ExceptionObject as Exception, "后台线程");
            StartupLog("unhandled background exception -> " + path);
        };
    }

    /// <summary>Set by <c>--shot &lt;file.png&gt;</c>, used by the UI smoke test.</summary>
    public static string? ScreenshotPath { get; private set; }

    /// <summary>Optional <c>--shot-page N</c>: capture a single page instead of all.</summary>
    public static int? ScreenshotPage { get; private set; }

    /// <summary>Optional <c>--shot-loader N</c>: pre-select a loader on the install page.</summary>
    public static int? ScreenshotLoader { get; private set; }

    /// <summary>Optional <c>--shot-version ID</c>: pre-select an installed version.</summary>
    public static string? ScreenshotVersion { get; private set; }

    /// <summary><c>--shot-market</c>: open the mods page market and search "sodium".</summary>
    public static bool ScreenshotMarket { get; private set; }

    /// <summary><c>--shot-login</c>: start a device-code sign-in so the code panel renders.</summary>
    public static bool ScreenshotLogin { get; private set; }

    /// <summary><c>--shot-task</c>: fill the task menu with samples and open it.</summary>
    public static bool ScreenshotTask { get; private set; }

    private static Services.BindingErrorListener? _bindingListener;

    /// <summary><c>--shot-picker</c> / <c>--shot-deps</c>: overlay screenshots.</summary>
    public static bool ScreenshotPicker { get; private set; }
    public static bool ScreenshotDeps { get; private set; }

    /// <summary>Window family resolved from settings, read by MainWindow for effects.</summary>
    public static Themes.ThemeFamily LauncherFamily { get; private set; } = Themes.ThemeFamily.Original;

    /// <summary>Vertical offset (px) to scroll before a --shot-page capture.</summary>
    public static int ScreenshotScroll { get; private set; }

    /// <summary>Acrylic master opacity for this process (0.70-1.00).</summary>
    public static double AcrylicOpacity { get; private set; } = Themes.ThemeManager.DefaultAcrylicOpacity;

    /// <summary>Settings as loaded at start-up, before MainViewModel touches them.</summary>
    public static Models.LauncherSettings StartupSettings { get; private set; } = null!;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        // Theme before everything: the style dictionary resolves its brushes
        // against the palette at parse time, so this order is load-bearing.
        StartupSettings = LoadStartupSettings(e.Args);

        // Test hook: --theme family[:accent] previews a theme without saving it.
        string? themeArg = ArgValue(e.Args, "--theme");
        if (!string.IsNullOrEmpty(themeArg))
        {
            string[] parts = themeArg.Split(':');
            if (parts[0].Length > 0) StartupSettings.ThemeFamily = parts[0];
            if (parts.Length > 1 && parts[1].Length > 0) StartupSettings.ThemeAccent = parts[1];
        }

        LauncherFamily = Themes.ThemeManager.ParseFamily(StartupSettings.ThemeFamily);
        AcrylicOpacity = StartupSettings.AcrylicOpacity;
        Themes.ThemeManager.Apply(this, LauncherFamily,
            Themes.ThemeManager.ParseAccent(StartupSettings.ThemeAccent), AcrylicOpacity);
        Resources.MergedDictionaries.Add(new System.Windows.ResourceDictionary
        {
            Source = new Uri("Themes/Theme.xaml", UriKind.Relative)
        });

        int shot = Array.IndexOf(e.Args, "--shot");
        if (shot >= 0 && shot + 1 < e.Args.Length)
            ScreenshotPath = e.Args[shot + 1];

        ScreenshotPage = ArgInt(e.Args, "--shot-page");
        ScreenshotScroll = ArgInt(e.Args, "--shot-scroll") ?? 0;
        ScreenshotLoader = ArgInt(e.Args, "--shot-loader");
        ScreenshotVersion = ArgValue(e.Args, "--shot-version");
        ScreenshotMarket = e.Args.Contains("--shot-market");
        ScreenshotLogin = e.Args.Contains("--shot-login");
        ScreenshotTask = e.Args.Contains("--shot-task");
        ScreenshotPicker = e.Args.Contains("--shot-picker");
        ScreenshotDeps = e.Args.Contains("--shot-deps");

        // Headless modes (used by the smoke test, no window is created).
        int install = Array.IndexOf(e.Args, "--install");
        if (install >= 0 && install + 1 < e.Args.Length)
        {
            RunHeadless(() => Services.HeadlessRunner.InstallAsync(
                e.Args[install + 1], ArgValue(e.Args, "--log") ?? "install.log",
                ArgInt(e.Args, "--threads"), ArgValue(e.Args, "--data")));
            return;
        }

        if (e.Args.Contains("--self-test"))
        {
            int rc = 0;
            RunHeadless(() =>
            {
                rc = Services.HeadlessRunner.SelfTest(ArgValue(e.Args, "--log") ?? "selftest.log");
                return Task.FromResult(rc);
            });
            return;
        }

        if (e.Args.Contains("--login"))
        {
            RunHeadless(() => Services.HeadlessRunner.LoginAsync(
                ArgValue(e.Args, "--log") ?? "login.log",
                ArgValue(e.Args, "--data"),
                ArgValue(e.Args, "--callback")));
            return;
        }

        if (e.Args.Contains("--scan-versions"))
        {
            RunHeadless(() =>
            {
                int rc = Services.HeadlessRunner.ScanVersions(
                    ArgValue(e.Args, "--log") ?? "versions.log", ArgValue(e.Args, "--data"));
                return Task.FromResult(rc);
            });
            return;
        }

        if (e.Args.Contains("--find-installs"))
        {
            RunHeadless(() =>
            {
                int rc = Services.HeadlessRunner.ListInstalls(
                    ArgValue(e.Args, "--log") ?? "find-installs.log", ArgValue(e.Args, "--data"));
                return Task.FromResult(rc);
            });
            return;
        }

        if (e.Args.Contains("--diagnose"))
        {
            string outDir = ArgValue(e.Args, "--out")
                            ?? System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory), "ECL 诊断");
            RunHeadless(() =>
            {
                int rc = Services.HeadlessRunner.Diagnose(
                    outDir, ArgValue(e.Args, "--log") ?? "diagnose.log", ArgValue(e.Args, "--data"));
                return Task.FromResult(rc);
            });
            return;
        }

        int bench = Array.IndexOf(e.Args, "--bench");
        if (bench >= 0)
        {
            string? target = bench + 1 < e.Args.Length && !e.Args[bench + 1].StartsWith("--", StringComparison.Ordinal)
                ? e.Args[bench + 1]
                : null;

            RunHeadless(() => Services.HeadlessRunner.BenchAsync(
                target, ArgValue(e.Args, "--log") ?? "bench.log", ArgValue(e.Args, "--data"),
                hashFiles: e.Args.Contains("--hash")));
            return;
        }

        int skinHead = Array.IndexOf(e.Args, "--skin-head");
        if (skinHead >= 0 && skinHead + 1 < e.Args.Length)
        {
            RunHeadless(() =>
            {
                int rc = Services.HeadlessRunner.SkinHeadProbe(
                    e.Args[skinHead + 1], ArgValue(e.Args, "--log") ?? "skin-head.log");
                return Task.FromResult(rc);
            });
            return;
        }

        int loaderMeta = Array.IndexOf(e.Args, "--loader-meta");
        if (loaderMeta >= 0 && loaderMeta + 2 < e.Args.Length)
        {
            RunHeadless(() => Services.HeadlessRunner.LoaderMetaAsync(
                e.Args[loaderMeta + 1], e.Args[loaderMeta + 2],
                ArgValue(e.Args, "--log") ?? "loader-meta.log", ArgValue(e.Args, "--data")));
            return;
        }

        int adopt = Array.IndexOf(e.Args, "--adopt");
        if (adopt >= 0 && adopt + 1 < e.Args.Length)
        {
            RunHeadless(() =>
            {
                int rc = Services.HeadlessRunner.AdoptInstall(
                    e.Args[adopt + 1],
                    ArgValue(e.Args, "--log") ?? "adopt.log",
                    persist: !e.Args.Contains("--dry-run"));
                return Task.FromResult(rc);
            });
            return;
        }

        int repair = Array.IndexOf(e.Args, "--repair");
        if (repair >= 0 && repair + 1 < e.Args.Length)
        {
            RunHeadless(() => Services.HeadlessRunner.RepairAsync(
                e.Args[repair + 1], ArgValue(e.Args, "--log") ?? "repair.log",
                ArgValue(e.Args, "--data"), ArgInt(e.Args, "--threads")));
            return;
        }

        int verify = Array.IndexOf(e.Args, "--verify");
        if (verify >= 0 && verify + 1 < e.Args.Length)
        {
            RunHeadless(() => Services.HeadlessRunner.VerifyVersionAsync(
                e.Args[verify + 1], ArgValue(e.Args, "--log") ?? "verify.log", ArgValue(e.Args, "--data")));
            return;
        }

        if (e.Args.Contains("--device-code"))
        {
            RunHeadless(() => Services.HeadlessRunner.RequestDeviceCodeAsync(
                ArgValue(e.Args, "--log") ?? "device-code.log",
                ArgInt(e.Args, "--wait") ?? 0));
            return;
        }

        int checkMods = Array.IndexOf(e.Args, "--check-mods");
        if (checkMods >= 0 && checkMods + 1 < e.Args.Length)
        {
            RunHeadless(() => Task.FromResult(Services.HeadlessRunner.CheckMods(
                e.Args[checkMods + 1], ArgValue(e.Args, "--log") ?? "mods.log", ArgValue(e.Args, "--data"))));
            return;
        }

        int ensureJava = Array.IndexOf(e.Args, "--ensure-java");
        if (ensureJava >= 0 && ensureJava + 1 < e.Args.Length &&
            int.TryParse(e.Args[ensureJava + 1], out int javaMajor))
        {
            RunHeadless(() => Services.HeadlessRunner.EnsureJavaAsync(
                javaMajor, ArgValue(e.Args, "--log") ?? "java.log", ArgValue(e.Args, "--data")));
            return;
        }

        // Photograph the first-run agreement on its own (main window is skipped).
        string? termsShot = ArgValue(e.Args, "--shot-terms");
        if (!string.IsNullOrEmpty(termsShot))
        {
            ShotTerms(termsShot, ArgValue(e.Args, "--terms-lang"));
            return;
        }

        int loader = Array.IndexOf(e.Args, "--install-loader");
        if (loader >= 0 && loader + 2 < e.Args.Length)
        {
            RunHeadless(() => Services.HeadlessRunner.InstallLoaderAsync(
                e.Args[loader + 1], e.Args[loader + 2], ArgValue(e.Args, "--loader"),
                ArgValue(e.Args, "--log") ?? "loader.log", ArgValue(e.Args, "--data"),
                ArgInt(e.Args, "--threads"), ArgValue(e.Args, "--optifine")));
            return;
        }

        int dryRun = Array.IndexOf(e.Args, "--launch-command");
        if (dryRun >= 0 && dryRun + 1 < e.Args.Length)
        {
            RunHeadless(() => Services.HeadlessRunner.PrintLaunchCommandAsync(
                e.Args[dryRun + 1], ArgValue(e.Args, "--log") ?? "launch.log", ArgValue(e.Args, "--data")));
            return;
        }

        int launch = Array.IndexOf(e.Args, "--launch");
        if (launch >= 0 && launch + 1 < e.Args.Length)
        {
            RunHeadless(() => Services.HeadlessRunner.LaunchAsync(
                e.Args[launch + 1], ArgValue(e.Args, "--log") ?? "game.log",
                ArgValue(e.Args, "--data"), ArgInt(e.Args, "--seconds") ?? 25));
            return;
        }

        int mods = Array.IndexOf(e.Args, "--mods");
        if (mods >= 0 && mods + 1 < e.Args.Length)
        {
            RunHeadless(() => Services.HeadlessRunner.ListModsAsync(
                e.Args[mods + 1], ArgValue(e.Args, "--log") ?? "mods.log", ArgValue(e.Args, "--data")));
            return;
        }

        int skinSet = Array.IndexOf(e.Args, "--skin-set");
        if (skinSet >= 0 && skinSet + 1 < e.Args.Length)
        {
            bool slim = Array.IndexOf(e.Args, "--slim") >= 0;
            RunHeadless(() => Services.HeadlessRunner.SetSkinAsync(
                e.Args[skinSet + 1], ArgValue(e.Args, "--log") ?? "skin.log",
                ArgValue(e.Args, "--data"), slim));
            return;
        }

        int toggle = Array.IndexOf(e.Args, "--mod-toggle");
        if (toggle >= 0 && toggle + 2 < e.Args.Length)
        {
            string? state = toggle + 3 < e.Args.Length ? e.Args[toggle + 3] : null;
            bool enable = state == null || !state.Equals("off", StringComparison.OrdinalIgnoreCase);
            RunHeadless(() => Services.HeadlessRunner.ToggleModAsync(
                e.Args[toggle + 1], e.Args[toggle + 2], enable,
                ArgValue(e.Args, "--log") ?? "mod-toggle.log", ArgValue(e.Args, "--data")));
            return;
        }

        int import = Array.IndexOf(e.Args, "--mod-import");
        if (import >= 0 && import + 2 < e.Args.Length)
        {
            RunHeadless(() => Services.HeadlessRunner.ImportModAsync(
                e.Args[import + 1], e.Args[import + 2],
                ArgValue(e.Args, "--log") ?? "mod-import.log", ArgValue(e.Args, "--data")));
            return;
        }

        int modSearch = Array.IndexOf(e.Args, "--mod-search");
        if (modSearch >= 0 && modSearch + 1 < e.Args.Length)
        {
            RunHeadless(() => Services.HeadlessRunner.ModSearchAsync(
                e.Args[modSearch + 1], ArgValue(e.Args, "--mc") ?? "1.20.1", ArgValue(e.Args, "--loader"),
                ArgValue(e.Args, "--log") ?? "mod-search.log", ArgValue(e.Args, "--data")));
            return;
        }

        int modInstall = Array.IndexOf(e.Args, "--mod-install");
        if (modInstall >= 0 && modInstall + 2 < e.Args.Length)
        {
            RunHeadless(() => Services.HeadlessRunner.ModInstallAsync(
                e.Args[modInstall + 1], e.Args[modInstall + 2],
                ArgValue(e.Args, "--log") ?? "mod-install.log", ArgValue(e.Args, "--data")));
            return;
        }

        int trash = Array.IndexOf(e.Args, "--mod-trash");
        if (trash >= 0 && trash + 2 < e.Args.Length)
        {
            RunHeadless(() => Services.HeadlessRunner.TrashModAsync(
                e.Args[trash + 1], e.Args[trash + 2],
                ArgValue(e.Args, "--log") ?? "mod-trash.log", ArgValue(e.Args, "--data")));
            return;
        }

        int optiMod = Array.IndexOf(e.Args, "--install-optifine-mod");
        if (optiMod >= 0 && optiMod + 1 < e.Args.Length)
        {
            RunHeadless(() => Services.HeadlessRunner.InstallOptiFineAsModAsync(
                e.Args[optiMod + 1], ArgValue(e.Args, "--optifine"), ArgValue(e.Args, "--profile"),
                ArgValue(e.Args, "--log") ?? "optifine-mod.log", ArgValue(e.Args, "--data")));
            return;
        }

        // A launcher must never die silently: surface the failure and keep the UI alive.
        DispatcherUnhandledException += OnDispatcherUnhandledException;
        AppDomain.CurrentDomain.UnhandledException += (_, args) =>
        {
            if (args.ExceptionObject is Exception ex)
                MessageBox.Show(ex.ToString(), "ECL 未处理的错误", MessageBoxButton.OK, MessageBoxImage.Error);
        };
        TaskScheduler.UnobservedTaskException += (_, args) => args.SetObserved();

        // Optional: dump WPF binding failures, which are otherwise invisible in Release.
        string? bindingLog = ArgValue(e.Args, "--binding-log");
        if (!string.IsNullOrWhiteSpace(bindingLog))
            _bindingListener = Services.BindingErrorListener.Attach(bindingLog);

        // First run (or a new agreement version): read and accept before any UI.
        if (!EnsureTermsAccepted())
        {
            Shutdown(0);
            return;
        }

        StartupLog("creating main window");
        var window = new MainWindow(ArgValue(e.Args, "--data"));
        StartupLog("main window constructed");
        MainWindow = window;
        window.Show();
        StartupLog("main window shown");

        // Test hook: blow up on the UI thread a moment after the window appears, so
        // the crash file + eclcr.exe handoff can be verified without waiting for a
        // real bug.
        if (e.Args.Contains("--force-crash"))
        {
            StartupLog("--force-crash: throwing on purpose");
            // Timer.Tick, not InvokeAsync/BeginInvoke: both of those return a
            // DispatcherOperation, which captures the exception into its own Task and
            // never lets it reach DispatcherUnhandledException. Tick is a plain
            // delegate invoked from the message loop, so the throw propagates.
            var crashTimer = new System.Windows.Threading.DispatcherTimer
            {
                Interval = TimeSpan.FromMilliseconds(800)
            };
            crashTimer.Tick += (_, _) =>
            {
                crashTimer.Stop();
                throw new InvalidOperationException("测试用的强制崩溃（--force-crash）");
            };
            crashTimer.Start();
        }
    }

    /// <summary>
    /// Append-only trace of the first-run path. There is no console attached to a
    /// GUI process, and the main window does not exist yet, so a file next to the
    /// exe is the only place start-up problems can surface.
    /// </summary>
    internal static void StartupLog(string message)
    {
        try
        {
            string path = System.IO.Path.Combine(AppContext.BaseDirectory, "startup.log");

            // Two lines per launch forever would reach a megabyte in a couple of years
            // of daily use. Start over once it grows past 256 KB.
            var info = new System.IO.FileInfo(path);
            if (info.Exists && info.Length > 256 * 1024)
                System.IO.File.Delete(path);

            System.IO.File.AppendAllText(
                path, $"[{DateTime.Now:HH:mm:ss.fff}] {message}{Environment.NewLine}");
        }
        catch
        {
            // Diagnostics must never break start-up.
        }
    }

    /// <summary>Current agreement revision; bump it to re-prompt every existing user.</summary>
    private const int TermsVersion = 1;

    /// <summary>
    /// Shows the agreement once. Returns false when the user declines, in which
    /// case the caller shuts down instead of opening a half-usable window.
    /// </summary>
    private static bool EnsureTermsAccepted()
    {
        var settings = StartupSettings;
        if (settings.TermsAccepted && settings.TermsVersion >= TermsVersion)
            return true;

        // Automated captures must not be blocked by a modal dialog; the terms
        // window is photographed separately via --shot-terms.
        if (ScreenshotPath != null && string.IsNullOrEmpty(ArgValue(Environment.GetCommandLineArgs(), "--shot-terms")))
            return true;

        // The agreement is the only window alive at this point. With the default
        // ShutdownMode.OnLastWindowClose, closing it tears down the whole application
        // before MainWindow is ever constructed — the user clicks "agree" and the
        // process simply exits. Hold shutdown until the main window exists.
        StartupLog($"terms prompt: alreadyAccepted={settings.TermsAccepted} version={settings.TermsVersion}");
        StartupLog($"settings path: {Models.LauncherSettings.ResolveSettingsPath()}");

        var previousMode = Current.ShutdownMode;
        Current.ShutdownMode = ShutdownMode.OnExplicitShutdown;

        bool accepted;
        try
        {
            var win = new TermsWindow();

            // Test hook: click "agree" for the screenshots/CI runs so the full
            // dialog-then-main-window path can be exercised without a human.
            if (Environment.GetCommandLineArgs().Contains("--auto-accept-terms"))
                win.Loaded += (_, _) => win.SimulateAccept();

            accepted = win.ShowDialog() == true && win.Accepted;
            StartupLog($"dialog closed: accepted={accepted}");
        }
        finally
        {
            Current.ShutdownMode = previousMode;
        }

        if (accepted)
        {
            settings.TermsAccepted = true;
            settings.TermsVersion = TermsVersion;
            try
            {
                // Must be the same file ResolveSettingsPath reads, otherwise portable
                // installs write the flag to %APPDATA% and re-prompt forever.
                settings.Save(Models.LauncherSettings.ResolveSettingsPath());
                StartupLog($"terms saved to {Models.LauncherSettings.ResolveSettingsPath()}");
            }
            catch
            {
                // Best effort; the prompt simply reappears next launch.
            }
        }

        return accepted;
    }

    /// <summary>
    /// Renders the first-run agreement to a PNG and exits. Separate path so the
    /// modal dialog can be inspected without a human clicking through it.
    /// </summary>
    private void ShotTerms(string path, string? language)
    {
        ShutdownMode = ShutdownMode.OnExplicitShutdown;

        var win = new TermsWindow(language);
        win.Show();

        Dispatcher.InvokeAsync(async () =>
        {
            try
            {
                await Task.Delay(500);
                var dpi = System.Windows.Media.VisualTreeHelper.GetDpi(win);
                var rtb = new System.Windows.Media.Imaging.RenderTargetBitmap(
                    (int)(win.ActualWidth * dpi.DpiScaleX), (int)(win.ActualHeight * dpi.DpiScaleY),
                    dpi.PixelsPerInchX, dpi.PixelsPerInchY,
                    System.Windows.Media.PixelFormats.Pbgra32);
                rtb.Render(win);

                var encoder = new System.Windows.Media.Imaging.PngBitmapEncoder();
                encoder.Frames.Add(System.Windows.Media.Imaging.BitmapFrame.Create(rtb));
                using var fs = System.IO.File.Create(path);
                encoder.Save(fs);
                Shutdown(0);
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine(ex);
                Shutdown(1);
            }
        });
    }

    private void RunHeadless(Func<Task<int>> work)
    {
        ShutdownMode = ShutdownMode.OnExplicitShutdown;
        Dispatcher.InvokeAsync(async () =>
        {
            int code = 1;
            try
            {
                code = await work();
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine(ex);
            }
            Shutdown(code);
        });
    }

    private static string? ArgValue(string[] args, string name)
    {
        int i = Array.IndexOf(args, name);
        return i >= 0 && i + 1 < args.Length ? args[i + 1] : null;
    }

    /// <summary>
    /// Loads the settings file that MainViewModel will use later, so the theme is
    /// known before the first window is parsed. --data only redirects the game
    /// data root; the settings file itself always lives in %APPDATA%\EternityCraft.
    /// </summary>
    private static Models.LauncherSettings LoadStartupSettings(string[] args)
    {
        var settings = Models.LauncherSettings.Load(Models.LauncherSettings.ResolveSettingsPath());

        // Must happen before the first window is parsed: Localization.Apply runs on
        // load and reads the current language.
        Services.Localization.SetLanguage(settings.UiLanguage);
        string? data = ArgValue(args, "--data");
        if (!string.IsNullOrWhiteSpace(data)) settings.DataRoot = data;
        return settings;
    }

    private static int? ArgInt(string[] args, string name)
        => int.TryParse(ArgValue(args, name), out int v) ? v : null;

    private static void OnDispatcherUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        MessageBox.Show(
            "启动器遇到一个错误，但已继续运行。\n\n" + e.Exception.Message + "\n\n" + e.Exception.StackTrace,
            "ECL", MessageBoxButton.OK, MessageBoxImage.Warning);
        e.Handled = true;
    }
}
