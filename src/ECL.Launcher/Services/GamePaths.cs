// SPDX-License-Identifier: MPL-2.0
// Copyright (c) 2026 ChenchenMC <https://github.com/ChenchenMC-cn/EternityCraftLauncher>
using System.IO;

namespace ECL.Services;

/// <summary>
/// Every path the launcher touches, derived from a single root directory so that
/// moving the data folder is a one-field change in settings.
///
/// <para>With <see cref="IsolateByVersion"/> on (the default), each version gets
/// its own game directory (<c>versions/&lt;id&gt;/game</c>). That is what makes
/// per-version <c>mods/</c>, <c>saves/</c> and <c>options.txt</c> possible: a
/// Forge instance must not see Fabric jars, and an OptiFine-as-mod jar must never
/// leak into the standalone OptiFine profile. Shared assets and libraries stay at
/// the root, so isolation costs no extra disk space for game files.</para>
/// </summary>
public sealed class GamePaths
{
    public string Root { get; }

    /// <summary>True when each version owns its own game directory.</summary>
    public bool IsolateByVersion { get; }

    public GamePaths(string root, bool isolateByVersion = true)
    {
        // Portable mode stores "data" (relative) in settings.json. Resolving that
        // against the working directory would put the data next to whatever folder
        // the shortcut happened to launch from, so anchor it to the exe instead.
        Root = Path.GetFullPath(Path.IsPathRooted(root)
            ? root
            : Path.Combine(AppContext.BaseDirectory, root));

        IsolateByVersion = isolateByVersion;
    }

    public static GamePaths Default => new(Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "EternityCraft"));

    public string VersionsDir => Path.Combine(Root, "versions");
    public string LibrariesDir => Path.Combine(Root, "libraries");
    public string AssetsDir => Path.Combine(Root, "assets");
    public string AssetIndexesDir => Path.Combine(AssetsDir, "indexes");
    public string AssetObjectsDir => Path.Combine(AssetsDir, "objects");
    public string LogsDir => Path.Combine(Root, "logs");

    /// <summary>Downloaded runtimes live here, one folder per Java major.</summary>
    public string RuntimeDir => Path.Combine(Root, "runtime");
    public string JavaDir(int major) => Path.Combine(RuntimeDir, $"java-{major}");

    public string SettingsFile => Path.Combine(Root, "settings.json");
    public string AccountsFile => Path.Combine(Root, "accounts.json");

    public string VersionDir(string id) => Path.Combine(VersionsDir, id);
    public string VersionJson(string id) => Path.Combine(VersionsDir, id, id + ".json");
    public string VersionJar(string id) => Path.Combine(VersionsDir, id, id + ".jar");
    public string VersionNatives(string id) => Path.Combine(VersionsDir, id, "natives");
    public string VersionLog(string id) => Path.Combine(LogsDir, id + "-latest.log");

    /// <summary>The <c>--gameDir</c> handed to the JVM for this version.</summary>
    public string GameDir(string versionId) =>
        IsolateByVersion ? Path.Combine(VersionsDir, versionId, "game") : Root;

    public string ModsDir(string versionId) => Path.Combine(GameDir(versionId), "mods");

    /// <summary>Where disabled mods go instead of being deleted outright.</summary>
    public string ModsTrashDir(string versionId) => Path.Combine(ModsDir(versionId), ".ecl-trash");

    public string AssetObject(string hash) => Path.Combine(AssetObjectsDir, hash[..2], hash);
    public string AssetIndex(string id) => Path.Combine(AssetIndexesDir, id + ".json");
    public string LibraryFile(string relativePath) =>
        Path.Combine(LibrariesDir, relativePath.Replace('/', Path.DirectorySeparatorChar));

    public void EnsureLayout()
    {
        foreach (var dir in new[] { Root, VersionsDir, LibrariesDir, AssetsDir, AssetIndexesDir, AssetObjectsDir, LogsDir })
            Directory.CreateDirectory(dir);
    }
}
