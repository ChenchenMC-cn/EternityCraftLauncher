// SPDX-License-Identifier: MPL-2.0
// Copyright (c) 2026 ChenchenMC <https://github.com/ChenchenMC-cn/EternityCraftLauncher>
using System.IO;

namespace ECL.Services;

/// <summary>An existing Minecraft layout found on disk, outside ECL's own data root.</summary>
/// <param name="Root">Directory that contains <c>versions/</c>, <c>libraries/</c>, <c>assets/</c>.</param>
/// <param name="VersionCount">Every directory under <c>versions/</c>, complete or not.</param>
/// <param name="ReadyCount">How many of those can actually be launched.</param>
public sealed record ExistingInstall(string Root, int VersionCount, int ReadyCount)
{
    public bool HasLaunchable => ReadyCount > 0;
}

/// <summary>
/// Finds a Minecraft install ECL is not currently using.
///
/// <para>Why this exists: ECL keeps its data in <c>%APPDATA%\EternityCraft</c> (or
/// <c>ECL\data</c> in portable mode), not in <c>.minecraft</c>. People who "just
/// replace .minecraft" therefore see an empty version list and conclude the
/// launcher cannot read their install. It can -- it was looking somewhere else.</para>
/// </summary>
public static class ExistingInstallFinder
{
    /// <summary>
    /// Validates one path the user picked. Accepts the root (<c>.minecraft</c>), its
    /// <c>versions</c> folder, or a single <c>versions/&lt;id&gt;</c> directory --
    /// people drag whatever they have into the folder picker.
    /// </summary>
    public static ExistingInstall? Inspect(string? path)
    {
        string? root = Normalise(path);
        if (root == null) return null;

        try
        {
            // Probing with isolation off is the only way to see every profile that
            // is physically present; the mod count is irrelevant here.
            var probes = InstalledVersionScanner.Probe(new GamePaths(root, isolateByVersion: false));
            if (probes.Count == 0) return null;

            return new ExistingInstall(root, probes.Count, probes.Count(p => p.Health == "ready"));
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// The installs worth offering, best first. <paramref name="currentRoot"/> is
    /// excluded so the launcher never suggests switching to where it already is.
    /// </summary>
    public static IReadOnlyList<ExistingInstall> Discover(string currentRoot)
    {
        var candidates = new List<string?>();

        // Next to the launcher first: a portable copy usually ships beside its data.
        candidates.Add(Path.Combine(AppContext.BaseDirectory, ".minecraft"));

        // The official location, and what people mean by "replace .minecraft".
        candidates.Add(Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), ".minecraft"));

        // A folder up: users unzip launchers into a subdirectory.
        string exeParent = Path.GetDirectoryName(
            AppContext.BaseDirectory.TrimEnd(Path.DirectorySeparatorChar)) ?? "";
        if (exeParent.Length > 0)
            candidates.Add(Path.Combine(Path.GetDirectoryName(exeParent) ?? exeParent, ".minecraft"));

        // Our own default, in case the user moved the data root elsewhere.
        candidates.Add(Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "EternityCraft"));

        string normalisedCurrent = SafeFullPath(currentRoot) ?? currentRoot;
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var found = new List<ExistingInstall>();

        foreach (string? candidate in candidates)
        {
            if (string.IsNullOrWhiteSpace(candidate)) continue;

            string full = SafeFullPath(candidate) ?? candidate;
            if (!seen.Add(full)) continue;
            if (string.Equals(full, normalisedCurrent, StringComparison.OrdinalIgnoreCase)) continue;
            if (!Directory.Exists(full)) continue;

            var info = Inspect(full);
            if (info == null || info.VersionCount == 0) continue;
            found.Add(info);
        }

        // Deliberately not sorted: the candidate list above is a priority order
        // (folder next to the launcher first, then %APPDATA%\.minecraft, then our
        // own default). Sorting by counts would push ECL's own stale data root
        // above the .minecraft the user just dropped in.
        return found;
    }

    /// <summary>Maps whatever the user pointed at onto a directory that has <c>versions/</c>.</summary>
    private static string? Normalise(string? path)
    {
        if (string.IsNullOrWhiteSpace(path)) return null;

        string full;
        try { full = Path.GetFullPath(path.Trim().Trim('"')); }
        catch { return null; }

        if (!Directory.Exists(full)) return null;

        // <root>
        if (Directory.Exists(Path.Combine(full, "versions"))) return full;

        string name = Path.GetFileName(full.TrimEnd(Path.DirectorySeparatorChar));

        // <root>/versions
        if (name.Equals("versions", StringComparison.OrdinalIgnoreCase))
        {
            string? parent = Path.GetDirectoryName(full.TrimEnd(Path.DirectorySeparatorChar));
            return parent != null && Directory.Exists(Path.Combine(parent, "versions")) ? parent : null;
        }

        // <root>/versions/<id>
        string? maybeVersions = Path.GetDirectoryName(full.TrimEnd(Path.DirectorySeparatorChar));
        if (maybeVersions != null &&
            Path.GetFileName(maybeVersions).Equals("versions", StringComparison.OrdinalIgnoreCase))
        {
            string? root = Path.GetDirectoryName(maybeVersions);
            return root != null && Directory.Exists(Path.Combine(root, "versions")) ? root : null;
        }

        // A bare folder of profiles: tell the caller nothing was found rather than
        // silently treating an unrelated directory as a game root.
        return null;
    }

    private static string? SafeFullPath(string path)
    {
        try { return Path.GetFullPath(path); }
        catch { return null; }
    }
}
