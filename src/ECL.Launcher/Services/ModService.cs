// SPDX-License-Identifier: MPL-2.0
// Copyright (c) 2026 ChenchenMC <https://github.com/ChenchenMC-cn/EternityCraftLauncher>
using System.IO;
using System.IO.Compression;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using ECL.ViewModels;

namespace ECL.Services;

/// <summary>Which loader a mod jar was built for, decided from its metadata.</summary>
public enum ModPlatform
{
    Unknown = 0,
    Fabric = 1,
    Quilt = 2,
    Forge = 3,
    OptiFine = 4
}

/// <summary>One file in a version's <c>mods/</c> folder.</summary>

/// <summary>One declared dependency: the mod id and, when the loader tells us, a version range.</summary>
public sealed record ModDependency(string ModId, string Requirement, bool Mandatory)
{
    public string Label => string.IsNullOrWhiteSpace(Requirement) ? ModId : $"{ModId} ({Requirement})";
}

/// <summary>A dependency that no enabled mod satisfies.</summary>
public sealed record MissingDependency(string ModId, string Requirement, string RequiredBy)
{
    public string Label => string.IsNullOrWhiteSpace(Requirement)
        ? ModId
        : $"{ModId} ({Requirement})";

    public string Detail => Label + " ← " + RequiredBy;
}

/// <summary>Ids that the loader itself provides — never worth reporting as missing.</summary>
internal static class PlatformMods
{
    private static readonly HashSet<string> Ids = new(StringComparer.OrdinalIgnoreCase)
    {
        "minecraft", "java", "fabricloader", "fabric_loader", "fabric", "quilt_loader",
        "quilt", "forge", "neoforge", "optifine", "ecl"
    };

    public static bool IsPlatform(string id) => Ids.Contains(id);
}

public sealed class ModEntry : ObservableObject
{
    private bool _isEnabled;

    public string FileName { get; set; } = "";
    public string FullPath { get; set; } = "";
    public string DisplayName { get; set; } = "";
    public string ModId { get; set; } = "";
    public string ModVersion { get; set; } = "";
    public string Authors { get; set; } = "";
    public string? Description { get; set; }
    public ModPlatform Platform { get; set; } = ModPlatform.Unknown;
    public long SizeBytes { get; set; }
    public string? ParseError { get; set; }

    /// <summary>Required mod ids declared by the jar itself (platform ones filtered out).</summary>
    public List<ModDependency> Depends { get; set; } = new();

    public bool IsEnabled
    {
        get => _isEnabled;
        set => SetProperty(ref _isEnabled, value);
    }

    public string Title => string.IsNullOrWhiteSpace(DisplayName) ? FileName : DisplayName;

    public string SubTitle
    {
        get
        {
            var parts = new List<string>(3);
            if (!string.IsNullOrWhiteSpace(ModVersion)) parts.Add("v" + ModVersion);
            if (!string.IsNullOrWhiteSpace(Authors)) parts.Add(Authors);
            parts.Add(SizeText);
            return string.Join("  ·  ", parts);
        }
    }

    public string FileHint => string.IsNullOrWhiteSpace(ModId)
        ? FileName
        : $"{ModId}  ·  {FileName}";

    public string SizeText => SizeBytes switch
    {
        >= 1024 * 1024 => $"{SizeBytes / 1024.0 / 1024.0:F1} MB",
        >= 1024 => $"{SizeBytes / 1024.0:F0} KB",
        _ => $"{SizeBytes} B"
    };

    public string PlatformLabel => Platform switch
    {
        ModPlatform.Fabric => "FABRIC",
        ModPlatform.Quilt => "QUILT",
        ModPlatform.Forge => "FORGE",
        ModPlatform.OptiFine => "OPTIFINE",
        _ => "未知"
    };

    public string PlatformColor => Platform switch
    {
        ModPlatform.Fabric => "#F5B942",
        ModPlatform.Quilt => "#B98CFF",
        ModPlatform.Forge => "#8FA6FF",
        ModPlatform.OptiFine => "#FF9E64",
        _ => "#8A94A8"
    };

    /// <summary>Re-raises the bindings that depend on the file name.</summary>
    internal void NotifyFileNameChanged()
    {
        OnPropertyChanged(nameof(FileName));
        OnPropertyChanged(nameof(FullPath));
        OnPropertyChanged(nameof(FileHint));
        OnPropertyChanged(nameof(Title));
    }

    /// <summary>True when the mod can be loaded by the given version kind.</summary>
    public bool WorksOn(LoaderKind kind) => Platform switch
    {
        // Fabric mods run on Quilt (Quilt keeps the Fabric loader compatibility layer).
        ModPlatform.Fabric => kind is LoaderKind.Fabric or LoaderKind.Quilt,
        ModPlatform.Quilt => kind is LoaderKind.Quilt or LoaderKind.Fabric,
        ModPlatform.Forge => kind is LoaderKind.Forge,
        // OptiFine as a mod is only meaningful on Forge; the standalone profile
        // already contains it.
        ModPlatform.OptiFine => kind is LoaderKind.Forge,
        _ => true
    };
}

/// <summary>
/// Reads and edits a version's <c>mods/</c> folder.
///
/// <para>Metadata comes straight out of the jar (no network, no external mod
/// database): <c>fabric.mod.json</c> for Fabric/Quilt, <c>META-INF/mods.toml</c>
/// for Forge and OptiFine, <c>mcmod.info</c> for 1.12-era jars. A jar whose
/// metadata cannot be parsed is still listed, just without a name — a launcher
/// has no business hiding a file the user put there.</para>
/// </summary>
public sealed class ModService
{
    private readonly GamePaths _paths;

    private static readonly Regex TomlPair = new(
        @"^\s*(?<key>[A-Za-z_][A-Za-z0-9_.-]*)\s*=\s*(?<value>.+?)\s*$",
        RegexOptions.Compiled);

    public ModService(GamePaths paths) => _paths = paths;

    public string ModsDir(string versionId) => _paths.ModsDir(versionId);

    // ---------------------------------------------------------------- listing ---

    public List<ModEntry> List(string versionId)
    {
        var result = new List<ModEntry>();
        string dir = ModsDir(versionId);
        if (!Directory.Exists(dir)) return result;

        foreach (var file in Directory.EnumerateFiles(dir, "*.jar*", SearchOption.TopDirectoryOnly))
        {
            string name = Path.GetFileName(file);
            bool disabled;
            if (name.EndsWith(".jar", StringComparison.OrdinalIgnoreCase)) disabled = false;
            else if (name.EndsWith(".jar.disabled", StringComparison.OrdinalIgnoreCase)) disabled = true;
            else continue;

            var entry = new ModEntry
            {
                FileName = name,
                FullPath = file,
                SizeBytes = new FileInfo(file).Length,
                IsEnabled = !disabled
            };

            try
            {
                ReadMetadata(entry, file);
            }
            catch (Exception ex)
            {
                entry.ParseError = ex.Message;
            }

            result.Add(entry);
        }

        return result
            .OrderByDescending(m => m.IsEnabled)
            .ThenBy(m => m.Title, StringComparer.CurrentCultureIgnoreCase)
            .ToList();
    }

    internal static void ReadMetadata(ModEntry entry, string jarPath)
    {
        using var zip = ZipFile.OpenRead(jarPath);

        var fabric = Find(zip, "fabric.mod.json");
        if (fabric != null)
        {
            ReadFabric(entry, fabric);
            return;
        }

        var forge = Find(zip, "META-INF/mods.toml") ?? Find(zip, "META-INF/neoforge.mods.toml");
        if (forge != null)
        {
            ReadForge(entry, forge);
            return;
        }

        var legacy = Find(zip, "mcmod.info");
        if (legacy != null)
        {
            ReadLegacy(entry, legacy);
            return;
        }

        // No recognised metadata: still guess OptiFine, which ships its classes
        // at the jar root under optifine/.
        string fileName = Path.GetFileName(jarPath);
        if (fileName.Contains("OptiFine", StringComparison.OrdinalIgnoreCase))
        {
            entry.Platform = ModPlatform.OptiFine;
            entry.DisplayName = "OptiFine";
            entry.ModId = "optifine";
        }
    }

    private static ZipArchiveEntry? Find(ZipArchive zip, string path)
    {
        foreach (var e in zip.Entries)
            if (string.Equals(e.FullName, path, StringComparison.OrdinalIgnoreCase))
                return e;
        return null;
    }

    private static void ReadFabric(ModEntry entry, ZipArchiveEntry node)
    {
        using var stream = node.Open();
        using var doc = JsonDocument.Parse(stream);
        var root = doc.RootElement;

        entry.Platform = ModPlatform.Fabric;
        entry.ModId = Str(root, "id");
        entry.DisplayName = Str(root, "name");
        entry.ModVersion = Str(root, "version");
        entry.Authors = JoinAuthors(root);
        entry.Description = Str(root, "description");
        entry.Depends.AddRange(ReadFabricDepends(root));

        // Quilt builds declare "quilt_loader" instead of (or next to) fabric-loader.
        if (entry.ModId.Length == 0) entry.ModId = Path.GetFileNameWithoutExtension(entry.FileName);
    }

    private static string JoinAuthors(JsonElement root)
    {
        if (!root.TryGetProperty("authors", out var authors)) return "";
        if (authors.ValueKind == JsonValueKind.Array)
        {
            var names = new List<string>();
            foreach (var a in authors.EnumerateArray())
            {
                if (a.ValueKind == JsonValueKind.String) names.Add(a.GetString() ?? "");
                else if (a.ValueKind == JsonValueKind.Object) names.Add(Str(a, "name"));
            }
            return string.Join(", ", names.Where(n => !string.IsNullOrWhiteSpace(n)).Take(3));
        }
        return authors.ValueKind == JsonValueKind.String ? authors.GetString() ?? "" : "";
    }

    private static void ReadForge(ModEntry entry, ZipArchiveEntry node)
    {
        entry.Depends.AddRange(ReadForgeDepends(node));

        using var reader = new StreamReader(node.Open(), Encoding.UTF8);
        string? modId = null, display = null, version = null, authors = null;
        bool inMods = false, firstBlockDone = false;

        string? line;
        while ((line = reader.ReadLine()) != null)
        {
            string trimmed = line.Trim();
            if (trimmed.Length == 0 || trimmed.StartsWith('#')) continue;

            if (trimmed.StartsWith("[[", StringComparison.Ordinal))
            {
                // Only the first [[mods]] block describes the jar itself; later
                // blocks are bundled dependencies.
                if (trimmed.Replace(" ", "") == "[[mods]]")
                {
                    if (firstBlockDone) break;
                    inMods = true;
                    firstBlockDone = true;
                }
                else if (inMods)
                {
                    break;
                }
                continue;
            }

            if (!inMods) continue;

            var m = TomlPair.Match(line);
            if (!m.Success) continue;

            string key = m.Groups["key"].Value;
            string value = Unquote(m.Groups["value"].Value);
            switch (key)
            {
                case "modId": modId = value; break;
                case "displayName": display = value; break;
                case "version": version = value; break;
                case "authors": authors = value; break;
            }
        }

        entry.Platform = string.Equals(modId, "optifine", StringComparison.OrdinalIgnoreCase)
            ? ModPlatform.OptiFine
            : ModPlatform.Forge;
        entry.ModId = modId ?? "";
        entry.DisplayName = display ?? modId ?? "";
        entry.ModVersion = CleanForgeVersion(version ?? "");
        entry.Authors = authors ?? "";
    }

    /// <summary>Forge templates often write ${file.jarVersion}; that is not a version.</summary>
    private static string CleanForgeVersion(string version) =>
        version.Contains("${", StringComparison.Ordinal) ? "" : version;

    private static void ReadLegacy(ModEntry entry, ZipArchiveEntry node)
    {
        using var stream = node.Open();
        using var doc = JsonDocument.Parse(stream);
        if (doc.RootElement.ValueKind != JsonValueKind.Array) return;
        var first = doc.RootElement.EnumerateArray().FirstOrDefault();
        if (first.ValueKind != JsonValueKind.Object) return;

        entry.Platform = ModPlatform.Forge;
        entry.ModId = Str(first, "modid");
        entry.DisplayName = Str(first, "name");
        entry.ModVersion = Str(first, "version");
        entry.Authors = Str(first, "authorList");
        entry.Description = Str(first, "description");
    }

    private static string Str(JsonElement obj, string name)
    {
        if (!obj.TryGetProperty(name, out var v)) return "";
        return v.ValueKind switch
        {
            JsonValueKind.String => v.GetString() ?? "",
            JsonValueKind.Array => string.Join(", ", v.EnumerateArray()
                .Where(x => x.ValueKind == JsonValueKind.String)
                .Select(x => x.GetString())),
            _ => ""
        };
    }

    private static string Unquote(string raw)
    {
        string s = raw.Trim();
        // Strip a trailing comment that is outside quotes.
        if (s.StartsWith('"') || s.StartsWith('\''))
        {
            char quote = s[0];
            int end = s.LastIndexOf(quote);
            if (end > 0) s = s[1..end];
        }
        else
        {
            int hash = s.IndexOf('#');
            if (hash > 0) s = s[..hash].TrimEnd();
        }
        return s.Trim();
    }

    // ---------------------------------------------------------------- editing ---

    /// <summary>Renames between <c>.jar</c> and <c>.jar.disabled</c>.</summary>
    public ModEntry SetEnabled(string versionId, ModEntry entry, bool enabled)
    {
        string dir = ModsDir(versionId);
        string current = Path.Combine(dir, entry.FileName);
        if (!File.Exists(current))
            throw new FileNotFoundException($"找不到模组文件：{entry.FileName}", current);

        string target;
        if (entry.FileName.EndsWith(".jar.disabled", StringComparison.OrdinalIgnoreCase))
        {
            string stem = entry.FileName[..^".disabled".Length];
            target = enabled ? stem : entry.FileName;
        }
        else
        {
            target = enabled ? entry.FileName : entry.FileName + ".disabled";
        }

        string targetPath = Path.Combine(dir, target);
        if (string.Equals(target, entry.FileName, StringComparison.OrdinalIgnoreCase))
        {
            entry.IsEnabled = enabled;
            return entry;
        }

        File.Move(current, targetPath, overwrite: false);

        // Mutate in place: the list item is bound to this very object, and
        // swapping instances would drop the UI's selection.
        entry.FileName = target;
        entry.FullPath = targetPath;
        entry.IsEnabled = enabled;
        entry.NotifyFileNameChanged();
        return entry;
    }

    /// <summary>
    /// Copies a jar into the version's mods folder. Name collisions get a numeric
    /// suffix instead of silently overwriting somebody's file.
    /// </summary>
    public ModEntry Import(string versionId, string sourcePath)
    {
        if (!File.Exists(sourcePath))
            throw new FileNotFoundException("找不到要导入的文件。", sourcePath);
        if (!sourcePath.EndsWith(".jar", StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("只能导入 .jar 模组文件。");

        // Reject anything that is not a readable zip before it lands in mods/.
        // ZipFile.OpenRead throws InvalidDataException with an English message about
        // "End of Central Directory" — useless to a player who picked the wrong file.
        try
        {
            using var probe = ZipFile.OpenRead(sourcePath);
            if (probe.Entries.Count == 0)
                throw new InvalidOperationException("这个 jar 是空的，不像一个模组。");
        }
        catch (InvalidDataException)
        {
            throw new InvalidOperationException(
                $"「{Path.GetFileName(sourcePath)}」不是有效的 jar/zip 文件，可能下载不完整或被改过扩展名。");
        }

        string dir = ModsDir(versionId);
        Directory.CreateDirectory(dir);

        string name = Path.GetFileName(sourcePath);
        string dest = Path.Combine(dir, name);
        int n = 2;
        while (File.Exists(dest) || File.Exists(dest + ".disabled"))
        {
            string stem = Path.GetFileNameWithoutExtension(name);
            dest = Path.Combine(dir, $"{stem} ({n}).jar");
            n++;
        }

        File.Copy(sourcePath, dest, overwrite: false);

        var entry = new ModEntry
        {
            FileName = Path.GetFileName(dest),
            FullPath = dest,
            SizeBytes = new FileInfo(dest).Length,
            IsEnabled = true
        };
        try { ReadMetadata(entry, dest); }
        catch (Exception ex)
        {
            entry.ParseError = ex.Message;
            ErrorReporter.ReportNonFatal("mod", $"无法读取 {entry.FileName} 的元数据", ex.ToString());
        }
        return entry;
    }

    /// <summary>
    /// Moves a jar to <c>mods/.ecl-trash</c> instead of deleting it. A launcher
    /// should not be the reason someone loses a 300 MB modpack jar for good.
    /// </summary>
    public void MoveToTrash(string versionId, ModEntry entry)
    {
        string dir = ModsDir(versionId);
        string current = Path.Combine(dir, entry.FileName);
        if (!File.Exists(current)) return;

        string trash = _paths.ModsTrashDir(versionId);
        Directory.CreateDirectory(trash);

        string stamped = $"{DateTime.Now:yyyyMMdd-HHmmss}-{entry.FileName}";
        File.Move(current, Path.Combine(trash, stamped), overwrite: true);
    }

    public int CountEnabled(string versionId) => List(versionId).Count(m => m.IsEnabled);

    /// <summary>fabric.mod.json "depends": { "cloth-config": ">=11.0.0" } (value may be an array).</summary>
    private static IEnumerable<ModDependency> ReadFabricDepends(JsonElement root)
    {
        if (!root.TryGetProperty("depends", out var depends) ||
            depends.ValueKind != JsonValueKind.Object) yield break;

        foreach (var prop in depends.EnumerateObject())
        {
            if (PlatformMods.IsPlatform(prop.Name)) continue;

            string requirement = prop.Value.ValueKind switch
            {
                JsonValueKind.String => prop.Value.GetString() ?? "",
                JsonValueKind.Array => string.Join(" || ", prop.Value.EnumerateArray()
                    .Where(v => v.ValueKind == JsonValueKind.String)
                    .Select(v => v.GetString() ?? "")
                    .Where(s => s.Length > 0)),
                _ => ""
            };

            yield return new ModDependency(prop.Name, requirement, true);
        }
    }

    /// <summary>
    /// forge mods.toml: one [[dependencies.&lt;modid&gt;]] block per dependency with
    /// modId / mandatory / versionRange keys.
    /// </summary>
    private static IEnumerable<ModDependency> ReadForgeDepends(ZipArchiveEntry node)
    {
        var list = new List<ModDependency>();
        try
        {
            using var reader = new StreamReader(node.Open(), Encoding.UTF8);
            string? modId = null, range = null;
            bool mandatory = true, inBlock = false;

            void Flush()
            {
                if (!inBlock || string.IsNullOrWhiteSpace(modId)) return;
                if (!PlatformMods.IsPlatform(modId) && mandatory)
                    list.Add(new ModDependency(modId!, range ?? "", true));
                modId = null; range = null; mandatory = true; inBlock = false;
            }

            string? line;
            while ((line = reader.ReadLine()) != null)
            {
                string t = line.Trim();
                if (t.Length == 0 || t.StartsWith('#')) continue;

                if (t.StartsWith("[[", StringComparison.Ordinal))
                {
                    Flush();
                    inBlock = t.Replace(" ", "").StartsWith("[[dependencies.", StringComparison.OrdinalIgnoreCase);
                    continue;
                }

                if (!inBlock) continue;

                int eq = t.IndexOf('=');
                if (eq <= 0) continue;

                string key = t[..eq].Trim().Trim('"');
                string value = t[(eq + 1)..].Trim().Trim('"');

                if (key.Equals("modId", StringComparison.OrdinalIgnoreCase)) modId = value;
                else if (key.Equals("versionRange", StringComparison.OrdinalIgnoreCase)) range = value;
                else if (key.Equals("mandatory", StringComparison.OrdinalIgnoreCase))
                    mandatory = !value.Equals("false", StringComparison.OrdinalIgnoreCase);
            }

            Flush();
        }
        catch
        {
            // A malformed toml must not stop the mod from being listed.
        }

        return list;
    }

    /// <summary>
    /// Reports missing dependencies for a set of mods: "cloth-config (&gt;=11.0.0) ← sodium".
    /// Only mandatory, non-platform ids count; an empty result means everything resolves.
    /// </summary>
    public static List<string> FindMissingDependencies(IReadOnlyList<ModEntry> mods) =>
        FindMissingDependenciesDetailed(mods).Select(m => m.Detail).ToList();

    /// <summary>Same check, structured — the dependency prompt needs the mod id to offer an install.</summary>
    public static List<MissingDependency> FindMissingDependenciesDetailed(IReadOnlyList<ModEntry> mods)
    {
        var enabled = mods.Where(m => m.IsEnabled).ToList();
        var present = new HashSet<string>(
            enabled.Select(m => m.ModId).Where(id => id.Length > 0),
            StringComparer.OrdinalIgnoreCase);

        var missing = new List<MissingDependency>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var mod in enabled)
        {
            foreach (var dep in mod.Depends)
            {
                if (PlatformMods.IsPlatform(dep.ModId)) continue;
                if (present.Contains(dep.ModId)) continue;
                if (!seen.Add(dep.ModId)) continue;

                missing.Add(new MissingDependency(dep.ModId, dep.Requirement, mod.DisplayName));
            }
        }

        return missing;
    }

}
