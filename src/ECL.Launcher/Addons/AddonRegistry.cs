// SPDX-License-Identifier: MPL-2.0
// Copyright (c) 2026 ChenchenMC <https://github.com/ChenchenMC-cn/EternityCraftLauncher>
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace ECL.Addons;

/// <summary>
/// Discovers addons under <c>&lt;dataRoot&gt;/addons/&lt;id&gt;/addon.json</c>.
///
/// <para>Loading is deliberately cheap and total: every folder is read once at startup,
/// bad packs are collected with a reason instead of aborting, and nothing is executed
/// here — plugin assemblies are only resolved when the registry is asked for them.</para>
///
/// <para>Order is deterministic (id, ordinal) so a theme pack cannot win by luck.</para>
/// </summary>
public static class AddonRegistry
{
    private static readonly List<AddonManifest> _all = new();
    private static readonly List<(string Path, string Reason)> _rejected = new();

    public static IReadOnlyList<AddonManifest> All => _all;
    public static IReadOnlyList<(string Path, string Reason)> Rejected => _rejected;

    /// <summary>Id of the theme pack to apply, from settings. Empty = built-in themes only.</summary>
    public static string ActiveThemeId { get; private set; } = "";

    public static AddonManifest? ActiveTheme =>
        _all.FirstOrDefault(a => string.Equals(a.Id, ActiveThemeId, StringComparison.OrdinalIgnoreCase)
                                 && a.Theme != null);

    /// <summary>
    /// Scans the addon folder. Safe to call twice; the previous state is dropped first.
    /// </summary>
    public static void Reload(string dataRoot, string? activeThemeId)
    {
        _all.Clear();
        _rejected.Clear();
        ActiveThemeId = activeThemeId ?? "";

        string root = Path.Combine(dataRoot, "addons");
        if (!Directory.Exists(root)) return;

        foreach (string dir in Directory.EnumerateDirectories(root).OrderBy(d => d, StringComparer.Ordinal))
        {
            string manifestPath = Path.Combine(dir, "addon.json");
            if (!File.Exists(manifestPath))
            {
                _rejected.Add((dir, "addon.json is missing"));
                continue;
            }

            var m = AddonManifest.Load(manifestPath, out string error);
            if (m == null)
            {
                _rejected.Add((manifestPath, error));
                continue;
            }

            // Two packs with the same id: the first wins, the second is reported rather
            // than silently shadowed -- duplicated ids are almost always a packaging bug.
            if (_all.Any(a => string.Equals(a.Id, m.Id, StringComparison.OrdinalIgnoreCase)))
            {
                _rejected.Add((manifestPath, $"duplicate id '{m.Id}'"));
                continue;
            }

            _all.Add(m);
        }
    }

    public static AddonManifest? Find(string id) =>
        _all.FirstOrDefault(a => string.Equals(a.Id, id, StringComparison.OrdinalIgnoreCase));

    /// <summary>One-line summary for the log and the diagnostics bundle.</summary>
    public static string Describe()
    {
        if (_all.Count == 0 && _rejected.Count == 0) return "addons: none";

        var accepted = string.Join(", ", _all.Select(a => $"{a.Id}/{a.Kind}"));
        return _rejected.Count == 0
            ? $"addons: {_all.Count} loaded ({accepted})"
            : $"addons: {_all.Count} loaded ({accepted}), {_rejected.Count} rejected";
    }
}
