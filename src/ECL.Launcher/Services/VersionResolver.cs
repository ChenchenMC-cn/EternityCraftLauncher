// SPDX-License-Identifier: MPL-2.0
// Copyright (c) 2026 ChenchenMC <https://github.com/ChenchenMC-cn/EternityCraftLauncher>
using System.IO;
using System.Text.Json;
using ECL.Models;

namespace ECL.Services;

/// <summary>
/// A version after all <c>inheritsFrom</c> links have been folded in.
/// <see cref="JarVersionId"/> points at the vanilla profile whose client jar is used,
/// which for Fabric/Forge is the parent version, not the modded one.
/// </summary>
public sealed class ResolvedVersion
{
    public required string Id { get; init; }
    public required VersionDetail Detail { get; init; }
    public required string JarVersionId { get; init; }
    public bool IsModded => Id != JarVersionId;
}

/// <summary>
/// Merges mod-loader profiles with their vanilla parent, following the same rules
/// the official launcher uses: child scalars win, libraries are unioned with the
/// child overriding equal names, and argument lists are concatenated parent-first.
/// </summary>
public static class VersionResolver
{
    private static readonly JsonSerializerOptions JsonOpts = new() { PropertyNameCaseInsensitive = true };

    public static async Task<ResolvedVersion> ResolveAsync(string versionId, GamePaths paths,
        IProgress<string>? log, CancellationToken ct)
        => await ResolveAsync(versionId, paths, log, ct, 0).ConfigureAwait(false);

    private static async Task<ResolvedVersion> ResolveAsync(string versionId, GamePaths paths,
        IProgress<string>? log, CancellationToken ct, int depth)
    {
        if (depth > 8)
            throw new InvalidOperationException($"版本继承链过深（{versionId}），可能存在循环继承。");

        string jsonPath = paths.VersionJson(versionId);
        if (!File.Exists(jsonPath))
            throw new InvalidOperationException($"缺少版本文件：{jsonPath}。请先安装 {versionId}。");

        string json = await File.ReadAllTextAsync(jsonPath, ct).ConfigureAwait(false);
        var detail = JsonSerializer.Deserialize<VersionDetail>(json, JsonOpts)
                     ?? throw new InvalidOperationException($"版本文件无法解析：{versionId}");

        if (string.IsNullOrEmpty(detail.InheritsFrom))
            return new ResolvedVersion { Id = versionId, Detail = detail, JarVersionId = versionId };

        string parentId = detail.InheritsFrom;
        log?.Report($"{versionId} 继承自 {parentId}，正在合并…");
        var parent = await ResolveAsync(parentId, paths, log, ct, depth + 1).ConfigureAwait(false);

        return new ResolvedVersion
        {
            Id = versionId,
            Detail = Merge(parent.Detail, detail),
            JarVersionId = parent.JarVersionId
        };
    }

    public static VersionDetail Merge(VersionDetail parent, VersionDetail child) => new()
    {
        Id = child.Id,
        Type = child.Type ?? parent.Type,
        MainClass = child.MainClass ?? parent.MainClass,
        Assets = child.Assets ?? parent.Assets,
        AssetIndex = child.AssetIndex ?? parent.AssetIndex,
        // A child that ships its own client jar overrides the parent's -- but a child
        // carrying a downloads block *without* a client (some pack profiles do) must not
        // hide the parent's jar, or the plan silently loses the client jar and the
        // repair path can never fetch it.
        Downloads = PickDownloads(parent.Downloads, child.Downloads),
        JavaVersion = child.JavaVersion ?? parent.JavaVersion,
        Logging = child.Logging ?? parent.Logging,
        MinecraftArguments = child.MinecraftArguments ?? parent.MinecraftArguments,
        Libraries = MergeLibraries(parent.Libraries, child.Libraries),
        Arguments = MergeArguments(parent.Arguments, child.Arguments),
        InheritsFrom = null
    };

    private static VersionDownloads? PickDownloads(VersionDownloads? parent, VersionDownloads? child)
    {
        if (child?.Client != null) return child;
        if (parent?.Client != null) return parent;
        return child ?? parent;
    }

    private static List<LibraryNode> MergeLibraries(List<LibraryNode> parent, List<LibraryNode> child)
    {
        var result = new List<LibraryNode>(parent.Count + child.Count);
        result.AddRange(parent);

        foreach (var lib in child)
        {
            // Hand-edited or third-party profiles sometimes carry {"name": null};
            // that used to throw NullReferenceException out of the whole resolve.
            if (string.IsNullOrWhiteSpace(lib.Name)) continue;

            // The child may pin a different VERSION of a dependency the parent also
            // declares (Forge 1.7.10 needs guava 17.0 while vanilla 1.7.10 ships
            // 15.0). Matching only the full coordinate would leave both on the
            // classpath and the wrong one wins. Match on group:artifact and let the
            // child's version replace the parent's.
            string key = GroupArtifact(lib.Name);
            int idx = result.FindIndex(l => GroupArtifact(l.Name) == key);
            if (idx >= 0) result[idx] = lib;
            else result.Add(lib);
        }
        return result;
    }

    private static string GroupArtifact(string? name)
    {
        if (string.IsNullOrEmpty(name)) return "";
        int first = name.IndexOf(':');
        if (first < 0) return name;
        int second = name.IndexOf(':', first + 1);
        return second < 0 ? name : name[..second];
    }

    private static ArgumentsNode? MergeArguments(ArgumentsNode? parent, ArgumentsNode? child)
    {
        if (parent == null) return child;
        if (child == null) return parent;

        return new ArgumentsNode
        {
            Game = Concat(parent.Game, child.Game),
            Jvm = Concat(parent.Jvm, child.Jvm)
        };

        static List<JsonElement>? Concat(List<JsonElement>? a, List<JsonElement>? b)
        {
            if (a == null) return b;
            if (b == null) return a;
            var list = new List<JsonElement>(a.Count + b.Count);
            list.AddRange(a);
            list.AddRange(b);
            return list;
        }
    }
}
