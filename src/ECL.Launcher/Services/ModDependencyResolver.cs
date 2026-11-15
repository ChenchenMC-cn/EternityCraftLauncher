// SPDX-License-Identifier: MPL-2.0
// Copyright (c) 2026 ChenchenMC <https://github.com/ChenchenMC-cn/EternityCraftLauncher>
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace ECL.Services;

/// <summary>Outcome of walking a mod's dependency graph.</summary>
/// <param name="Order">Versions to install, dependencies first.</param>
/// <param name="Missing">Requirements that could not be satisfied.</param>
public sealed record DependencyPlan(
    IReadOnlyList<ModrinthVersion> Order,
    IReadOnlyList<string> Missing);

/// <summary>
/// Walks Modrinth's <c>dependencies</c> graph and produces an install order.
///
/// <para>Deliberately free of HTTP: the caller passes a fetch delegate, which keeps the
/// traversal unit-testable without a network (and lets the self-test pin the rules for
/// cycles, missing versions and duplicate projects).</para>
///
/// <para>Order matters: dependencies come before the mods that need them, so a partially
/// failed install still leaves a loadable set rather than a mod with missing parents.</para>
/// </summary>
public static class ModDependencyResolver
{
    /// <summary>Hard stop; a malicious or broken graph must not spin forever.</summary>
    public const int MaxDepth = 8;

    /// <summary>Node limit across one plan, independent of depth.</summary>
    public const int MaxNodes = 200;

    /// <param name="listVersions">
    /// Fetches candidate versions for a project, already filtered by game version + loader.
    /// Returning an empty list means "no compatible build".
    /// </param>
    public static async Task<DependencyPlan> ResolveAsync(
        ModrinthVersion root,
        Func<string, CancellationToken, Task<IReadOnlyList<ModrinthVersion>>> listVersions,
        CancellationToken ct = default)
    {
        var order = new List<ModrinthVersion>();
        var missing = new List<string>();
        var seenProjects = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var visiting = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        async Task WalkAsync(ModrinthVersion version, string projectKey, int depth)
        {
            ct.ThrowIfCancellationRequested();

            if (order.Count >= MaxNodes)
            {
                missing.Add($"依赖链超过 {MaxNodes} 个条目，已停止解析");
                return;
            }

            // Deps first, so the caller can install in list order.
            if (depth < MaxDepth)
            {
                foreach (var dep in version.Dependencies)
                {
                    if (!dep.IsRequired) continue;
                    if (string.IsNullOrWhiteSpace(dep.ProjectId)) continue;

                    if (seenProjects.Contains(dep.ProjectId) || visiting.Contains(dep.ProjectId))
                        continue;   // already scheduled, or a cycle back into the current path

                    visiting.Add(dep.ProjectId);

                    ModrinthVersion? chosen = null;
                    try
                    {
                        var candidates = await listVersions(dep.ProjectId, ct).ConfigureAwait(false);
                        chosen = Pick(candidates, dep.VersionId);
                    }
                    catch (OperationCanceledException) { throw; }
                    catch (Exception ex)
                    {
                        missing.Add($"{version.Name} 需要 {dep.ProjectId}，查询失败：{ex.Message}");
                    }

                    if (chosen == null)
                    {
                        if (missing.All(m => !m.Contains(dep.ProjectId)))
                        {
                            missing.Add(version.VersionNumber.Length > 0
                                ? $"{version.Name} {version.VersionNumber} 需要前置 {dep.ProjectId}，但没有找到兼容版本"
                                : $"{version.Name} 需要前置 {dep.ProjectId}，但没有找到兼容版本");
                        }
                    }
                    else
                    {
                        seenProjects.Add(dep.ProjectId);
                        await WalkAsync(chosen, dep.ProjectId, depth + 1).ConfigureAwait(false);
                    }

                    visiting.Remove(dep.ProjectId);
                }
            }
            else
            {
                missing.Add($"{version.Name} 的依赖链超过 {MaxDepth} 层，已截断");
            }

            order.Add(version);
        }

        seenProjects.Add(root.ProjectId);
        await WalkAsync(root, root.ProjectId, 0).ConfigureAwait(false);

        return new DependencyPlan(order, missing);
    }

    /// <summary>
    /// Picks the version to install for a dependency: an explicitly pinned version wins,
    /// otherwise the newest release, otherwise the newest anything (Modrinth returns newest
    /// first). Loader filtering already happened upstream, so no extra scoring is needed.
    /// </summary>
    public static ModrinthVersion? Pick(IReadOnlyList<ModrinthVersion> candidates, string? pinnedVersionId)
    {
        if (candidates.Count == 0) return null;

        if (!string.IsNullOrWhiteSpace(pinnedVersionId))
        {
            var pinned = candidates.FirstOrDefault(v =>
                string.Equals(v.Id, pinnedVersionId, StringComparison.OrdinalIgnoreCase));
            if (pinned != null) return pinned;
        }

        return candidates.FirstOrDefault(v => v.IsRelease && v.HasFile)
               ?? candidates.FirstOrDefault(v => v.HasFile)
               ?? candidates[0];
    }
}
