// SPDX-License-Identifier: MPL-2.0
// Copyright (c) 2026 ChenchenMC <https://github.com/ChenchenMC-cn/EternityCraftLauncher>
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Media;

namespace ECL.Services;

/// <summary>
/// Two-language UI layer (Chinese / English) without touching 148 XAML attributes.
///
/// <para>The main window is written in Chinese literals. Converting every one of them
/// to a binding would be a huge, risky diff and would break the moment someone adds a
/// label. Instead this walks the visual tree once, after the window loads, and swaps
/// any string that (a) is in the dictionary and (b) is <b>not data-bound</b>. Bound
/// text is left alone, so dynamic content is never clobbered.</para>
///
/// <para>Anything missing from the dictionary simply stays in Chinese — a partial
/// translation degrades instead of producing blank labels.</para>
/// </summary>
public static class Localization
{
    /// <summary>"auto" follows the OS UI language; otherwise "zh" or "en".</summary>
    public static string Language { get; private set; } = "auto";

    /// <summary>How many strings are translated. Used by the self-test.</summary>
    public static int EntryCount => En.Count;

    public static bool IsEnglish => Language switch
    {
        "en" => true,
        "zh" => false,
        // "auto": English only when the OS is clearly not Chinese.
        _ => !CultureInfo.CurrentUICulture.TwoLetterISOLanguageName
                 .Equals("zh", StringComparison.OrdinalIgnoreCase)
    };

    /// <summary>Applies a language choice. "auto" re-resolves from the OS.</summary>
    public static void SetLanguage(string? value)
    {
        Language = string.IsNullOrWhiteSpace(value) ? "auto" : value.Trim().ToLowerInvariant();
    }

    /// <summary>
    /// Looks a Chinese string up. Unknown strings pass through unchanged, which is what
    /// keeps a half-finished translation usable.
    /// </summary>
    public static string T(string zh)
    {
        if (!IsEnglish || string.IsNullOrEmpty(zh)) return zh;
        return En.TryGetValue(zh, out var en) ? en : zh;
    }

    /// <summary>
    /// Rewrites every static (non-bound) string in the tree. Call once after the window
    /// is loaded; re-call after rebuilding a page from scratch.
    /// </summary>
    public static int Apply(DependencyObject? root)
    {
        if (root == null || !IsEnglish) return 0;

        int replaced = 0;
        int sawBound = 0;

        foreach (var node in Descendants(root))
        {
            try
            {
                switch (node)
                {
                    case TextBlock tb when !string.IsNullOrEmpty(tb.Text):
                        if (BindingOperations.GetBindingExpression(tb, TextBlock.TextProperty) != null)
                        {
                            sawBound++;
                            break;
                        }

                        string newText = T(tb.Text);
                        if (!ReferenceEquals(newText, tb.Text) && newText != tb.Text)
                        {
                            tb.Text = newText;
                            replaced++;
                        }
                        break;

                    case ContentControl cc when cc.Content is string content && content.Length > 0:
                        if (BindingOperations.GetBindingExpression(cc, ContentControl.ContentProperty) != null)
                        {
                            sawBound++;
                            break;
                        }

                        string newContent = T(content);
                        if (newContent != content)
                        {
                            cc.Content = newContent;
                            replaced++;
                        }
                        break;
                }

                if (node is FrameworkElement fe && fe.ToolTip is string tip && tip.Length > 0)
                    fe.ToolTip = T(tip);
            }
            catch
            {
                // One odd control must not abort the whole pass.
            }
        }

        LastReplaced = replaced;
        LastBound = sawBound;
        return replaced;
    }

    /// <summary>How many strings the last <see cref="Apply"/> pass rewrote.</summary>
    public static int LastReplaced { get; private set; }

    /// <summary>How many text nodes it skipped because they are data-bound.</summary>
    public static int LastBound { get; private set; }

    /// <summary>
    /// Phrases that appear inside runtime log lines.
    ///
    /// <para>Log messages are built with interpolation at ~75 call sites, so a
    /// dictionary keyed by whole sentences cannot reach them. Translating the stable
    /// fragments here (after interpolation, at the single Log() choke point) covers
    /// every variant without touching any caller.</para>
    ///
    /// <para>Order matters: longer, more specific fragments first, otherwise a short
    /// one eats part of a longer match.</para>
    /// </summary>
    private static readonly (string Zh, string En)[] LogPhrases =
    {
        ("数据目录：", "Data root: "),
        ("账户：", "Accounts: "),
        ("个，当前 ", " total, current "),
        ("Java 运行时：", "Java runtimes: "),
        ("皮肤服务已启动：", "Skin server started: "),
        ("皮肤功能初始化失败（不影响启动）：", "Skin init failed (launch unaffected): "),
        ("操作失败：", "Operation failed: "),
        ("保存设置失败：", "Failed to save settings: "),
        ("切换账户：", "Switched account: "),
        ("新增离线账户：", "Added offline account: "),
        ("已打开微软官方登录页（Sign in to Minecraft）。", "Opened the official Microsoft sign-in page."),
        ("启动命令：", "Launch command: "),
        ("游戏退出码 ", "Game exit code "),
        ("已启用自定义皮肤：", "Custom skin enabled: "),
        ("已清除 ", "Cleared JVM args for "),
        (" 的专属 JVM 参数", ""),
        ("未检测到 Java，请安装 JDK/JRE", "No Java found — please install a JDK/JRE"),
        ("不可用", "unavailable"),
    };

    /// <summary>Rewrites the Chinese fragments of one log line. Unknown text passes through.</summary>
    public static string TranslateLog(string message)
    {
        if (!IsEnglish || string.IsNullOrEmpty(message)) return message;

        foreach (var (zh, en) in LogPhrases)
            message = message.Replace(zh, en, StringComparison.Ordinal);

        return message;
    }

    private static IEnumerable<DependencyObject> Descendants(DependencyObject root)
    {
        var stack = new Stack<DependencyObject>();
        stack.Push(root);

        while (stack.Count > 0)
        {
            var current = stack.Pop();
            yield return current;

            // VisualTreeHelper throws on DependencyObjects that are not Visual/Visual3D
            // (runs, brushes, document elements). An unhandled throw inside an iterator
            // aborts the WHOLE enumeration, which is why only the first page used to get
            // translated — everything after the offending node was silently skipped.
            int count;
            try
            {
                count = VisualTreeHelper.GetChildrenCount(current);
            }
            catch
            {
                continue;
            }

            for (int i = 0; i < count; i++)
            {
                DependencyObject child;
                try
                {
                    child = VisualTreeHelper.GetChild(current, i);
                }
                catch
                {
                    continue;
                }

                stack.Push(child);
            }
        }
    }

    // --- dictionary ---------------------------------------------------------
    // Keyed by the exact Chinese literal used in XAML / code. Keep the keys byte-for-byte
    // identical to the source, including punctuation and full-width spaces.
    private static readonly Dictionary<string, string> En = new(StringComparer.Ordinal)
    {
        // navigation / pages
        ["主页"] = "Home",
        ["安装"] = "Install",
        ["模组"] = "Mods",
        ["账户"] = "Accounts",
        ["设置"] = "Settings",
        ["日志"] = "Logs",
        ["游戏"] = "Play",
        ["存储"] = "Storage",
        ["外观"] = "Appearance",
        ["关于"] = "About",

        // common buttons
        ["下载"] = "Download",
        ["取消"] = "Cancel",
        ["关闭"] = "Close",
        ["删除"] = "Delete",
        ["移除"] = "Remove",
        ["刷新"] = "Refresh",
        ["搜索"] = "Search",
        ["添加"] = "Add",
        ["打开"] = "Open",
        ["打开目录"] = "Open folder",
        ["浏览"] = "Browse",
        ["保存设置"] = "Save settings",
        ["清空已完成"] = "Clear finished",
        ["重新检测"] = "Re-detect",
        ["校验文件"] = "Verify files",
        ["补全文件"] = "Repair files",
        ["导入模组"] = "Import mod",
        ["安装新版本"] = "Install new version",
        ["安装版本"] = "Install version",
        ["完成登录"] = "Finish sign-in",
        ["查看任务"] = "View task",
        ["最大化"] = "Maximize",
        ["最小化"] = "Minimize",
        ["去安装"] = "Go to install",
        ["刷新版本列表"] = "Refresh versions",
        ["申请 Minecraft 权限"] = "Request Minecraft API access",
        ["打开 Azure 应用注册"] = "Open Azure app registrations",
        ["打开微软登录页"] = "Open Microsoft sign-in",
        ["添加离线账户"] = "Add offline account",
        ["添加微软账户（正版登录）"] = "Add Microsoft account",
        ["管理账户"] = "Manage accounts",
        ["使用"] = "Use",
        ["使用中"] = "In use",
        ["稍后再说"] = "Later",

        // home / install
        ["准备开始"] = "Ready to start",
        ["选择一个版本，然后开始游戏"] = "Pick a version, then start the game",
        ["已安装的版本"] = "Installed versions",
        ["还没有安装任何版本"] = "No version installed yet",
        ["去安装页选择 Minecraft 版本，或加上 Fabric / Forge"] =
            "Go to Install and pick a Minecraft version, with Fabric / Forge if you want",
        ["选版本"] = "Choose version",
        ["原版"] = "Vanilla",
        ["加载器"] = "Loader",
        ["先选本体，再选加载器"] = "Pick the game version first, then a loader",
        ["Minecraft 版本"] = "Minecraft version",
        ["Fabric 分支"] = "Fabric branch",
        ["Fabric 模组可跑在 Quilt 上；Forge 与 Fabric 模组互不通用。"] =
            "Fabric mods also run on Quilt. Forge and Fabric mods are not interchangeable.",
        ["同时装上 OptiFine"] = "Install OptiFine too",
        ["显示快照版本"] = "Show snapshots",
        ["显示测试版 / 内测版"] = "Show betas / alphas",

        // mods
        ["已安装"] = "Installed",
        ["这个版本还没有模组"] = "This version has no mods yet",
        ["点右上角「导入模组」，或把 .jar 直接复制进上面那个目录"] =
            "Use \"Import mod\" in the top right, or copy the .jar into the folder above",
        ["模组"] = "Mods",
        ["光影 · 优化"] = "Shaders · Performance",
        ["生态最大"] = "Largest ecosystem",
        ["正在搜索…"] = "Searching…",
        ["搜索模组，例如 sodium / lithium / iris"] = "Search mods, e.g. sodium / lithium / iris",
        ["次下载"] = "downloads",
        ["作者"] = "Author",
        ["作者　　"] = "Author    ",
        ["需要"] = "Requires",
        ["下载"] = "Download",
        ["没有找到适配版本？换个版本或加载器，或者到项目主页手动下载。"] =
            "No compatible build? Try another version or loader, or download it manually.",
        ["每个版本有独立的 mods 目录；启用与禁用只改文件名，移除会先放进 .ecl-trash"] =
            "Each version has its own mods folder. Disable only renames the file; remove moves it to .ecl-trash.",

        // accounts
        ["微软账户可以进入正版服务器；离线账户只用于单机与离线服"] =
            "A Microsoft account can join online servers; offline accounts are for single-player and offline servers.",
        ["输入一个名字即可，UUID 由服务端一致的离线规则生成。"] =
            "Just pick a name. The UUID is derived with the same offline rule servers use.",
        ["已添加的账户"] = "Added accounts",
        ["密码只在微软官方页面输入，令牌用 Windows DPAPI 加密后保存。推荐先在下方填好自己的 Azure 应用 ID —— 那样登录完成后浏览器会自动回到启动器。"] =
            "Passwords are only ever entered on Microsoft's own page; tokens are stored encrypted with Windows DPAPI. Filling in your own Azure app ID below lets the browser return to the launcher automatically.",
        ["填好之后点登录，浏览器登录完会自动跳回启动器，不用复制任何网址。只需注册一次，约 5 分钟："] =
            "After filling it in, sign in once. The browser returns here by itself — no URL copying. You only register the app once (~5 minutes):",
        ["登录完成后浏览器会跳到 oauth20_desktop.srf 的空白页。把地址栏那一整串网址（以 code= 开头的那段）完整复制到下面："] =
            "After signing in the browser lands on a blank oauth20_desktop.srf page. Copy the whole address bar (the part starting with code=) into the box below:",
        ["失败时下方会显示微软返回的原始错误（含 correlation_id），把它发出来就能定位。"] =
            "On failure the raw Microsoft error (including correlation_id) appears below — send that and it can be diagnosed.",
        ["清空输入框即恢复内置官方 ID。改完点下方「保存设置」生效。"] =
            "Clear the box to fall back to the built-in official ID. Click \"Save settings\" to apply.",
        ["Azure 应用 ID（推荐填写 · 免费 · 一次）"] = "Azure app ID (recommended, free, one-time)",

        // Azure walkthrough
        ["A. 注册应用（约 3 分钟）"] = "A. Register the app (~3 min)",
        ["B. 申请 Minecraft API 权限（每周审一次，可能要等几天）"] =
            "B. Request Minecraft API access (reviewed weekly, may take days)",
        ["C. 把「应用程序(客户端) ID」粘到下面输入框，点保存"] =
            "C. Paste the Application (client) ID below and save",
        ["0. 个人账户必须先有租户，否则会报「Microsoft Services 租户」或 401"] =
            "0. A personal account needs a tenant first, otherwise you get a \"Microsoft Services tenant\" error or 401",
        ["1. Microsoft Entra ID → 应用注册 → 新注册"] = "1. Microsoft Entra ID → App registrations → New registration",
        ["2. 名称随便填（不能含 Minecraft / Mojang / Microsoft / Xbox）"] =
            "2. Any name (must not contain Minecraft / Mojang / Microsoft / Xbox)",
        ["3. 账户类型选「仅个人 Microsoft 账户」，重定向 URI 留空 → 注册"] =
            "3. Account type: personal Microsoft accounts only. Leave redirect URI empty, then register",
        ["4. 身份验证 → 添加平台 → 移动和桌面应用程序 → 勾 http://localhost"] =
            "4. Authentication → Add platform → Mobile and desktop apps → check http://localhost",
        ["5. 同页底部 →「允许公共客户端流」设为「是」→ 保存"] =
            "5. Same page, bottom: set \"Allow public client flows\" to Yes, then save",
        ["6. 点下面「申请 Minecraft 权限」，类型选 New AppID for Approval"] =
            "6. Click \"Request Minecraft API access\" below and choose New AppID for Approval",
        ["7. 需要填「应用程序(客户端) ID」+「目录(租户) ID」+ 一个网站或仓库地址"] =
            "7. You'll need the Application (client) ID, the Directory (tenant) ID, and a site or repo URL",
        ["注意：白名单没批下来之前，最后一步会返回 403（不是配置错了）。"] =
            "Note: until the whitelist is approved the last step returns 403 — that is not a misconfiguration.",
        ["有卡 → azure.microsoft.com/free（验 1 美元，注册不收费）"] =
            "Have a card → azure.microsoft.com/free (1 USD hold, free registration)",
        ["有学校邮箱 → azure.microsoft.com/free/students（免卡）"] =
            "Have a school email → azure.microsoft.com/free/students (no card)",
        ["有租户后右上角头像 → 切换目录 → 再往下做"] =
            "With a tenant: avatar top-right → Switch directory, then continue",
        ["都没有 → 先用内置 id + 粘贴回调，或 developer.microsoft.com/microsoft-365/dev-program（会过期）"] =
            "Neither → use the built-in id with pasted callback, or developer.microsoft.com/microsoft-365/dev-program (expires)",

        // settings
        ["游戏数据目录"] = "Game data folder",
        ["即 .minecraft 等效目录：versions / libraries / assets / mods 都在它下面"] =
            "The .minecraft equivalent: versions / libraries / assets / mods all live under it",
        ["修改后点「保存设置」；已有安装可把整个目录直接指到旧 .minecraft"] =
            "Click \"Save settings\" after changing. An existing install can point straight at the old .minecraft",
        ["版本隔离"] = "Per-version isolation",
        ["每个版本独立的 saves / mods / options.txt，改完保存后重新选择版本生效"] =
            "Each version gets its own saves / mods / options.txt. Re-select the version after saving",
        ["内存分配"] = "Memory",
        ["最大堆内存"] = "Max heap",
        ["建议 4096 MB。分配过多反而会拖慢启动。"] =
            "4096 MB is a good default. More than that can actually slow start-up.",
        ["并行线程"] = "Parallel downloads",
        ["镜像加速"] = "Mirror",
        ["通过 BMCLAPI 镜像下载游戏库与资源文件"] =
            "Download libraries and assets through the BMCLAPI mirror",
        ["自动下载 Java"] = "Auto-download Java",
        ["缺对应大版本时自动下载 Eclipse Temurin JRE（走清华镜像）"] =
            "Fetch Eclipse Temurin JRE when the required major is missing (via the TUNA mirror)",
        ["Java 运行时"] = "Java runtime",
        ["启动包装器"] = "Launch wrapper",
        ["用 Java 组件包裹启动，崩溃时给出可读提示"] =
            "Wrap the launch in a Java helper so crashes come back readable",
        ["游戏内语言"] = "Game language",
        ["启动时通过 --lang 传给游戏；「自动」跟随 Windows 显示语言"] =
            "Passed to the game as --lang. \"Auto\" follows the Windows display language",
        ["JVM 参数"] = "JVM arguments",
        ["只对当前选中的版本生效；留空则继承设置页里的全局参数"] =
            "Applies to the selected version only; empty means inherit the global arguments",
        ["主题风格"] = "Theme",
        ["深色 · 默认"] = "Dark · default",
        ["浅色 · 卡片"] = "Light · cards",
        ["亚克力 · 半透明"] = "Acrylic · translucent",
        ["强调色"] = "Accent colour",
        ["换主题后点「保存设置」，启动器会自动重启一次。"] =
            "After changing the theme, click \"Save settings\" — the launcher restarts once.",
        ["毛玻璃需要 Windows 10 1803 或更新；窗口圆角在 Windows 11 上生效"] =
            "Blur needs Windows 10 1803 or newer; rounded corners only on Windows 11",
        ["更改会在点击「保存设置」后写入磁盘"] = "Changes are written to disk when you click \"Save settings\"",
        ["原版风"] = "Vanilla",
        ["Windows 风"] = "Windows",
        ["毛玻璃"] = "Blur",
        ["轻量 · 快"] = "Lightweight · fast",

        // logs / reports
        ["启动器与游戏进程的完整输出"] = "Full output from the launcher and the game process",
        ["运行日志"] = "Run log",
        ["崩溃报告"] = "Crash report",
        ["非致命错误"] = "Non-fatal errors",
        ["启动器自己崩溃时留下的记录"] = "Records left when the launcher itself crashed",
        ["下载失败、模组读不出来之类的问题"] = "Download failures, unreadable mods, and similar",

        // misc
        ["正在读取版本列表…"] = "Loading versions…",
        ["游戏启动时会因为缺这些模组而崩溃。现在补上，还是稍后自己处理？"] =
            "The game will crash without these mods. Add them now, or handle it later?",
        ["按版本 json 逐个核对 libraries / assets 的大小和 SHA-1"] =
            "Check every library and asset against the version json (size and SHA-1)",
        ["重新核对并下载缺失或损坏的文件，已正常的会跳过"] =
            "Re-check and download missing or corrupt files; healthy ones are skipped",
        ["作为 Forge 模组放进 mods 目录，不额外生成版本"] =
            "Dropped into mods as a Forge mod — no extra profile is created",
        ["协议　　用户协议 v1 · 首次启动需同意"] = "Agreement    Terms v1 · must accept on first run",
        ["性质　　国际性 Minecraft Java 版第三方启动器，不附带任何游戏本体文件"] =
            "An international third-party Minecraft Java launcher. Ships no game files.",
        ["与 Mojang Studios、Microsoft Corporation 无隶属或授权关系。Minecraft 是 Mojang Studios 的商标。"] =
            "Not affiliated with or endorsed by Mojang Studios or Microsoft Corporation. Minecraft is a trademark of Mojang Studios.",
        ["界面语言：{0}，替换 {1} 处，跳过绑定 {2} 处"] = "UI language: {0}, replaced {1} texts, skipped {2} bound",
        ["正式版"] = "Release",
        ["快照"] = "Snapshot",
        ["官方原版，不带任何加载器。"] = "Official vanilla — no loader.",
        ["安装原版"] = "Install vanilla",
        ["重新安装原版"] = "Reinstall vanilla",
        ["安装 Forge"] = "Install Forge",
        ["安装 Forge + OptiFine"] = "Install Forge + OptiFine",
        ["安装 OptiFine"] = "Install OptiFine",
        ["从 Modrinth 安装"] = "Install from Modrinth",
        ["收起市场"] = "Hide market",
        ["Fabric：轻量、启动快，模组生态最大。"] = "Fabric: light, fast, largest mod ecosystem.",
        ["Quilt：Fabric 的分支，兼容多数 Fabric 模组。"] = "Quilt: a Fabric fork, runs most Fabric mods.",
        ["Forge：需要运行官方安装器打补丁，耗时最长，模组最多。"] = "Forge: runs the official patcher — slowest, most mods.",
        ["OptiFine：光影与性能优化，作为独立版本安装（不挂 Forge）。"] = "OptiFine: shaders and performance, installed as its own profile.",
        ["检测到 {0} 个 Java 运行时"] = "{0} Java runtime(s) detected",
        ["未检测到 Java，请安装 JDK/JRE"] = "No Java found — please install a JDK/JRE",
        ["还没有可启动的版本"] = "No version ready to launch",
        ["选择一个版本"] = "Pick a version",
        ["微软账户"] = "Microsoft account",
        ["离线账户"] = "Offline account",
        ["开始游戏"] = "Play",
        ["先选择一个版本"] = "Pick a version first",
        ["未检测到 Java 运行时"] = "No Java runtime found",
        ["界面语言"] = "Interface language",
        ["切换后需要重启启动器"] = "Takes effect after a restart",
        ["自动"] = "Auto",
        ["跟随系统"] = "Follow system",
        ["中文"] = "Chinese",
        ["简体"] = "Simplified",
        ["（中国）"] = " (China)",
        ["被 "] = "by ",

        // --- existing install discovery ------------------------------------
        ["当前查找目录：{0}"] = "Looking in: {0}",
        ["发现已有 Minecraft 目录：{0}"] = "Found an existing Minecraft folder: {0}",
        ["{0} 个版本，其中 {1} 个可启动"] = "{0} version(s), {1} launchable",
        ["使用这个目录"] = "Use this folder",
        ["使用已有 Minecraft 目录"] = "Use an existing Minecraft folder",
        ["把数据目录切换到 {0} 吗？"] = "Switch the data folder to {0}?",
        ["里面有 {0} 个版本，{1} 个可以直接启动。"] = "It holds {0} version(s), {1} ready to launch.",
        ["切换后会关闭「版本隔离」，原目录的 mods、saves、options.txt 继续沿用。"] =
            "Version isolation will be turned off so that mods, saves and options.txt\nin that folder keep working as they are.",
        ["已切换到 {0}"] = "Switched to {0}",
        ["选择目录…"] = "Choose folder...",
        ["选择已有的 .minecraft 目录"] = "Choose an existing .minecraft folder",
        ["这个目录里没有找到版本（应有 versions/版本/版本.json）"] =
            "No versions in that folder (expected versions/<id>/<id>.json)",
        ["   有卡 → azure.microsoft.com/free（验 1 美元，注册不收费）"] = "   Has a card -> azure.microsoft.com/free ($1 auth hold, not charged)",
        ["   有学校邮箱 → azure.microsoft.com/free/students（免卡）"] = "   School email -> azure.microsoft.com/free/students (no card)",
        ["   有租户后右上角头像 → 切换目录 → 再往下做"] =
            "   Once you have a tenant: avatar (top right) -> Switch directory -> continue below",
        ["   都没有 → 先用内置 id + 粘贴回调，或 developer.microsoft.com/microsoft-365/dev-program（会过期）"] =
            "   Neither -> use your own id with the paste-back flow, or developer.microsoft.com/microsoft-365/dev-program (expires)",
        ["  作者 "] = "  Author ",
        [" 下载"] = " downloads",
        [" 次下载"] = " downloads",
        [" 需要"] = " requires",
        ["LittleSkin 用户名（不需要密码）"] = "LittleSkin username (no password)",
        ["Modrinth 模组市场"] = "Modrinth",
        ["① 在浏览器按 Ctrl+L ② 按 Ctrl+C ③ 切回启动器（会自动填好并登录）"] =
            "(1) In the browser press Ctrl+L  (2) press Ctrl+C  (3) come back here - it fills in and signs you in",
        ["不用粘贴、不用点按钮；识别不到再手动贴到下面。"] = "No copy-paste and no button to press. If it misses the URL, paste it below.",
        ["从国内镜像取文件，比官方源快很多"] = "Fetches files from a China mirror - much faster than the official source",
        ["去填 client_id"] = "Enter client_id",
        ["同时下载"] = "Concurrent downloads",
        ["启动参数（高级）"] = "Launch arguments (advanced)",
        ["安全模式下载"] = "Safe-mode downloads",
        ["密码只在微软官方页面输入，令牌用 Windows DPAPI 加密后保存。需要先在「设置 → 微软账户」填好自己的 Azure 应用 ID —— 那样登录完成后浏览器会自动回到启动器。"] =
            "Your password is only typed on Microsoft's own page; tokens are stored encrypted with Windows DPAPI.\nSet your own Azure application ID in Settings -> Microsoft account first, so the browser can hand control back automatically.",
        ["导入 PNG"] = "Import PNG",
        ["导入披风"] = "Import cape",
        ["导出诊断包"] = "Export diagnostics",
        ["就是 .minecraft 的等价目录，版本、库、资源、模组都放里面"] =
            "The .minecraft-equivalent folder - versions, libraries, assets and mods all live here",
        ["崩溃时把英文报错翻成中文，并告诉你怎么修"] = "Turns a crash stack trace into a plain explanation plus a fix",
        ["披风"] = "Cape",
        ["指定 java.exe"] = "Pick java.exe",
        ["换一种下载方式。慢一些；下载时老是崩启动器就打开它"] =
            "Use the other download path. Slower, but turn it on if downloading keeps crashing the launcher",
        ["换主题或改不透明度后点「保存设置」，启动器会自动重启一次。"] =
            "After changing the theme or opacity, click Save settings - the launcher restarts once.",
        ["日志 + 崩溃报告 + 配置打成一个 zip（自动脱敏），报 bug 时直接发这个"] =
            "Logs, crash reports and settings in one zip (redacted) - attach it when you report a bug",
        ["正版账号可直接拉取；也可用 LittleSkin 或本地 PNG。进入游戏时由本机皮肤服务注入，不会上传到任何地方。"] =
            "Taken from your Microsoft account, from LittleSkin, or from a local PNG.\nInjected by a local skin server - nothing is uploaded anywhere.",
        ["每个版本各用各的存档和模组，装几个版本也不会互相串；改完保存，重选版本生效"] =
            "Each version keeps its own saves and mods, so several installs never mix.\nSave, then reselect the version to apply.",
        ["毛玻璃不透明度"] = "Acrylic opacity",
        ["浏览器里登录完之后，页面会变成一片空白 —— 那串网址就是结果。三步："] = "After signing in the page turns blank - that URL is the result. Three steps:",
        ["清除"] = "Clear",
        ["用 LittleSkin 皮肤"] = "Use a LittleSkin skin",
        ["用正版皮肤"] = "Use my account's skin",
        ["留空 = 不能用正版登录（不再内置任何官方 ID）。改完点下方「保存设置」生效。"] =
            "Empty = Microsoft sign-in unavailable (no bundled official ID).\nClick Save settings below to apply.",
        ["皮肤与披风"] = "Skin & cape",
        ["缺 Java 时自动下载（国内镜像，约 50 MB）"] = "Fetch Java automatically when missing (China mirror, ~50 MB)",
        ["诊断包"] = "Diagnostics",
        ["越往右越实；100% 就是纯色。字看不太清就往右拉。"] =
            "Further right is more opaque; 100% is solid. Drag right if text is hard to read.",
        ["逐个核对游戏文件对不对（按官方记录比大小和指纹）"] = "Checks every game file against the official size and hash",
        ["邮箱　　"] = "Email　　",
        ["官方原版"] = "Vanilla (official)",
        ["只对这个版本生效，追加在全局参数之后"] =
            "Applies to this version only, appended after the global arguments",
        ["留空 = 不使用额外 JVM 参数"] = "Empty = no extra JVM arguments",
        ["留空 = 继承全局：{0}"] = "Empty = inherit the global ones: {0}",
    };
}
