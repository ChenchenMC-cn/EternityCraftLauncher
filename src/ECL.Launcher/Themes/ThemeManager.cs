// SPDX-License-Identifier: MPL-2.0
// Copyright (c) 2026 ChenchenMC <https://github.com/ChenchenMC-cn/EternityCraftLauncher>
using System.Windows;
using System.Windows.Media;

namespace ECL.Themes;

public enum ThemeFamily
{
    Original = 0,   // 原版风：当前深色
    Acrylic = 1,    // 亚克力 + 毛玻璃 + 半透明（Win10 1803+）
    Windows = 2     // Windows 风浅色（设置页那种）
}

public enum ThemeAccent
{
    Green = 0,
    Blue = 1,
    Red = 2,
    White = 3,
    Purple = 4
}

/// <summary>
/// Builds the palette half of the theme. Styles live in Theme.xaml and reference
/// these brushes by key with StaticResource, which is why App.OnStartup merges
/// this dictionary BEFORE Theme.xaml: parse order decides what the styles bind to.
///
/// <para>Three families x five accents, all defined here in one table so a new
/// colour scheme is a data change, not a XAML archaeology trip.</para>
/// </summary>
public static class ThemeManager
{
    public static readonly string[] FamilyLabels = { "原版风", "毛玻璃", "Windows 风" };
    public static readonly string[] AccentLabels = { "绿色", "蓝色", "红色", "白色", "紫色" };
    public static readonly string[] FamilyIds = { "original", "acrylic", "windows" };
    public static readonly string[] AccentIds = { "green", "blue", "red", "white", "purple" };

    /// <summary>True for the acrylic family: the window itself becomes translucent.</summary>
    public static bool IsTranslucent(ThemeFamily family) => family == ThemeFamily.Acrylic;

    // ------------------------------------------------------------ parsing ---

    public static ThemeFamily ParseFamily(string? id)
    {
        for (int i = 0; i < FamilyIds.Length; i++)
            if (string.Equals(FamilyIds[i], id, StringComparison.OrdinalIgnoreCase))
                return (ThemeFamily)i;
        // also accept the raw enum names
        return Enum.TryParse(id, ignoreCase: true, out ThemeFamily f) ? f : ThemeFamily.Original;
    }

    public static ThemeAccent ParseAccent(string? id)
    {
        for (int i = 0; i < AccentIds.Length; i++)
            if (string.Equals(AccentIds[i], id, StringComparison.OrdinalIgnoreCase))
                return (ThemeAccent)i;
        return Enum.TryParse(id, ignoreCase: true, out ThemeAccent a) ? a : ThemeAccent.Green;
    }

    /// <summary>Preview colours for the settings page swatches (depend on family).</summary>
    public static string[] AccentSwatches(string family) =>
        AccentSwatches(ParseFamily(family));

    public static string[] AccentSwatches(ThemeFamily family)
    {
        var accents = Accents[family];
        var result = new string[accents.Length];
        for (int i = 0; i < accents.Length; i++)
            result[i] = accents[i].Accent;
        return result;
    }

    // -------------------------------------------------------------- apply ---

    public static void Apply(Application app, string familyId, string accentId, double acrylicOpacity = DefaultAcrylicOpacity) =>
        Apply(app, ParseFamily(familyId), ParseAccent(accentId), acrylicOpacity);

    public static void Apply(Application app, ThemeFamily family, ThemeAccent accent,
        double acrylicOpacity = DefaultAcrylicOpacity)
    {
        var dict = BuildPalette(family, accent, acrylicOpacity);
        app.Resources.MergedDictionaries.Insert(0, dict);
    }

    /// <summary>Master opacity of the acrylic family: 1.0 = solid, 0.7 = very glassy.</summary>
    public const double DefaultAcrylicOpacity = 0.95;

    private static ResourceDictionary BuildPalette(ThemeFamily family, ThemeAccent accent,
        double acrylicOpacity = DefaultAcrylicOpacity)
    {
        // Acrylic surfaces are generated, not tabulated: one master opacity drives every
        // layer, so "还是太透" is a slider instead of another hard-coded guess.
        var surfaces = family == ThemeFamily.Acrylic
            ? AcrylicSurfaces(acrylicOpacity)
            : Surfaces[family];
        var a = Accents[family][(int)accent];

        var dict = new ResourceDictionary();

        void Brush(string key, string hex)
        {
            var color = (Color)ColorConverter.ConvertFromString(hex);
            dict[key] = new SolidColorBrush(color);
            if (!key.EndsWith("Color", StringComparison.Ordinal))
                dict[key + "Color"] = color;    // some templates want the Color itself
        }

        foreach (var (key, hex) in surfaces)
            Brush(key, key == "BgWindow" ? WindowTint(family, accent, acrylicOpacity) : hex);

        Brush("Accent", a.Accent);
        Brush("AccentDim", a.Dim);
        Brush("AccentAlt", a.Alt);
        Brush("OnAccent", a.On);
        Brush("AccentTint", a.Tint);
        Brush("Danger", family == ThemeFamily.Windows ? "#C42B1C" : "#FF6B6B");
        Brush("Warn", family == ThemeFamily.Windows ? "#9A6700" : "#F5B942");

        // Accent-tinted halo for the slider thumb (accent at 20%).
        Brush("AccentHalo", WithAlpha(a.Accent, 0x33));

        var gradient = new LinearGradientBrush(new GradientStopCollection
        {
            new GradientStop((Color)ColorConverter.ConvertFromString(a.Accent), 0),
            new GradientStop((Color)ColorConverter.ConvertFromString(a.Alt), 1)
        })
        { StartPoint = new Point(0, 0), EndPoint = new Point(1, 1) };
        dict["AccentGradient"] = gradient;

        var gradientHover = new LinearGradientBrush(new GradientStopCollection
        {
            new GradientStop((Color)ColorConverter.ConvertFromString(a.HoverA), 0),
            new GradientStop((Color)ColorConverter.ConvertFromString(a.HoverB), 1)
        })
        { StartPoint = new Point(0, 0), EndPoint = new Point(1, 1) };
        dict["AccentGradientHover"] = gradientHover;

        return dict;
    }

    private static string WithAlpha(string hex, byte alpha) =>
        $"#{alpha:X2}{hex.TrimStart('#')}";

    // -------------------------------------------------------------- table ---

    private sealed record AccentSet(string Accent, string Alt, string HoverA, string HoverB,
        string Dim, string Tint, string On);

    /// <summary>Family-independent per-accent sets for the dark families.</summary>
    private static readonly AccentSet[] DarkAccents =
    {
        new("#3DDC97", "#2BB8FF", "#54E8A6", "#54C8FF", "#1F5B45", "#14211C", "#04140D"),
        new("#2BB8FF", "#6E7CFF", "#54C8FF", "#9AA8FF", "#1C4D6E", "#12202B", "#041019"),
        new("#FF6B6B", "#FF9E64", "#FF8585", "#FFB08A", "#6E2525", "#241416", "#1A0505"),
        new("#E9EEF7", "#8A94A8", "#FFFFFF", "#D6DEEB", "#5A6478", "#1C222E", "#0B0E14"),
        new("#B98CFF", "#FF8AD8", "#C9A3FF", "#FFA1E0", "#4E3378", "#1C1629", "#12071F")
    };

    private static readonly AccentSet[] LightAccents =
    {
        new("#0E8A5F", "#37B87F", "#12A06D", "#5BC896", "#B7E4D2", "#E2F3EA", "#FFFFFF"),
        new("#0067C0", "#2E8AE6", "#1B7BD3", "#5FA9E8", "#BFD9F2", "#E3EEF9", "#FFFFFF"),
        new("#D13438", "#F2755F", "#E04A4E", "#F2948A", "#F5C9C9", "#FAE7E7", "#FFFFFF"),
        new("#6E7681", "#9AA2AC", "#7E8791", "#A8B0BA", "#D8DCE1", "#EEF0F2", "#FFFFFF"),
        new("#6B3FBF", "#9B6BE0", "#7C50D0", "#A985E8", "#DCCDF3", "#EEE7F9", "#FFFFFF")
    };

    private static readonly Dictionary<ThemeFamily, AccentSet[]> Accents = new()
    {
        [ThemeFamily.Original] = DarkAccents,
        [ThemeFamily.Acrylic] = DarkAccents,
        [ThemeFamily.Windows] = LightAccents
    };

    /// <summary>
    /// Builds the acrylic palette from one master opacity.
    ///
    /// <para>Every layer is "master + offset", so raising the slider lifts the whole
    /// stack together and the cards stay slightly more solid than the window behind
    /// them. Text stays white: the surface is what carries the contrast.</para>
    /// </summary>
    public static (string, string)[] AcrylicSurfaces(double opacity)
    {
        double master = Math.Clamp(opacity, 0.70, 1.00);

        static string Hex(double alpha, string rgb)
        {
            int a = (int)Math.Round(Math.Clamp(alpha, 0.05, 1.00) * 255);
            return "#" + a.ToString("X2") + rgb;
        }

        string A(double offset, string rgb) => Hex(master + offset, rgb);

        return new (string, string)[]
        {
            ("BgWindow", A(0.00, "0B0E14")), ("BgSidebar", A(-0.01, "0E1219")),
            ("BgPanel", A(0.02, "11161F")), ("BgCard", A(0.05, "151A24")),
            ("BgCardHover", A(0.08, "1B2130")), ("BgInput", A(0.06, "0D1119")),
            // Borders keep their own translucency: they read as hairlines, not surfaces.
            ("BorderSubtle", Hex(0.42 + master * 0.18, "262E3C")),
            ("BorderStrong", Hex(0.52 + master * 0.18, "323D52")),
            ("TextPrimary", "#E9EEF7"), ("TextSecondary", "#8A94A8"), ("TextMuted", "#5B6478"),
            ("HoverBg", A(0.08, "1B2230")), ("HoverBgStrong", A(0.10, "232C3C")),
            ("SelectedBg", A(0.06, "16202B")),
            ("BorderHover", Hex(0.55 + master * 0.20, "3A4A63")),
            ("BadgeBg", A(0.06, "1D2431")), ("AvatarBg", A(0.06, "25303F")),
            ("ErrorCardBg", A(0.04, "2A1519")), ("ErrorCardBorder", Hex(0.55 + master * 0.18, "5A2A31")),
            ("ErrorText", "#FFB4B4"),
            ("WarnCardBg", A(0.04, "2A2416")), ("WarnCardBorder", Hex(0.55 + master * 0.18, "5A4A22")),
            ("WarnText", "#F5C877"),
            ("ThumbDot", "#F2F6FC")
        };
    }

    private static readonly Dictionary<ThemeFamily, (string Key, string Hex)[]> Surfaces = new()
    {
        [ThemeFamily.Original] = new (string, string)[]
        {
            ("BgWindow", "#0B0E14"), ("BgSidebar", "#0E1219"), ("BgPanel", "#11161F"),
            ("BgCard", "#151A24"), ("BgCardHover", "#1B2130"), ("BgInput", "#0D1119"),
            ("BorderSubtle", "#1E2532"), ("BorderStrong", "#2A3446"),
            ("TextPrimary", "#E9EEF7"), ("TextSecondary", "#8A94A8"), ("TextMuted", "#5B6478"),
            ("HoverBg", "#1B2230"), ("HoverBgStrong", "#232C3C"), ("SelectedBg", "#16202B"),
            ("BorderHover", "#33415A"), ("BadgeBg", "#1D2431"), ("AvatarBg", "#25303F"),
            ("ErrorCardBg", "#2A1519"), ("ErrorCardBorder", "#5A2A31"), ("ErrorText", "#FFB4B4"),
            ("WarnCardBg", "#2A2416"), ("WarnCardBorder", "#5A4A22"), ("WarnText", "#F5C877"),
            ("ThumbDot", "#F2F6FC")
        },

        // The acrylic family is generated by AcrylicSurfaces() so the user's opacity
        // setting can drive it; nothing reads this entry.
        [ThemeFamily.Acrylic] = Array.Empty<(string, string)>(),

        [ThemeFamily.Windows] = new (string, string)[]
        {
            // Filled per-accent below for BgWindow: the Win11 settings look has a
            // faint accent tint on the desktop-level background.
            ("BgWindow", "#F3F3F3"), ("BgSidebar", "#F8F9FA"), ("BgPanel", "#FFFFFF"),
            ("BgCard", "#FFFFFF"), ("BgCardHover", "#F4F6F8"), ("BgInput", "#FFFFFF"),
            ("BorderSubtle", "#E4E6EA"), ("BorderStrong", "#D3D6DC"),
            ("TextPrimary", "#1B1F24"), ("TextSecondary", "#5D6570"), ("TextMuted", "#8B929C"),
            ("HoverBg", "#F1F2F4"), ("HoverBgStrong", "#E9EAEE"), ("SelectedBg", "#ECEDEF"),
            ("BorderHover", "#C4C9D0"), ("BadgeBg", "#EFF1F4"), ("AvatarBg", "#E9EDF3"),
            ("ErrorCardBg", "#FDEDED"), ("ErrorCardBorder", "#F3C8C8"), ("ErrorText", "#B3261E"),
            ("WarnCardBg", "#FDF5E4"), ("WarnCardBorder", "#EFDDB0"), ("WarnText", "#8A6116"),
            ("ThumbDot", "#FFFFFF")
        }
    };

    /// <summary>
    /// Window background for a family. Acrylic is generated from the opacity setting, so
    /// it cannot be read out of the static table -- asking for it used to throw
    /// "Sequence contains no matching element" at start-up.
    /// </summary>
    public static string WindowTint(ThemeFamily family, ThemeAccent accent,
        double acrylicOpacity = DefaultAcrylicOpacity) => family switch
    {
        ThemeFamily.Windows => accent switch
        {
            ThemeAccent.Green => "#EDF5F0",
            ThemeAccent.Blue => "#ECF2F8",
            ThemeAccent.Red => "#F8F0EE",
            ThemeAccent.White => "#F5F5F6",
            _ => "#F2EFF8"
        },
        ThemeFamily.Acrylic => AcrylicSurfaces(acrylicOpacity).First(s => s.Item1 == "BgWindow").Item2,
        _ => Surfaces[family].First(s => s.Key == "BgWindow").Hex
    };
}
