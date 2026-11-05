// SPDX-License-Identifier: MPL-2.0
// Copyright (c) 2026 ChenchenMC <https://github.com/ChenchenMC-cn/EternityCraftLauncher>
using System.Text.Json;
using System.Text.Json.Serialization;
using ECL.Services;

namespace ECL.Models;

// ---------------------------------------------------------------- manifest ---

public sealed class VersionManifest
{
    [JsonPropertyName("latest")] public LatestVersions? Latest { get; set; }
    [JsonPropertyName("versions")] public List<ManifestEntry> Versions { get; set; } = new();
}

public sealed class LatestVersions
{
    [JsonPropertyName("release")] public string? Release { get; set; }
    [JsonPropertyName("snapshot")] public string? Snapshot { get; set; }
}

public sealed class ManifestEntry
{
    [JsonPropertyName("id")] public string Id { get; set; } = "";
    [JsonPropertyName("type")] public string Type { get; set; } = "";
    [JsonPropertyName("url")] public string Url { get; set; } = "";
    [JsonPropertyName("time")] public DateTimeOffset Time { get; set; }
    [JsonPropertyName("releaseTime")] public DateTimeOffset ReleaseTime { get; set; }
}

// ------------------------------------------------------------ version json ---

public sealed class VersionDetail
{
    [JsonPropertyName("id")] public string Id { get; set; } = "";
    [JsonPropertyName("type")] public string? Type { get; set; }
    [JsonPropertyName("mainClass")] public string? MainClass { get; set; }
    [JsonPropertyName("assets")] public string? Assets { get; set; }
    [JsonPropertyName("inheritsFrom")] public string? InheritsFrom { get; set; }
    [JsonPropertyName("assetIndex")] public AssetIndexRef? AssetIndex { get; set; }
    [JsonPropertyName("downloads")] public VersionDownloads? Downloads { get; set; }
    [JsonPropertyName("javaVersion")] public JavaVersionRef? JavaVersion { get; set; }
    [JsonPropertyName("libraries")] public List<LibraryNode> Libraries { get; set; } = new();
    [JsonPropertyName("arguments")] public ArgumentsNode? Arguments { get; set; }
    [JsonPropertyName("minecraftArguments")] public string? MinecraftArguments { get; set; }
    [JsonPropertyName("logging")] public LoggingNode? Logging { get; set; }
}

public sealed class LoggingNode
{
    [JsonPropertyName("client")] public LoggingClient? Client { get; set; }
}

public sealed class LoggingClient
{
    [JsonPropertyName("argument")] public string? Argument { get; set; }
    [JsonPropertyName("file")] public LoggingFile? File { get; set; }
    [JsonPropertyName("type")] public string? Type { get; set; }
}

public sealed class LoggingFile
{
    [JsonPropertyName("id")] public string? Id { get; set; }
    [JsonPropertyName("sha1")] public string? Sha1 { get; set; }
    [JsonPropertyName("size")] public long Size { get; set; }
    [JsonPropertyName("url")] public string? Url { get; set; }
}

public sealed class AssetIndexRef
{
    [JsonPropertyName("id")] public string Id { get; set; } = "";
    [JsonPropertyName("sha1")] public string? Sha1 { get; set; }
    [JsonPropertyName("size")] public long Size { get; set; }
    [JsonPropertyName("totalSize")] public long TotalSize { get; set; }
    [JsonPropertyName("url")] public string? Url { get; set; }
}

public sealed class VersionDownloads
{
    [JsonPropertyName("client")] public DownloadArtifact? Client { get; set; }
    [JsonPropertyName("server")] public DownloadArtifact? Server { get; set; }
}

public sealed class DownloadArtifact
{
    [JsonPropertyName("path")] public string? Path { get; set; }
    [JsonPropertyName("sha1")] public string? Sha1 { get; set; }
    [JsonPropertyName("size")] public long Size { get; set; }
    [JsonPropertyName("url")] public string? Url { get; set; }
}

public sealed class JavaVersionRef
{
    [JsonPropertyName("component")] public string? Component { get; set; }
    [JsonPropertyName("majorVersion")] public int MajorVersion { get; set; }
}

public sealed class LibraryNode
{
    [JsonPropertyName("name")] public string Name { get; set; } = "";
    /// <summary>Maven repository base, used when the entry has no downloads block (Fabric style).</summary>
    [JsonPropertyName("url")] public string? Url { get; set; }
    [JsonPropertyName("downloads")] public LibraryDownloads? Downloads { get; set; }
    [JsonPropertyName("natives")] public Dictionary<string, string>? Natives { get; set; }
    [JsonPropertyName("rules")] public List<RuleNode>? Rules { get; set; }
    [JsonPropertyName("extract")] public ExtractRule? Extract { get; set; }
}

public sealed class LibraryDownloads
{
    [JsonPropertyName("artifact")] public DownloadArtifact? Artifact { get; set; }
    [JsonPropertyName("classifiers")] public Dictionary<string, DownloadArtifact>? Classifiers { get; set; }
}

public sealed class ExtractRule
{
    [JsonPropertyName("exclude")] public List<string>? Exclude { get; set; }
}

public sealed class RuleNode
{
    [JsonPropertyName("action")] public string Action { get; set; } = "allow";
    [JsonPropertyName("os")] public OsRule? Os { get; set; }
    [JsonPropertyName("features")] public Dictionary<string, bool>? Features { get; set; }
}

public sealed class OsRule
{
    [JsonPropertyName("name")] public string? Name { get; set; }
    [JsonPropertyName("arch")] public string? Arch { get; set; }
    [JsonPropertyName("version")] public string? Version { get; set; }
}

public sealed class ArgumentsNode
{
    [JsonPropertyName("game")] public List<JsonElement>? Game { get; set; }
    [JsonPropertyName("jvm")] public List<JsonElement>? Jvm { get; set; }
}

// -------------------------------------------------------------- asset index ---

public sealed class AssetIndexFile
{
    [JsonPropertyName("objects")] public Dictionary<string, AssetObject> Objects { get; set; } = new();
    [JsonPropertyName("virtual")] public bool Virtual { get; set; }
    [JsonPropertyName("map_to_resources")] public bool MapToResources { get; set; }
}

public sealed class AssetObject
{
    [JsonPropertyName("hash")] public string Hash { get; set; } = "";
    [JsonPropertyName("size")] public long Size { get; set; }
}

// ------------------------------------------------------------------ results ---

public sealed class InstallPlan
{
    public string VersionId { get; init; } = "";
    public List<NativeCore.DownloadItem> Files { get; } = new();
    public List<(string ZipPath, string DestDir)> Extracts { get; } = new();
    public long TotalBytes { get; set; }
    public int TotalFiles => Files.Count;
}
