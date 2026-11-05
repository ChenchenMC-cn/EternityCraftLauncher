// SPDX-License-Identifier: MPL-2.0
// Copyright (c) 2026 ChenchenMC <https://github.com/ChenchenMC-cn/EternityCraftLauncher>
using System.IO;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;
using ECL.Services;

namespace ECL.Models;

public sealed class LauncherSettings
{
    [JsonPropertyName("dataRoot")] public string DataRoot { get; set; } = "";
    [JsonPropertyName("javaPath")] public string? JavaPath { get; set; }
    [JsonPropertyName("memoryMb")] public int MemoryMb { get; set; } = 4096;
    [JsonPropertyName("downloadThreads")] public int DownloadThreads { get; set; } = 16;
    [JsonPropertyName("useMirror")] public bool UseMirror { get; set; }

    /// <summary>
    /// Download through the managed HttpClient path instead of ECL.Native.dll.
    /// Exists because "下载就崩" could not be pinned down otherwise: if the crash stops
    /// here, the fault is in the native batch downloader, not in the launcher.
    /// </summary>
    [JsonPropertyName("safeDownload")] public bool SafeDownload { get; set; }
    [JsonPropertyName("mirrorBase")] public string MirrorBase { get; set; } = "https://bmclapi2.bangbang93.com";
    [JsonPropertyName("useBootstrap")] public bool UseBootstrap { get; set; } = true;

    /// <summary>Download a matching Temurin JRE automatically when none is installed.</summary>
    [JsonPropertyName("autoDownloadJava")] public bool AutoDownloadJava { get; set; } = true;
    [JsonPropertyName("extraJvmArgs")] public string ExtraJvmArgs { get; set; } = "";

    /// <summary>
    /// Per-version JVM arguments, keyed by installed version id. Appended after the
    /// global <see cref="ExtraJvmArgs"/> so a version can add to it without the
    /// global value leaking into every launch.
    /// </summary>
    [JsonPropertyName("versionJvmArgs")]
    public Dictionary<string, string> VersionJvmArgs { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    [JsonPropertyName("lastVersion")] public string? LastVersion { get; set; }
    [JsonPropertyName("userName")] public string UserName { get; set; } = "Steve";
    [JsonPropertyName("userUuid")] public string UserUuid { get; set; } = "";
    [JsonPropertyName("closeOnLaunch")] public bool CloseOnLaunch { get; set; }
    [JsonPropertyName("showSnapshots")] public bool ShowSnapshots { get; set; }

    /// <summary>
    /// Give every version its own game directory (saves / mods / options.txt).
    /// Required for sane mod management, hence on by default. Turning it off makes
    /// all versions share the root folder, like a vanilla launcher does.
    /// </summary>
    [JsonPropertyName("isolateByVersion")] public bool IsolateByVersion { get; set; } = true;

    /// <summary>
    /// Appearance: "original" (dark) / "acrylic" (blur + translucency) /
    /// "windows" (light), times accent "green|blue|red|white|purple".
    /// Applied at startup; changing it restarts the launcher on save.
    /// </summary>
    [JsonPropertyName("themeFamily")] public string ThemeFamily { get; set; } = "original";
    [JsonPropertyName("themeAccent")] public string ThemeAccent { get; set; } = "green";

    /// <summary>
    /// How solid the acrylic family is: 1.0 = opaque, 0.7 = very glassy. Exposed as a
    /// slider because "毛玻璃看不清字" is a taste question, not a constant.
    /// </summary>
    [JsonPropertyName("acrylicOpacity")] public double AcrylicOpacity { get; set; } = 0.95;

    /// <summary>
    /// Microsoft sign-in client id. Defaults to Mojang's own public client id, so
    /// no Azure registration is needed; advanced users may override it.
    /// </summary>
    [JsonPropertyName("msClientId")] public string MsClientId { get; set; } = MicrosoftAuthService.DefaultClientId;

    /// <summary>
    /// In-game language handed to the JVM as <c>--lang</c>. "auto" follows the
    /// Windows UI language (see <see cref="Services.McLanguages.Resolve"/>).
    /// </summary>
    [JsonPropertyName("gameLanguage")] public string GameLanguage { get; set; } = Services.McLanguages.AutoCode;

    /// <summary>
    /// The language value ECL last wrote into options.txt. Switching back to "auto"
    /// removes that exact line — but only if the player has not since changed it in-game.
    /// </summary>
    [JsonPropertyName("lastAppliedLanguage")] public string LastAppliedLanguage { get; set; } = "";

    /// <summary>True once the user has accepted the terms shown on first start.</summary>
    /// <summary>"auto" follows the Windows UI language; "zh" / "en" force one.</summary>
    /// <summary>
    /// "en" (default), "zh", or "auto" (follow the OS). Ships in English: the public
    /// build is aimed at an international audience, and Chinese is one click away in
    /// Settings -> Appearance.
    /// </summary>
    [JsonPropertyName("uiLanguage")] public string UiLanguage { get; set; } = "en";

    [JsonPropertyName("termsAccepted")] public bool TermsAccepted { get; set; }

    /// <summary>Version of the accepted terms, so a rewritten agreement re-prompts.</summary>
    [JsonPropertyName("termsVersion")] public int TermsVersion { get; set; }

    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        WriteIndented = true,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        DefaultIgnoreCondition = JsonIgnoreCondition.Never
    };

    /// <summary>
    /// Where settings.json lives.
    ///
    /// <para>Portable mode: if a settings.json sits next to the exe, that one wins.
    /// The setup package writes one with <c>dataRoot = "data"</c>, so everything —
    /// settings, accounts, versions, libraries — stays inside the ECL folder and the
    /// whole thing can be moved to a USB stick.</para>
    ///
    /// <para>Otherwise the per-user folder is used, which is what every existing
    /// install already expects.</para>
    /// </summary>
    public static string ResolveSettingsPath()
    {
        string local = Path.Combine(AppContext.BaseDirectory, "settings.json");
        if (File.Exists(local)) return local;

        return Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "EternityCraft", "settings.json");
    }

    public static LauncherSettings Load(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                var s = JsonSerializer.Deserialize<LauncherSettings>(File.ReadAllText(path));
                if (s != null)
                {
                    if (string.IsNullOrWhiteSpace(s.DataRoot))
                        s.DataRoot = Path.GetDirectoryName(path) ?? GamePathsFallback();
                    s.Normalize();
                    return s;
                }
            }
        }
        catch
        {
            // A corrupt settings file must never block start-up: fall back to defaults.
        }

        var fresh = new LauncherSettings { DataRoot = Path.GetDirectoryName(path) ?? GamePathsFallback() };
        fresh.Normalize();
        return fresh;
    }

    private static string GamePathsFallback() => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "EternityCraft");

    public void Normalize()
    {
        if (string.IsNullOrWhiteSpace(DataRoot)) DataRoot = GamePathsFallback();
        if (MemoryMb < 512) MemoryMb = 512;
        if (MemoryMb > 65536) MemoryMb = 65536;
        DownloadThreads = Math.Clamp(DownloadThreads, 1, 64);
        if (string.IsNullOrWhiteSpace(MirrorBase)) MirrorBase = "https://bmclapi2.bangbang93.com";
        MirrorBase = MirrorBase.TrimEnd('/');
        if (string.IsNullOrWhiteSpace(UserName)) UserName = "Steve";
        if (string.IsNullOrWhiteSpace(UserUuid)) UserUuid = OfflineUuid(UserName);

        // A deserialised dictionary ignores the case-insensitive comparer from the
        // initialiser, so rebuild it once here.
        VersionJvmArgs ??= new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (VersionJvmArgs.Comparer != StringComparer.OrdinalIgnoreCase)
        {
            VersionJvmArgs = new Dictionary<string, string>(
                VersionJvmArgs, StringComparer.OrdinalIgnoreCase);
        }

        // Drop blank entries so the file does not accumulate empty keys.
        foreach (var key in VersionJvmArgs.Where(kv => string.IsNullOrWhiteSpace(kv.Value)).Select(kv => kv.Key).ToList())
            VersionJvmArgs.Remove(key);

        // Older settings.json files stored an empty id (the "bring your own app"
        // era); treat that as "use the official one" instead of breaking sign-in.
        if (string.IsNullOrWhiteSpace(MsClientId)) MsClientId = MicrosoftAuthService.DefaultClientId;

        // Unknown ids fall back to the defaults instead of a black window.
        ThemeFamily = ECL.Themes.ThemeManager.FamilyIds
            .FirstOrDefault(f => f.Equals(ThemeFamily.Trim(), StringComparison.OrdinalIgnoreCase), "original");
        ThemeAccent = ECL.Themes.ThemeManager.AccentIds
            .FirstOrDefault(a => a.Equals(ThemeAccent.Trim(), StringComparison.OrdinalIgnoreCase), "green");

        if (double.IsNaN(AcrylicOpacity) || AcrylicOpacity <= 0) AcrylicOpacity = 0.95;
        AcrylicOpacity = Math.Clamp(AcrylicOpacity, 0.70, 1.00);
    }

    public void Save(string path)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, JsonSerializer.Serialize(this, JsonOpts));
        }
        catch
        {
            // Ignored: settings persistence is best effort.
        }
    }

    /// <summary>Stable offline UUID, same algorithm the vanilla server uses for offline mode.</summary>
    public static string OfflineUuid(string name)
    {
        var bytes = System.Text.Encoding.UTF8.GetBytes("OfflinePlayer:" + name);
        var hash = System.Security.Cryptography.MD5.HashData(bytes);
        hash[6] = (byte)((hash[6] & 0x0F) | 0x30);
        hash[8] = (byte)((hash[8] & 0x3F) | 0x80);
        return Convert.ToHexString(hash).ToLowerInvariant();
    }

    public static string FormatUuid(string hex)
    {
        if (hex.Length != 32) return hex;
        return $"{hex[..8]}-{hex[8..12]}-{hex[12..16]}-{hex[16..20]}-{hex[20..]}";
    }
}
