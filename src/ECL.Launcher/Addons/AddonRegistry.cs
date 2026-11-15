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

    /// <summary>Folder that holds every addon: <c>&lt;data root&gt;/addons</c>.</summary>
    public static string Root(string dataRoot) => Path.Combine(dataRoot, "addons");

    /// <summary>
    /// Installs a packed addon (.zip) and returns its id.
    ///
    /// <para>The archive may wrap the files in a folder (that is what GitHub's "download
    /// zip" does), so the manifest is searched anywhere inside. Nothing is written until
    /// the manifest has passed the same validation the loader applies at start-up — a
    /// broken pack is rejected here instead of silently failing on the next launch.</para>
    /// </summary>
    public static string? ImportZip(string dataRoot, string zipPath, out string error)
    {
        error = "";
        string work = Path.Combine(Path.GetTempPath(), "ecl-addon-" + Guid.NewGuid().ToString("N")[..8]);

        try
        {
            Directory.CreateDirectory(work);
            System.IO.Compression.ZipFile.ExtractToDirectory(zipPath, work);

            string? manifestPath = Directory
                .EnumerateFiles(work, "addon.json", SearchOption.AllDirectories)
                .FirstOrDefault();

            if (manifestPath == null)
            {
                error = "压缩包里找不到 addon.json";
                return null;
            }

            var manifest = AddonManifest.Load(manifestPath, out string why);
            if (manifest == null)
            {
                error = why;
                return null;
            }

            string root = Root(dataRoot);
            string target = Path.Combine(root, manifest.Id);
            if (Directory.Exists(target))
            {
                error = $"已经装了同 id 的插件：{manifest.Id}";
                return null;
            }

            Directory.CreateDirectory(root);
            CopyDirectory(Path.GetDirectoryName(manifestPath)!, target);

            return manifest.Id;
        }
        catch (Exception ex)
        {
            error = ex.Message;
            return null;
        }
        finally
        {
            try { if (Directory.Exists(work)) Directory.Delete(work, true); } catch { /* temp */ }
        }
    }

    /// <summary>
    /// Moves an addon folder to <c>addons/.trash</c>. Deleting someone's pack outright is
    /// not this launcher's call — the trash keeps "undo" possible without a dialog.
    /// </summary>
    public static bool MoveToTrash(string dataRoot, string id, out string error)
    {
        error = "";
        try
        {
            string dir = Path.Combine(Root(dataRoot), id);
            if (!Directory.Exists(dir))
            {
                error = "找不到插件目录：" + id;
                return false;
            }

            string trash = Path.Combine(Root(dataRoot), ".trash");
            Directory.CreateDirectory(trash);
            string target = Path.Combine(trash, $"{DateTime.Now:yyyyMMdd-HHmmss}-{id}");

            Directory.Move(dir, target);
            return true;
        }
        catch (Exception ex)
        {
            error = ex.Message;
            return false;
        }
    }

    private static void CopyDirectory(string source, string target)
    {
        Directory.CreateDirectory(target);

        foreach (string file in Directory.EnumerateFiles(source))
            File.Copy(file, Path.Combine(target, Path.GetFileName(file)), overwrite: true);

        foreach (string dir in Directory.EnumerateDirectories(source))
            CopyDirectory(dir, Path.Combine(target, Path.GetFileName(dir)));
    }

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
