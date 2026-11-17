// SPDX-License-Identifier: MPL-2.0
// Copyright (c) 2026 ChenchenMC <https://github.com/ChenchenMC-cn/EternityCraftLauncher>
using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;

namespace ECL.Services;

/// <summary>The three "drop a zip in a folder" content types the launcher manages.</summary>
public enum PackKind
{
    ResourcePack,
    ShaderPack,
    DataPack
}

/// <summary>One file in a content folder.</summary>
public sealed class PackEntry
{
    public string FileName { get; init; } = "";
    public string FullPath { get; init; } = "";
    public long SizeBytes { get; init; }
    public bool IsEnabled { get; init; }

    /// <summary>File name without the pack extension, for display.</summary>
    public string Title
    {
        get
        {
            string name = FileName;
            if (name.EndsWith(".disabled", StringComparison.OrdinalIgnoreCase))
                name = name[..^".disabled".Length];
            if (name.EndsWith(".zip", StringComparison.OrdinalIgnoreCase))
                name = name[..^".zip".Length];
            return name;
        }
    }

    public string SizeText => SizeBytes switch
    {
        >= 1024 * 1024 => $"{SizeBytes / 1024.0 / 1024.0:F1} MB",
        >= 1024 => $"{SizeBytes / 1024.0:F0} KB",
        _ => $"{SizeBytes} B"
    };

    public string StatusText => IsEnabled ? "启用" : "已禁用";
}

/// <summary>
/// Resource packs, shader packs and data packs are all "a zip in a folder the game scans".
/// One service covers the three so the UI and the rules (disable by renaming, remove to a
/// trash folder) stay identical — the only difference is the directory name.
/// </summary>
public sealed class PackFolderService
{
    private readonly GamePaths _paths;

    public PackFolderService(GamePaths paths) => _paths = paths;

    public static string FolderName(PackKind kind) => kind switch
    {
        PackKind.ResourcePack => "resourcepacks",
        PackKind.ShaderPack => "shaderpacks",
        _ => "datapacks"
    };

    public string Dir(string versionId, PackKind kind) =>
        Path.Combine(_paths.GameDir(versionId), FolderName(kind));

    public string TrashDir(string versionId, PackKind kind) =>
        Path.Combine(Dir(versionId, kind), ".ecl-trash");

    public List<PackEntry> List(string versionId, PackKind kind)
    {
        var result = new List<PackEntry>();
        string dir = Dir(versionId, kind);
        if (!Directory.Exists(dir)) return result;

        foreach (string file in Directory.EnumerateFiles(dir, "*.zip*", SearchOption.TopDirectoryOnly))
        {
            string name = Path.GetFileName(file);
            bool disabled;

            if (name.EndsWith(".zip", StringComparison.OrdinalIgnoreCase)) disabled = false;
            else if (name.EndsWith(".zip.disabled", StringComparison.OrdinalIgnoreCase)) disabled = true;
            else continue;

            result.Add(new PackEntry
            {
                FileName = name,
                FullPath = file,
                SizeBytes = new FileInfo(file).Length,
                IsEnabled = !disabled
            });
        }

        return result
            .OrderByDescending(e => e.IsEnabled)
            .ThenBy(e => e.Title, StringComparer.CurrentCultureIgnoreCase)
            .ToList();
    }

    /// <summary>
    /// Copies a pack in, renaming duplicates instead of overwriting: two resource packs can
    /// legitimately share a file name, and silently replacing the user's file is not a call
    /// this launcher should make.
    /// </summary>
    public PackEntry Import(string versionId, PackKind kind, string sourcePath)
    {
        if (!File.Exists(sourcePath))
            throw new FileNotFoundException("找不到文件", sourcePath);

        if (!IsZip(sourcePath))
            throw new InvalidDataException("这个文件不是有效的 zip 压缩包");

        string dir = Dir(versionId, kind);
        Directory.CreateDirectory(dir);

        string target = Path.Combine(dir, Path.GetFileName(sourcePath));
        int n = 2;
        string stem = Path.GetFileNameWithoutExtension(sourcePath);
        while (File.Exists(target))
            target = Path.Combine(dir, $"{stem} ({n++}).zip");

        File.Copy(sourcePath, target, overwrite: false);

        return new PackEntry
        {
            FileName = Path.GetFileName(target),
            FullPath = target,
            SizeBytes = new FileInfo(target).Length,
            IsEnabled = true
        };
    }

    /// <summary>Disable by renaming to <c>.zip.disabled</c> — the game then ignores the file.</summary>
    public void SetEnabled(string versionId, PackKind kind, PackEntry entry, bool enabled)
    {
        string path = entry.FullPath;
        if (!File.Exists(path) && File.Exists(path + ".disabled")) path += ".disabled";

        bool currentlyEnabled = path.EndsWith(".zip", StringComparison.OrdinalIgnoreCase);
        if (currentlyEnabled == enabled) return;

        string target = enabled
            ? path[..^".disabled".Length]
            : path + ".disabled";

        if (File.Exists(target))
            throw new IOException("目标文件名已存在：" + Path.GetFileName(target));

        File.Move(path, target);
    }

    /// <summary>Moves a pack to <c>.ecl-trash</c> rather than deleting it.</summary>
    public void MoveToTrash(string versionId, PackKind kind, PackEntry entry)
    {
        string trash = TrashDir(versionId, kind);
        Directory.CreateDirectory(trash);

        string target = Path.Combine(trash, $"{DateTime.Now:yyyyMMdd-HHmmss}-{entry.FileName}");
        File.Move(entry.FullPath, target);
    }

    /// <summary>A zip is valid if it opens and has at least one entry; cheap sanity check.</summary>
    public static bool IsZip(string path)
    {
        try
        {
            using var zip = ZipFile.OpenRead(path);
            return zip.Entries.Count > 0;
        }
        catch
        {
            return false;
        }
    }
}
