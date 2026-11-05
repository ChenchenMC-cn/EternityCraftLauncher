// SPDX-License-Identifier: MPL-2.0
// Copyright (c) 2026 ChenchenMC <https://github.com/ChenchenMC-cn/EternityCraftLauncher>
using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;
using ECL.Models;
using ECL.ViewModels;

namespace ECL.Services;

public enum LoaderKind
{
    Vanilla = 0,
    Fabric = 1,
    Quilt = 2,
    Forge = 3,
    OptiFine = 4
}

/// <summary>Health of a directory under <c>versions/</c>.</summary>
public enum VersionHealth
{
    /// <summary>json parses and a client jar (own or inherited) exists — launchable.</summary>
    Ready = 0,
    /// <summary>json exists but no jar can be found.</summary>
    MissingJar = 1,
    /// <summary>Directory exists but carries no version json at all.</summary>
    MissingJson = 2,
    /// <summary>json exists but cannot be parsed.</summary>
    BrokenJson = 3
}

/// <summary>
/// One directory under <c>versions/</c>, whatever state it is in. This is the
/// shape written to <c>eclversions.config</c>, so it also covers profiles other
/// launchers left behind — ECL should recognise them instead of pretending the
/// folder is empty.
/// </summary>
public sealed class VersionProbe
{
    [JsonPropertyName("id")] public string Id { get; init; } = "";
    [JsonPropertyName("health")] public string Health { get; init; } = "ready";
    [JsonPropertyName("kind")] public string Kind { get; init; } = "vanilla";
    [JsonPropertyName("baseVersion")] public string BaseVersion { get; init; } = "";
    [JsonPropertyName("loaderVersion")] public string LoaderVersion { get; init; } = "";
    [JsonPropertyName("type")] public string? Type { get; init; }
    [JsonPropertyName("mainClass")] public string? MainClass { get; init; }
    [JsonPropertyName("inheritsFrom")] public string? InheritsFrom { get; init; }
    [JsonPropertyName("hasJar")] public bool HasJar { get; init; }
    [JsonPropertyName("hasJson")] public bool HasJson { get; init; }
    [JsonPropertyName("installedByEcl")] public bool InstalledByEcl { get; init; }
    [JsonPropertyName("modCount")] public int ModCount { get; init; }
    [JsonPropertyName("lastWrite")] public DateTime LastWrite { get; init; }

    /// <summary>Parsed profile, kept in memory so <see cref="InstalledVersionScanner.Scan"/>
    /// does not have to read every json twice. Never serialised.</summary>
    [JsonIgnore] public VersionDetail? Detail { get; init; }
}

/// <summary>A version that is fully installed and ready to launch.</summary>
public sealed class InstalledVersion : ObservableObject
{
    private bool _isSelected;

    public required string Id { get; init; }
    public required string BaseVersion { get; init; }
    public LoaderKind Kind { get; init; }
    public string LoaderVersion { get; init; } = "";
    public string DisplayName { get; init; } = "";
    public string SubText { get; init; } = "";
    public DateTime LastUsed { get; init; }
    public int ModCount { get; init; }

    public string KindLabel => Kind switch
    {
        LoaderKind.Fabric => "FABRIC",
        LoaderKind.Quilt => "QUILT",
        LoaderKind.Forge => "FORGE",
        LoaderKind.OptiFine => "OPTIFINE",
        _ => Localization.T("原版")
    };

    public string KindBadgeForeground => Kind switch
    {
        LoaderKind.Fabric => "#F5B942",
        LoaderKind.Quilt => "#B98CFF",
        LoaderKind.Forge => "#8FA6FF",
        LoaderKind.OptiFine => "#FF9E64",
        _ => "#3DDC97"
    };

    /// <summary>Shown on the home card; empty when the profile has no mods.</summary>
    public string ModsBadge => ModCount > 0 ? $"{ModCount} 模组" : "";

    public bool IsSelected
    {
        get => _isSelected;
        set => SetProperty(ref _isSelected, value);
    }

    public override string ToString() => DisplayName;
}

public static class InstalledVersionScanner
{
    private static readonly JsonSerializerOptions JsonOpts = new() { PropertyNameCaseInsensitive = true };

    /// <summary>Where the probe result is mirrored for humans and other tools.</summary>
    public static string CatalogPath(GamePaths paths) => Path.Combine(paths.Root, "eclversions.config");

    /// <summary>
    /// Walks every directory under <c>versions/</c> and reports what is actually
    /// there — including half-installed folders and profiles created by other
    /// launchers. Never throws: a broken profile must not break the list.
    /// </summary>
    public static List<VersionProbe> Probe(GamePaths paths)
    {
        var result = new List<VersionProbe>();
        if (!Directory.Exists(paths.VersionsDir)) return result;

        foreach (string dir in Directory.EnumerateDirectories(paths.VersionsDir))
        {
            string id = Path.GetFileName(dir);
            try
            {
                // Most launchers name the json after the folder; fall back to any
                // single json so a renamed profile is still recognised.
                string jsonPath = Path.Combine(dir, id + ".json");
                if (!File.Exists(jsonPath))
                {
                    jsonPath = Directory.EnumerateFiles(dir, "*.json", SearchOption.TopDirectoryOnly)
                                        .FirstOrDefault() ?? jsonPath;
                }

                bool hasJson = File.Exists(jsonPath);
                bool hasJar = File.Exists(Path.Combine(dir, id + ".jar"));
                bool eclInstalled = File.Exists(Path.Combine(dir, ".installed"));
                DateTime stamp = File.Exists(jsonPath) ? File.GetLastWriteTime(jsonPath)
                                                       : Directory.GetLastWriteTime(dir);

                if (!hasJson)
                {
                    result.Add(new VersionProbe
                    {
                        Id = id, Health = "missing-json", HasJar = hasJar,
                        InstalledByEcl = eclInstalled, LastWrite = stamp
                    });
                    continue;
                }

                VersionDetail? detail = null;
                try { detail = JsonSerializer.Deserialize<VersionDetail>(File.ReadAllText(jsonPath), JsonOpts); }
                catch { /* handled below */ }

                if (detail == null)
                {
                    result.Add(new VersionProbe
                    {
                        Id = id, Health = "broken-json", HasJson = true, HasJar = hasJar,
                        InstalledByEcl = eclInstalled, LastWrite = stamp
                    });
                    continue;
                }

                var (kind, loaderVersion) = Detect(detail, id);
                string baseVersion = string.IsNullOrEmpty(detail.InheritsFrom) ? id : detail.InheritsFrom;
                int ofIdx = id.IndexOf("-OptiFine", StringComparison.OrdinalIgnoreCase);
                if (ofIdx > 0) baseVersion = id[..ofIdx];

                // A modded profile usually has no jar of its own; the parent does.
                bool jarUsable = hasJar ||
                    (!string.IsNullOrEmpty(detail.InheritsFrom) &&
                     File.Exists(Path.Combine(paths.VersionsDir, detail.InheritsFrom,
                                              detail.InheritsFrom + ".jar")));

                result.Add(new VersionProbe
                {
                    Id = id,
                    Health = jarUsable ? "ready" : "missing-jar",
                    Kind = kind.ToString().ToLowerInvariant(),
                    BaseVersion = baseVersion,
                    LoaderVersion = loaderVersion,
                    Type = detail.Type,
                    MainClass = detail.MainClass,
                    InheritsFrom = detail.InheritsFrom,
                    HasJar = hasJar,
                    HasJson = true,
                    InstalledByEcl = eclInstalled,
                    ModCount = CountMods(paths, id),
                    LastWrite = stamp,
                    Detail = detail
                });
            }
            catch
            {
                // Never let one bad profile break the sweep.
            }
        }

        return result.OrderBy(v => v.Id, StringComparer.OrdinalIgnoreCase).ToList();
    }

    /// <summary>
    /// Mirrors the probe result into <c>eclversions.config</c> at the data root.
    /// Best effort: a read-only or locked folder must never break start-up.
    /// </summary>
    public static void SaveCatalog(GamePaths paths, IReadOnlyList<VersionProbe> probes)
    {
        try
        {
            var doc = new
            {
                generatedAt = DateTimeOffset.Now.ToString("O"),
                dataRoot = paths.Root,
                scanned = probes.Count,
                ready = probes.Count(p => p.Health == "ready"),
                versions = probes
            };

            string json = JsonSerializer.Serialize(doc, new JsonSerializerOptions
            {
                WriteIndented = true,
                Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping
            });

            File.WriteAllText(CatalogPath(paths), json);
        }
        catch
        {
            // Diagnostics only.
        }
    }

    public static List<InstalledVersion> Scan(GamePaths paths)
    {
        var result = new List<InstalledVersion>();

        foreach (var probe in Probe(paths))
        {
            if (probe.Health != "ready" || probe.Detail == null) continue;

            var (display, sub) = Describe(probe.Id, LoaderKindOf(probe.Kind), probe.LoaderVersion, probe.Detail);

            DateTime stamp = probe.LastWrite;
            try
            {
                string marker = Path.Combine(paths.VersionDir(probe.Id), ".installed");
                if (File.Exists(marker))
                {
                    string text = File.ReadAllText(marker).Trim();
                    if (DateTime.TryParse(text, out var dt)) stamp = dt;
                }
            }
            catch
            {
                // keep the file timestamp
            }

            result.Add(new InstalledVersion
            {
                Id = probe.Id,
                BaseVersion = probe.BaseVersion,
                Kind = LoaderKindOf(probe.Kind),
                LoaderVersion = probe.LoaderVersion,
                DisplayName = display,
                SubText = probe.ModCount > 0 ? $"{sub}  ·  {probe.ModCount} 模组" : sub,
                LastUsed = stamp,
                ModCount = probe.ModCount
            });
        }

        return result.OrderByDescending(v => v.BaseVersion, VersionComparer.Instance)
                     .ThenBy(v => v.Kind)
                     .ThenByDescending(v => v.LoaderVersion, VersionComparer.Instance)
                     .ToList();
    }

    private static LoaderKind LoaderKindOf(string kind) => kind switch
    {
        "fabric" => LoaderKind.Fabric,
        "quilt" => LoaderKind.Quilt,
        "forge" => LoaderKind.Forge,
        "optifine" => LoaderKind.OptiFine,
        _ => LoaderKind.Vanilla
    };

    /// <summary>Enabled mod jars in this version's own mods folder.</summary>
    private static int CountMods(GamePaths paths, string id)
    {
        try
        {
            string dir = paths.ModsDir(id);
            if (!Directory.Exists(dir)) return 0;
            return Directory.EnumerateFiles(dir, "*.jar", SearchOption.TopDirectoryOnly).Count();
        }
        catch
        {
            return 0;
        }
    }

    private static (LoaderKind Kind, string LoaderVersion) Detect(VersionDetail detail, string id)
    {
        string main = detail.MainClass ?? "";

        if (id.Contains("OptiFine", StringComparison.OrdinalIgnoreCase))
        {
            // 1.20.1-OptiFine_1.20.1_HD_U_I6
            int idx = id.IndexOf("OptiFine", StringComparison.OrdinalIgnoreCase);
            string ed = id[(idx + "OptiFine".Length)..].TrimStart('_', '-');
            // Some builds repeat the Minecraft version inside the folder name
            // ("1.20.1-OptiFine_1.20.1_HD_U_I6"); drop that leading segment only
            // when it actually looks like a version.
            int sep = ed.IndexOf('_');
            if (sep > 0 && ed.Length > sep + 1 && char.IsDigit(ed[0])) ed = ed[(sep + 1)..];
            return (LoaderKind.OptiFine, ed);
        }

        if (main.Contains("quilt", StringComparison.OrdinalIgnoreCase) ||
            id.StartsWith("quilt-loader-", StringComparison.OrdinalIgnoreCase))
        {
            string rest = id.StartsWith("quilt-loader-", StringComparison.OrdinalIgnoreCase)
                ? id["quilt-loader-".Length..]
                : "";
            int dash = rest.LastIndexOf('-');
            return (LoaderKind.Quilt, dash > 0 ? rest[..dash] : "");
        }

        if (main.Contains("fabric", StringComparison.OrdinalIgnoreCase) ||
            id.StartsWith("fabric-loader-", StringComparison.OrdinalIgnoreCase))
        {
            // fabric-loader-0.16.9-1.20.1
            string rest = id.StartsWith("fabric-loader-", StringComparison.OrdinalIgnoreCase)
                ? id["fabric-loader-".Length..]
                : "";
            int dash = rest.LastIndexOf('-');
            string loader = dash > 0 ? rest[..dash] : "";
            return (LoaderKind.Fabric, loader);
        }

        if (main.Contains("bootstraplauncher", StringComparison.OrdinalIgnoreCase) ||
            main.Contains("forge", StringComparison.OrdinalIgnoreCase) ||
            main.Contains("fml", StringComparison.OrdinalIgnoreCase) ||
            id.Contains("-forge", StringComparison.OrdinalIgnoreCase))
        {
            // Modern: "1.20.1-forge-47.2.0"
            int modern = id.IndexOf("-forge-", StringComparison.OrdinalIgnoreCase);
            if (modern >= 0) return (LoaderKind.Forge, id[(modern + "-forge-".Length)..]);

            // Legacy client install: "1.7.10-Forge10.13.4.1614-1.7.10" — no dash
            // between "Forge" and the build, and the MC version is repeated at the
            // end. Without this the profile reads as vanilla.
            int legacy = id.IndexOf("-Forge", StringComparison.OrdinalIgnoreCase);
            if (legacy >= 0)
            {
                string rest = id[(legacy + "-Forge".Length)..].TrimStart('-', '_');
                int dash = rest.LastIndexOf('-');
                if (dash > 0 && LooksLikeMcVersion(rest[(dash + 1)..])) rest = rest[..dash];
                return (LoaderKind.Forge, rest);
            }

            return (LoaderKind.Forge, "");
        }

        return (LoaderKind.Vanilla, "");
    }

    private static (string Display, string Sub) Describe(string id, LoaderKind kind, string loaderVersion,
        VersionDetail detail)
    {
        string baseVersion = string.IsNullOrEmpty(detail.InheritsFrom) ? id : detail.InheritsFrom;
        int ofIdx = id.IndexOf("-OptiFine", StringComparison.OrdinalIgnoreCase);
        if (ofIdx > 0) baseVersion = id[..ofIdx];
        string loader = string.IsNullOrEmpty(loaderVersion) ? GuessLoaderFromId(id) : loaderVersion;

        return kind switch
        {
            LoaderKind.Fabric => ($"Fabric {loader}".Trim(), $"Minecraft {baseVersion}"),
            LoaderKind.Quilt => ($"Quilt {loader}".Trim(), $"Minecraft {baseVersion}"),
            LoaderKind.Forge => ($"Forge {loader}".Trim(), $"Minecraft {baseVersion}"),
            LoaderKind.OptiFine => ($"OptiFine {loader}".Trim(), $"Minecraft {baseVersion}"),
            _ => ($"Minecraft {id}", Localization.T("官方原版"))
        };
    }

    /// <summary>"1.7", "1.7.10", "1.20.1" — digits and one or two dots.</summary>
    private static bool LooksLikeMcVersion(string s)
    {
        if (s.Length == 0) return false;
        int dots = 0;
        foreach (char c in s)
        {
            if (c == '.') { dots++; continue; }
            if (!char.IsDigit(c)) return false;
        }
        return dots is >= 1 and <= 2;
    }

    private static string GuessLoaderFromId(string id)
    {
        int idx = id.IndexOf("-forge-", StringComparison.OrdinalIgnoreCase);
        return idx >= 0 ? id[(idx + "-forge-".Length)..] : id;
    }
}

/// <summary>Loose version comparison so "1.21" sorts above "1.9".</summary>
public sealed class VersionComparer : IComparer<string>
{
    public static readonly VersionComparer Instance = new();

    public int Compare(string? x, string? y)
    {
        if (ReferenceEquals(x, y)) return 0;
        if (x == null) return -1;
        if (y == null) return 1;

        var xs = Tokenize(x);
        var ys = Tokenize(y);
        int n = Math.Min(xs.Count, ys.Count);
        for (int i = 0; i < n; i++)
        {
            var (ax, bx) = (xs[i], ys[i]);
            int cmp = ax.Kind == bx.Kind
                ? (ax.Kind == 0 ? ax.Num.CompareTo(bx.Num) : string.CompareOrdinal(ax.Text, bx.Text))
                : ax.Kind.CompareTo(bx.Kind);
            if (cmp != 0) return cmp;
        }
        return xs.Count.CompareTo(ys.Count);
    }

    private static List<(int Kind, int Num, string Text)> Tokenize(string s)
    {
        var list = new List<(int, int, string)>();
        int i = 0;
        while (i < s.Length)
        {
            if (char.IsDigit(s[i]))
            {
                int start = i;
                while (i < s.Length && char.IsDigit(s[i])) i++;
                list.Add((0, int.Parse(s[start..i]), ""));
            }
            else
            {
                int start = i;
                while (i < s.Length && !char.IsDigit(s[i])) i++;
                list.Add((1, 0, s[start..i]));
            }
        }
        return list;
    }
}
