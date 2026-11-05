// SPDX-License-Identifier: MPL-2.0
// Copyright (c) 2026 ChenchenMC <https://github.com/ChenchenMC-cn/EternityCraftLauncher>
namespace ECL.Services;

/// <summary>One headed paragraph of the agreement.</summary>
public sealed record TermsBlock(string Heading, string Body);

/// <summary>
/// A complete agreement in one language, including the window's own chrome —
/// every visible string follows the picker, not just the body text.
/// </summary>
public sealed record TermsDocument(
    string Code,
    string Display,
    string Title,
    string Subtitle,
    string LanguageLabel,
    string AcceptHint,
    string AcceptButton,
    string DeclineButton,
    string FallbackNotice,
    IReadOnlyList<TermsBlock> Blocks,
    string Signature);

/// <summary>
/// The first-run agreement. Six languages, hand-written: 简体中文、香港繁體、
/// 台灣繁體、日本語、British and American English. Other Minecraft languages are
/// not offered here on purpose — a legal text translated by a machine is worse
/// than no translation at all.
/// </summary>
public static class TermsText
{
    /// <summary>Bump together with App.TermsVersion to re-prompt everyone.</summary>
    public const int Version = 1;

    public const string FallbackCode = "en_us";

    /// <summary>Lazy on purpose: the documents below are declared after this member.</summary>
    private static readonly Lazy<TermsDocument[]> Lazy =
        new(() => new[] { ZhCn!, ZhHk!, ZhTw!, JaJp!, EnGb!, EnUs! });

    public static IReadOnlyList<TermsDocument> Translated => Lazy.Value;

    private static readonly Lazy<Dictionary<string, TermsDocument>> ByCodeLazy =
        new(() => Translated.ToDictionary(d => d.Code, StringComparer.OrdinalIgnoreCase));

    private static Dictionary<string, TermsDocument> ByCode => ByCodeLazy.Value;

    public static bool HasTranslation(string? code) =>
        !string.IsNullOrWhiteSpace(code) && ByCode.ContainsKey(code);

    /// <summary>Document for a language, falling back to English.</summary>
    public static TermsDocument For(string? code, out bool fellBack)
    {
        if (!string.IsNullOrWhiteSpace(code) && ByCode.TryGetValue(code, out var doc))
        {
            fellBack = false;
            return doc;
        }

        fellBack = true;
        return ByCode[FallbackCode];
    }

    /// <summary>
    /// Maps a Minecraft language code onto one of the six we actually ship.
    /// Anything outside the set lands on English (US).
    /// </summary>
    public static string ResolveCode(string? mcCode)
    {
        if (string.IsNullOrWhiteSpace(mcCode)) return FallbackCode;
        string c = mcCode.Trim().ToLowerInvariant();

        if (ByCode.ContainsKey(c)) return c;
        if (c.StartsWith("zh", StringComparison.Ordinal)) return c.Contains("tw") ? "zh_tw" : "zh_cn";
        if (c.StartsWith("ja", StringComparison.Ordinal)) return "ja_jp";
        return FallbackCode;
    }

    /// <summary>
    /// Language codes that have a json file under <c>&lt;dataRoot&gt;/terms/</c>. These are
    /// offered in the picker alongside the built-in six, otherwise a translation could
    /// be dropped in and never become selectable.
    /// </summary>
    public static IEnumerable<string> ExternalCodes(string dataRoot)
    {
        if (string.IsNullOrWhiteSpace(dataRoot)) yield break;

        string dir = System.IO.Path.Combine(dataRoot, "terms");
        if (!System.IO.Directory.Exists(dir)) yield break;

        foreach (string file in System.IO.Directory.EnumerateFiles(dir, "*.json"))
            yield return System.IO.Path.GetFileNameWithoutExtension(file).ToLowerInvariant();
    }

    /// <summary>
    /// Looks for <c>&lt;dataRoot&gt;/terms/&lt;code&gt;.json</c> so a translation can be
    /// shipped without rebuilding. Missing chrome fields reuse the English ones.
    /// </summary>
    public static TermsDocument? LoadExternal(string dataRoot, string code)
    {
        try
        {
            string path = System.IO.Path.Combine(dataRoot, "terms", code + ".json");
            if (!System.IO.File.Exists(path)) return null;

            using var doc = System.Text.Json.JsonDocument.Parse(System.IO.File.ReadAllText(path));
            var root = doc.RootElement;

            var blocks = new List<TermsBlock>();
            if (root.TryGetProperty("blocks", out var arr) &&
                arr.ValueKind == System.Text.Json.JsonValueKind.Array)
            {
                foreach (var item in arr.EnumerateArray())
                {
                    string h = item.TryGetProperty("heading", out var hv) ? hv.GetString() ?? "" : "";
                    string b = item.TryGetProperty("body", out var bv) ? bv.GetString() ?? "" : "";
                    if (b.Length > 0) blocks.Add(new TermsBlock(h, b));
                }
            }

            if (blocks.Count == 0) return null;

            string Get(string name, string fallback) =>
                root.TryGetProperty(name, out var v) && v.GetString() is { Length: > 0 } s ? s : fallback;

            var en = EnUs!;
            return new TermsDocument(
                code,
                Get("display", code),
                Get("title", en.Title),
                Get("subtitle", en.Subtitle),
                Get("languageLabel", en.LanguageLabel),
                Get("acceptHint", en.AcceptHint),
                Get("acceptButton", en.AcceptButton),
                Get("declineButton", en.DeclineButton),
                Get("fallbackNotice", en.FallbackNotice),
                blocks,
                Get("signature", ""));
        }
        catch
        {
            return null;
        }
    }

    // ------------------------------------------------------------ 简体中文 ---

    private static readonly TermsDocument ZhCn = new(
        "zh_cn", "简体中文", "用户协议与免责声明", "首次使用需要阅读并同意以下条款",
        "语言", "点击「同意并继续」表示你已阅读并接受上述条款（以所选语言版本为准）。",
        "同意并继续", "退出",
        "当前语言没有人工译本，以下为英文版本。",
        new TermsBlock[]
        {
            new("1. 软件性质",
                "ECL（Eternity Craft Launcher）是一个面向全球用户的国际性 Minecraft: Java Edition 第三方启动器。" +
                "本软件不包含、不分发任何 Minecraft 游戏本体文件；版本清单、资源与库文件均从 Mojang、FabricMC、" +
                "MinecraftForge 的官方源获取。"),
            new("2. 与 Mojang / Microsoft 的关系",
                "本软件由个人开发者 ChenchenMC 独立开发，与 Mojang Studios、Microsoft Corporation 及其关联公司" +
                "没有任何隶属、赞助、授权或背书关系。Minecraft 是 Mojang Studios 的商标。" +
                "本软件不提供任何破解、绕过正版验证或盗版功能。"),
            new("3. 账号与隐私",
                "微软账号登录全程在微软官方页面完成，本软件不接触也不保存你的密码。" +
                "登录令牌使用 Windows DPAPI 加密后保存在本机，不会上传到任何服务器。本软件不收集使用数据。"),
            new("4. 使用要求",
                "使用正版登录功能即表示你拥有合法的 Minecraft: Java Edition 授权，并同意遵守 Minecraft 最终用户" +
                "许可协议与使用准则。离线账户功能仅用于单机与你有权访问的服务器。"),
            new("5. 免责声明",
                "本软件按「现状」提供，不对因使用或无法使用本软件造成的任何直接或间接损失承担责任，" +
                "包括但不限于存档损坏、模组冲突、游戏崩溃、账号异常。安装模组与第三方加载器属于你的自主选择。"),
            new("6. 反馈",
                "欢迎反馈问题与建议。联系作者时请附上日志文件，这样定位问题会快很多。")
        },
        "开发者：ChenchenMC（中国）\n595802821@qq.com / 153326010@qq.com");

    // ---------------------------------------------------------- 香港繁體 ---

    private static readonly TermsDocument ZhHk = new(
        "zh_hk", "繁體中文（香港）", "使用者協議與免責聲明", "首次使用需要閱讀並同意以下條款",
        "語言", "按下「同意並繼續」即表示你已閱讀並接受上述條款（以所選語言版本為準）。",
        "同意並繼續", "退出",
        "目前語言沒有手寫譯本，以下為英文版本。",
        new TermsBlock[]
        {
            new("1. 軟件性質",
                "ECL（Eternity Craft Launcher）是一個面向全球用戶的國際性 Minecraft: Java Edition 第三方啟動器。" +
                "本軟件不包含、亦不分發任何 Minecraft 遊戲本體檔案；版本清單、資源與函式庫均從 Mojang、FabricMC、" +
                "MinecraftForge 的官方來源取得。"),
            new("2. 與 Mojang / Microsoft 的關係",
                "本軟件由個人開發者 ChenchenMC 獨立開發，與 Mojang Studios、Microsoft Corporation 及其關聯公司" +
                "並無任何隸屬、贊助、授權或背書關係。Minecraft 是 Mojang Studios 的商標。" +
                "本軟件不提供任何破解、繞過正版驗證或盜版功能。"),
            new("3. 帳戶與私隱",
                "Microsoft 帳戶登入全程在 Microsoft 官方頁面完成，本軟件不會接觸亦不會儲存你的密碼。" +
                "登入權杖以 Windows DPAPI 加密後儲存在本機，不會上傳至任何伺服器。本軟件不收集使用數據。"),
            new("4. 使用要求",
                "使用正版登入功能即表示你持有合法的 Minecraft: Java Edition 授權，並同意遵守 Minecraft 使用者" +
                "授權協議與使用準則。離線帳戶功能僅供單機及你有權存取的伺服器使用。"),
            new("5. 免責聲明",
                "本軟件按「現狀」提供，對因使用或無法使用本軟件而造成的任何直接或間接損失概不負責，" +
                "包括但不限於存檔損壞、模組衝突、遊戲崩潰、帳戶異常。安裝模組與第三方載入器屬你的自主選擇。"),
            new("6. 意見反映",
                "歡迎反映問題與建議。聯絡作者時請附上日誌檔案，這樣定位問題會快很多。")
        },
        "開發者：ChenchenMC（中國）\n595802821@qq.com / 153326010@qq.com");

    // ---------------------------------------------------------- 台灣繁體 ---

    private static readonly TermsDocument ZhTw = new(
        "zh_tw", "繁體中文（台灣）", "使用者協議與免責聲明", "首次使用需要閱讀並同意以下條款",
        "語言", "點選「同意並繼續」表示你已閱讀並接受上述條款（以所選語言版本為準）。",
        "同意並繼續", "退出",
        "目前語言沒有手寫譯本，以下為英文版本。",
        new TermsBlock[]
        {
            new("1. 軟體性質",
                "ECL（Eternity Craft Launcher）是一個面向全球使用者的國際性 Minecraft: Java Edition 第三方啟動器。" +
                "本軟體不包含、也不散布任何 Minecraft 遊戲本體檔案；版本清單、資源與函式庫均取自 Mojang、FabricMC、" +
                "MinecraftForge 的官方來源。"),
            new("2. 與 Mojang / Microsoft 的關係",
                "本軟體由個人開發者 ChenchenMC 獨立開發，與 Mojang Studios、Microsoft Corporation 及其關係企業" +
                "沒有任何隸屬、贊助、授權或背書關係。Minecraft 是 Mojang Studios 的商標。" +
                "本軟體不提供任何破解、繞過正版驗證或盜版功能。"),
            new("3. 帳號與隱私",
                "Microsoft 帳號登入全程在 Microsoft 官方頁面完成，本軟體不會接觸也不會儲存你的密碼。" +
                "登入權杖以 Windows DPAPI 加密後儲存在本機，不會上傳到任何伺服器。本軟體不蒐集使用資料。"),
            new("4. 使用要求",
                "使用正版登入功能即表示你擁有合法的 Minecraft: Java Edition 授權，並同意遵守 Minecraft 使用者" +
                "授權協議與使用準則。離線帳號功能僅供單人遊戲及你有權存取的伺服器使用。"),
            new("5. 免責聲明",
                "本軟體依「現狀」提供，對因使用或無法使用本軟體所造成的任何直接或間接損失概不負責，" +
                "包括但不限於存檔損毀、模組衝突、遊戲崩潰、帳號異常。安裝模組與第三方載入器屬於你的自主選擇。"),
            new("6. 意見回饋",
                "歡迎回報問題與建議。聯絡作者時請附上日誌檔案，這樣定位問題會快很多。")
        },
        "開發者：ChenchenMC（中國）\n595802821@qq.com / 153326010@qq.com");

    // -------------------------------------------------------------- 日本語 ---

    private static readonly TermsDocument JaJp = new(
        "ja_jp", "日本語", "利用規約および免責事項", "初回起動時に以下の内容をお読みいただき、同意してください",
        "言語", "「同意して続行」を押すと、上記の条項を読み、選択した言語版に同意したものとみなされます。",
        "同意して続行", "終了",
        "この言語の翻訳はまだありません。以下は英語版です。",
        new TermsBlock[]
        {
            new("1. 本ソフトウェアについて",
                "ECL（Eternity Craft Launcher）は、世界中のユーザーに向けた Minecraft: Java Edition 用の" +
                "国際的なサードパーティ製ランチャーです。Minecraft のゲーム本体ファイルは一切含まず、配布しません。" +
                "バージョン情報・アセット・ライブラリは Mojang、FabricMC、MinecraftForge の公式ソースから取得します。"),
            new("2. Mojang / Microsoft との関係",
                "本ソフトウェアは個人開発者 ChenchenMC による独立したプロジェクトであり、Mojang Studios、" +
                "Microsoft Corporation およびその関連会社との提携・後援・許諾・推奨関係はありません。" +
                "Minecraft は Mojang Studios の商標です。本ソフトウェアはクラック、認証回避、海賊版機能を" +
                "一切提供しません。"),
            new("3. アカウントとプライバシー",
                "Microsoft アカウントのサインインはすべて Microsoft の公式ページ上で行われ、本ソフトウェアが" +
                "パスワードを取得・保存することはありません。アクセストークンは Windows DPAPI で暗号化して" +
                "本機に保存され、外部に送信されません。利用データの収集も行いません。"),
            new("4. 利用条件",
                "オンラインサインインの利用は、正規の Minecraft: Java Edition ライセンスを保有し、" +
                "Minecraft エンドユーザーライセンス契約および利用ガイドラインに同意していることを意味します。" +
                "オフラインアカウントはシングルプレイおよびアクセス権のあるサーバーでのみ使用してください。"),
            new("5. 免責事項",
                "本ソフトウェアは「現状のまま」提供され、いかなる保証も行いません。本ソフトウェアの使用または" +
                "使用不能に起因する直接的・間接的な損害について、作者は一切の責任を負いません。" +
                "これにはワールドデータの破損、Mod の競合、起動クラッシュ、アカウントの異常などが含まれますが" +
                "これらに限りません。Mod およびサードパーティ製ローダーの導入は自己責任で行ってください。"),
            new("6. フィードバック",
                "不具合の報告や提案を歓迎します。連絡の際はログファイルを添付してください。原因の特定が" +
                "大幅に早くなります。")
        },
        "開発者：ChenchenMC（中国）\n595802821@qq.com / 153326010@qq.com");

    // ------------------------------------------------------- British English ---

    private static readonly TermsDocument EnGb = new(
        "en_gb", "English (UK)", "Terms of Use and Disclaimer",
        "Please read and accept the following before using this software for the first time",
        "Language", "Selecting “Agree and continue” means you have read and accept these terms in the language shown.",
        "Agree and continue", "Quit",
        "No hand-written translation exists for this language yet — the English version is shown below.",
        new TermsBlock[]
        {
            new("1. What this software is",
                "ECL (Eternity Craft Launcher) is an international third-party launcher for Minecraft: Java Edition, " +
                "built for users worldwide. It contains and distributes no Minecraft game files. Version manifests, " +
                "assets and libraries are fetched from the official Mojang, FabricMC and MinecraftForge sources."),
            new("2. Relationship with Mojang / Microsoft",
                "This software is an independent project by ChenchenMC and is not affiliated with, sponsored, " +
                "authorised or endorsed by Mojang Studios, Microsoft Corporation or their affiliates. Minecraft is a " +
                "trademark of Mojang Studios. This software provides no cracking, authentication bypass or piracy " +
                "features of any kind."),
            new("3. Account and privacy",
                "Microsoft sign-in takes place entirely on Microsoft's own pages; this software never sees or stores " +
                "your password. Access tokens are sealed with Windows DPAPI on this machine and are never uploaded. " +
                "No usage data is collected."),
            new("4. Requirements for use",
                "Using the online sign-in means you own a valid Minecraft: Java Edition licence and agree to the " +
                "Minecraft End User Licence Agreement and Usage Guidelines. Offline accounts are intended for single " +
                "player and for servers you are permitted to access."),
            new("5. Disclaimer",
                "This software is provided “as is”, without warranty of any kind. The author accepts no liability for " +
                "any direct or indirect loss arising from its use or inability to use it, including but not limited to " +
                "corrupted worlds, mod conflicts, crashes or account issues. Installing mods and third-party loaders " +
                "is your own decision."),
            new("6. Feedback",
                "Bug reports and suggestions are welcome. Please attach the log file when contacting the author — it " +
                "makes diagnosis much faster.")
        },
        "Developer: ChenchenMC (China)\n595802821@qq.com / 153326010@qq.com");

    // ---------------------------------------------------- American English ---

    private static readonly TermsDocument EnUs = new(
        "en_us", "English (US)", "Terms of Use and Disclaimer",
        "Please read and accept the following before using this software for the first time",
        "Language", "Selecting “Agree and continue” means you have read and accept these terms in the language shown.",
        "Agree and continue", "Quit",
        "No hand-written translation exists for this language yet — the English version is shown below.",
        new TermsBlock[]
        {
            new("1. What this software is",
                "ECL (Eternity Craft Launcher) is an international third-party launcher for Minecraft: Java Edition, " +
                "built for users worldwide. It contains and distributes no Minecraft game files. Version manifests, " +
                "assets and libraries are fetched from the official Mojang, FabricMC and MinecraftForge sources."),
            new("2. Relationship with Mojang / Microsoft",
                "This software is an independent project by ChenchenMC and is not affiliated with, sponsored, " +
                "authorized or endorsed by Mojang Studios, Microsoft Corporation or their affiliates. Minecraft is a " +
                "trademark of Mojang Studios. This software provides no cracking, authentication bypass or piracy " +
                "features of any kind."),
            new("3. Account and privacy",
                "Microsoft sign-in happens entirely on Microsoft's own pages; this software never sees or stores your " +
                "password. Access tokens are sealed with Windows DPAPI on this machine and are never uploaded. " +
                "No usage data is collected."),
            new("4. Requirements for use",
                "Using the online sign-in means you own a valid Minecraft: Java Edition license and agree to the " +
                "Minecraft End User License Agreement and Usage Guidelines. Offline accounts are intended for single " +
                "player and for servers you are permitted to access."),
            new("5. Disclaimer",
                "This software is provided “as is”, without warranty of any kind. The author accepts no liability for " +
                "any direct or indirect loss arising from its use or inability to use it, including but not limited to " +
                "corrupted worlds, mod conflicts, crashes or account issues. Installing mods and third-party loaders " +
                "is your own decision."),
            new("6. Feedback",
                "Bug reports and suggestions are welcome. Please attach the log file when contacting the author — it " +
                "makes diagnosis much faster.")
        },
        "Developer: ChenchenMC (China)\n595802821@qq.com / 153326010@qq.com");
}
