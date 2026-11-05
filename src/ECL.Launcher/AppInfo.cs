// SPDX-License-Identifier: MPL-2.0
// Copyright (c) 2026 ChenchenMC <https://github.com/ChenchenMC-cn/EternityCraftLauncher>
using System.Reflection;

namespace ECL;

/// <summary>
/// Single source of truth for the version shown to users.
///
/// <para>Everything reads from the assembly attributes stamped by the csproj, so a
/// release only needs the version bumped in one place. Before this existed the number
/// was hard-coded in three spots (view model, launch arguments, log header) and the
/// csproj still said 0.1.0 while the UI showed 0.8.0.</para>
///
/// <para>Naming rule (released, no "Beta", no leading "v"): <c>1.0</c> for the
/// release, <c>1.0 Fix 3</c> for a fix, <c>1.1</c> for a medium update, <c>2.0</c> for a
/// major one, and <c>1.0 Build07</c> for development builds. A tenth fix of the same
/// minor rolls over to the next minor. Automated by <c>scripts\version.bat -Bump</c>
/// (or <c>-Medium</c> / <c>-Major</c> / <c>-Dev</c>).</para>
/// </summary>
internal static class AppInfo
{
    /// <summary>Human-facing version, e.g. <c>1.0 Fix 2</c>. Comes from InformationalVersion.</summary>
    public static string Version { get; } = ReadInformational();

    /// <summary>
    /// Numeric form for places that must be dot-separated digits, e.g. the
    /// <c>-Dminecraft.launcher.version</c> launch argument. Comes from AssemblyVersion.
    /// </summary>
    public static string VersionNumber { get; } =
        typeof(AppInfo).Assembly.GetName().Version is { } v
            ? $"{v.Major}.{v.Minor}.{v.Build}"
            : "1.0.0";

    private static string ReadInformational()
    {
        var attr = typeof(AppInfo).Assembly
            .GetCustomAttribute<AssemblyInformationalVersionAttribute>();

        string value = attr?.InformationalVersion ?? "1.0.0";

        // SourceLink appends "+<commit>" when the repo is under git; users do not
        // need to see that in the title bar.
        int plus = value.IndexOf('+');
        return plus >= 0 ? value[..plus] : value;
    }
}
