// SPDX-License-Identifier: MPL-2.0
// Copyright (c) 2026 ChenchenMC <https://github.com/ChenchenMC-cn/EternityCraftLauncher>
using System.IO;
using System.Security.Cryptography;
using System.Threading.Tasks;
using ECL.Models;

namespace ECL.Services;

/// <summary>One file that failed verification.</summary>
public sealed record VerifyIssue(string Path, string Kind, string Detail)
{
    public string KindLabel => Kind switch
    {
        "missing" => "缺失",
        "size" => "大小不符",
        "hash" => "内容损坏",
        _ => Kind
    };

    /// <summary>Relative-ish path for display: keep the last three segments.</summary>
    public string ShortPath
    {
        get
        {
            string[] parts = Path.Replace('/', '\\').Split('\\', StringSplitOptions.RemoveEmptyEntries);
            return parts.Length <= 3 ? Path : string.Join('\\', parts[^3..]);
        }
    }
}

public sealed record VerifyReport(
    int Checked,
    int Ok,
    long Bytes,
    IReadOnlyList<VerifyIssue> Issues)
{
    public int Missing => Issues.Count(i => i.Kind == "missing");
    public int Corrupt => Issues.Count(i => i.Kind != "missing");
    public bool Healthy => Issues.Count == 0;

    public string Summary => Healthy
        ? $"全部正常（{Checked} 个文件，{Bytes / 1024.0 / 1024.0:0.0} MB）"
        : $"{Missing} 个缺失，{Corrupt} 个损坏（共检查 {Checked} 个）";
}

/// <summary>
/// Result of the cheap pre-launch sweep: are the files there, and roughly the right
/// size? No hashing, no network — this has to finish in the time a player is willing
/// to wait after clicking Play.
/// </summary>
public sealed record QuickCheck(int Checked, int Missing, long MissingBytes, IReadOnlyList<string> Samples)
{
    public bool Healthy => Missing == 0;

    public string FirstSample => Samples.Count > 0 ? Samples[0] : "";
}

/// <summary>
/// Verifies an installed version against its own json manifest and repairs what is
/// missing. Both operations work offline-except-download: the plan is built from the
/// local version file, so no manifest lookup is needed.
/// </summary>
public sealed class VersionVerifier
{
    private readonly GamePaths _paths;
    private readonly LauncherSettings _settings;
    private readonly ApiClient _api;

    public VersionVerifier(GamePaths paths, LauncherSettings settings, ApiClient api)
    {
        _paths = paths;
        _settings = settings;
        _api = api;
    }

    /// <summary>
    /// Existence and size sweep over everything the version expects.
    ///
    /// <para>A profile can carry its json and jar while every library and asset is
    /// gone — that is what a folder copied from another machine looks like, and what
    /// a half-finished download looks like. Java then dies within a second with
    /// <c>NoClassDefFoundError</c> and it reads as a launcher crash, so launching has
    /// to check first.</para>
    /// </summary>
    public async Task<QuickCheck> QuickCheckAsync(string versionId, CancellationToken ct)
    {
        var install = new InstallService(_settings, _paths, _api);
        var (plan, _) = await install.BuildPlanFromLocalAsync(versionId, null, ct).ConfigureAwait(false);

        int missing = 0;
        long missingBytes = 0;
        var samples = new System.Collections.Concurrent.ConcurrentQueue<string>();

        // 3627 stats in a row took 416 ms cold (NTFS + antivirus); the work is pure
        // metadata lookups, so it parallelises cleanly. Order no longer matters because
        // only counts and a few sample paths come out.
        Parallel.ForEach(plan.Files,
            new ParallelOptions { CancellationToken = ct, MaxDegreeOfParallelism = 8 },
            item =>
            {
                var info = new FileInfo(item.Destination);

                // Old-format libraries carry no size, so presence is all we can ask for
                // here; VerifyAsync hashes those when the user asks for a real check.
                bool bad = !info.Exists || info.Length == 0 ||
                           (item.ExpectedSize > 0 && info.Length != item.ExpectedSize);

                if (!bad) return;

                Interlocked.Increment(ref missing);
                Interlocked.Add(ref missingBytes, Math.Max(0, item.ExpectedSize));
                if (samples.Count < 5) samples.Enqueue(item.Destination);
            });

        return new QuickCheck(plan.Files.Count, missing, missingBytes, samples.ToArray());
    }

    /// <summary>
    /// Hashes every file the version expects. Size is checked first because it is
    /// free; a hash only runs when the size matches, which is the common case.
    /// </summary>
    public async Task<VerifyReport> VerifyAsync(string versionId,
        IProgress<string>? log, IProgress<double>? progress, CancellationToken ct)
    {
        var install = new InstallService(_settings, _paths, _api);
        var (plan, _) = await install.BuildPlanFromLocalAsync(versionId, log, ct).ConfigureAwait(false);

        log?.Report($"开始校验 {versionId}：{plan.Files.Count} 个文件");

        int ok = 0;
        long bytes = 0;
        var issues = new List<VerifyIssue>();
        int done = 0;

        foreach (var item in plan.Files)
        {
            ct.ThrowIfCancellationRequested();
            done++;

            if (done % 25 == 0)
            {
                progress?.Report((double)done / plan.Files.Count);
                log?.Report($"  已检查 {done} / {plan.Files.Count}");
            }

            string path = item.Destination;

            if (!File.Exists(path))
            {
                issues.Add(new VerifyIssue(path, "missing", ""));
                continue;
            }

            var info = new FileInfo(path);
            if (item.ExpectedSize > 0 && info.Length != item.ExpectedSize)
            {
                issues.Add(new VerifyIssue(path, "size", $"{info.Length} 字节，应为 {item.ExpectedSize}"));
                continue;
            }

            if (item.Sha1.Length > 0)
            {
                string actual = await HashAsync(path, ct).ConfigureAwait(false);
                if (!actual.Equals(item.Sha1, StringComparison.OrdinalIgnoreCase))
                {
                    issues.Add(new VerifyIssue(path, "hash",
                        $"{actual[..Math.Min(8, actual.Length)]}… 应为 {item.Sha1[..Math.Min(8, item.Sha1.Length)]}…"));
                    continue;
                }
            }

            ok++;
            bytes += info.Length;
        }

        progress?.Report(1);
        var report = new VerifyReport(plan.Files.Count, ok, bytes, issues);

        log?.Report($"校验完成：{report.Summary}");

        int distinctMissing = issues.Where(i => i.Kind == "missing")
                                    .Select(i => i.Path)
                                    .Distinct(StringComparer.OrdinalIgnoreCase).Count();
        log?.Report($"缺失去重后：{distinctMissing} 个唯一路径（原始 {issues.Count(i => i.Kind == "missing")} 条）");

        foreach (var issue in issues.Take(12))
            log?.Report($"  [{issue.KindLabel}] {issue.Path} {issue.Detail}");
        if (issues.Count > 12) log?.Report($"  …另有 {issues.Count - 12} 项，见报告");

        return report;
    }

    /// <summary>
    /// Re-runs the install plan for an existing version. The native downloader skips
    /// files that already hash correctly (reported as cache hits), so this only
    /// fetches what is missing or corrupt.
    /// </summary>
    /// <returns>How many files the plan covered (already-correct ones are skipped by
    /// the native downloader, not by this method).</returns>
    public async Task<int> RepairAsync(string versionId,
        IProgress<InstallProgress>? progress, IProgress<string>? log, CancellationToken ct)
    {
        var install = new InstallService(_settings, _paths, _api);
        var (plan, _) = await install.BuildPlanFromLocalAsync(versionId, log, ct).ConfigureAwait(false);

        log?.Report($"补全 {versionId}：计划核对 {plan.Files.Count} 个文件（已存在的会跳过）");
        await install.ExecuteAsync(plan, progress, log, ct).ConfigureAwait(false);

        return plan.Files.Count;
    }

    private static async Task<string> HashAsync(string path, CancellationToken ct)
    {
        await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read,
            FileShare.Read, 128 * 1024, useAsync: true);

        byte[] hash = await SHA1.HashDataAsync(stream, ct).ConfigureAwait(false);
        return Convert.ToHexString(hash).ToLowerInvariant();
    }
}
