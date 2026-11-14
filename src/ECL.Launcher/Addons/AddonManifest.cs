// SPDX-License-Identifier: MPL-2.0
// Copyright (c) 2026 ChenchenMC <https://github.com/ChenchenMC-cn/EternityCraftLauncher>
using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Linq;

namespace ECL.Addons;

/// <summary>
/// What an addon is allowed to be. Kept as data so the loader never has to guess.
/// </summary>
public enum AddonKind
{
    /// <summary>Colours only. No code, loaded by the launcher itself.</summary>
    Theme,

    /// <summary>C# assembly implementing <see cref="IEclAddon"/>.</summary>
    Plugin,

    /// <summary>Both: a theme pack that also ships a plugin assembly.</summary>
    Mixed
}

/// <summary>
/// One addon's manifest (<c>addon.json</c>).
///
/// <para>This is the ECL Interface API surface: everything a non-C# author can control
/// lives here, and the launcher refuses anything it cannot validate. Unknown fields are
/// ignored on purpose so a newer pack still loads on an older launcher.</para>
/// </summary>
public sealed class AddonManifest
{
    public const int CurrentSchema = 1;
    public const int CurrentApiVersion = 1;

    [JsonPropertyName("schemaVersion")] public int SchemaVersion { get; set; } = CurrentSchema;
    [JsonPropertyName("apiVersion")] public int ApiVersion { get; set; } = CurrentApiVersion;

    [JsonPropertyName("id")] public string Id { get; set; } = "";
    [JsonPropertyName("name")] public string Name { get; set; } = "";
    [JsonPropertyName("version")] public string Version { get; set; } = "";
    [JsonPropertyName("author")] public string Author { get; set; } = "";
    [JsonPropertyName("description")] public string Description { get; set; } = "";

    [JsonPropertyName("type")] public string Type { get; set; } = "theme";

    /// <summary>Optional assembly entry point for plugin / mixed kinds.</summary>
    [JsonPropertyName("entry")] public AddonEntry? Entry { get; set; }

    /// <summary>Theme payload; required for theme / mixed.</summary>
    [JsonPropertyName("theme")] public ThemeSpec? Theme { get; set; }

    /// <summary>
    /// Declared capabilities. Advisory for now, but it is what a future permission prompt
    /// would read, and it documents intent for anyone auditing a pack.
    /// </summary>
    [JsonPropertyName("capabilities")] public List<string> Capabilities { get; set; } = new();

    [JsonIgnore] public string Directory { get; set; } = "";

    [JsonIgnore]
    public AddonKind Kind => Type?.ToLowerInvariant() switch
    {
        "plugin" => AddonKind.Plugin,
        "mixed" => AddonKind.Mixed,
        _ => AddonKind.Theme
    };

    private static readonly JsonSerializerOptions Options = new()
    {
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true
    };

    /// <summary>
    /// Reads and validates one manifest. Returns null plus a reason instead of throwing:
    /// a broken pack must never stop the launcher from starting.
    /// </summary>
    public static AddonManifest? Load(string jsonPath, out string error)
    {
        error = "";
        try
        {
            var m = JsonSerializer.Deserialize<AddonManifest>(File.ReadAllText(jsonPath), Options);
            if (m == null) { error = "manifest is empty"; return null; }

            m.Directory = Path.GetDirectoryName(jsonPath) ?? "";

            if (m.SchemaVersion != CurrentSchema)
            {
                error = $"schemaVersion {m.SchemaVersion} is not supported (expected {CurrentSchema})";
                return null;
            }

            if (m.ApiVersion > CurrentApiVersion)
            {
                error = $"apiVersion {m.ApiVersion} is newer than this launcher supports " +
                        $"({CurrentApiVersion}); update the launcher";
                return null;
            }

            if (string.IsNullOrWhiteSpace(m.Id) || !IsValidId(m.Id))
            {
                error = "id must be 2-48 chars of a-z 0-9 . _ - and start with a letter";
                return null;
            }

            m.Name = string.IsNullOrWhiteSpace(m.Name) ? m.Id : m.Name.Trim();

            if (m.Kind is AddonKind.Plugin or AddonKind.Mixed)
            {
                if (m.Entry == null || string.IsNullOrWhiteSpace(m.Entry.Assembly))
                {
                    error = "type is plugin/mixed but entry.assembly is missing";
                    return null;
                }

                if (!File.Exists(Path.Combine(m.Directory, m.Entry.Assembly)))
                {
                    error = $"entry assembly not found: {m.Entry.Assembly}";
                    return null;
                }
            }

            if (m.Kind is AddonKind.Theme or AddonKind.Mixed && m.Theme == null)
            {
                error = "type is theme/mixed but the theme block is missing";
                return null;
            }

            if (m.Theme != null)
            {
                foreach (var (key, hex) in m.Theme.Colors)
                {
                    if (!IsHexColor(hex))
                    {
                        error = $"theme.colors.{key} = '{hex}' is not #RRGGBB or #AARRGGBB";
                        return null;
                    }
                }

                if (m.Theme.Opacity is < 0.5 or > 1.0)
                {
                    error = "theme.opacity must be between 0.5 and 1.0";
                    return null;
                }

                // Contrast guard. A pack that repaints the surfaces but leaves the text
                // colours from the old base produces unreadable screens (white on white,
                // exactly what the first Aurora test did). Either do both or stay on the
                // accent keys, where contrast is guaranteed by the base palette.
                bool touchesSurface = m.Theme.Colors.Keys.Any(k =>
                    k.StartsWith("Bg", StringComparison.OrdinalIgnoreCase) ||
                    k.StartsWith("Border", StringComparison.OrdinalIgnoreCase));
                bool touchesText = m.Theme.Colors.Keys.Any(k =>
                    k.StartsWith("Text", StringComparison.OrdinalIgnoreCase));

                if (touchesSurface && !touchesText)
                {
                    error = "theme.colors repaints background/border but sets no Text* colour " +
                            "(would break contrast); override both, or only Accent* keys";
                    return null;
                }
            }

            return m;
        }
        catch (Exception ex)
        {
            error = ex.GetType().Name + ": " + ex.Message;
            return null;
        }
    }

    private static bool IsValidId(string id)
    {
        if (id.Length is < 2 or > 48) return false;
        if (!char.IsAsciiLetterLower(id[0])) return false;

        foreach (char c in id)
            if (!(char.IsAsciiLetterLower(c) || char.IsAsciiDigit(c) || c is '.' or '_' or '-'))
                return false;

        return true;
    }

    private static bool IsHexColor(string? s)
    {
        if (string.IsNullOrEmpty(s) || s[0] != '#') return false;
        if (s.Length is not (7 or 9)) return false;

        for (int i = 1; i < s.Length; i++)
            if (!Uri.IsHexDigit(s[i])) return false;

        return true;
    }
}

public sealed class AddonEntry
{
    [JsonPropertyName("assembly")] public string Assembly { get; set; } = "";
    [JsonPropertyName("type")] public string Type { get; set; } = "";
}

/// <summary>The <c>theme</c> block: a base palette plus per-key overrides.</summary>
public sealed class ThemeSpec
{
    [JsonPropertyName("baseFamily")] public string BaseFamily { get; set; } = "original";
    [JsonPropertyName("baseAccent")] public string BaseAccent { get; set; } = "green";
    [JsonPropertyName("opacity")] public double Opacity { get; set; } = 0.95;

    /// <summary>
    /// Brush keys from <c>ThemeManager</c> (e.g. <c>Accent</c>, <c>BgWindow</c>) mapped to
    /// a colour. Everything unnamed keeps the base palette, so a pack can be five lines.
    /// </summary>
    [JsonPropertyName("colors")] public Dictionary<string, string> Colors { get; set; } = new();
}
