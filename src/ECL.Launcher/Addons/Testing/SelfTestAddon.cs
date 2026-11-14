// SPDX-License-Identifier: MPL-2.0
// Copyright (c) 2026 ChenchenMC <https://github.com/ChenchenMC-cn/EternityCraftLauncher>
using System;
using ECL.Addons.Api;

namespace ECL.Addons.Testing;

/// <summary>
/// A plugin that ships inside the launcher purely so the self-test can exercise the real
/// load path (manifest -> assembly -> type -> OnLoad -> launch event -> unload) without
/// pulling a second project into the build.
///
/// <para>It is found by "the single type implementing IEclAddon", not by name, because
/// obfuscation renames it.</para>
/// </summary>
internal sealed class SelfTestAddon : IEclAddon
{
    public const string ExpectedId = "selftest";

    public static bool LoadCalls;
    public static bool LaunchCalls;
    public static bool UnloadCalls;

    public string Id => ExpectedId;

    public void OnLoad(IEclHost host)
    {
        LoadCalls = true;
        host.Log("[addon selftest] OnLoad");
        host.BeforeLaunch += (_, e) =>
        {
            LaunchCalls = true;
            host.Log($"[addon selftest] BeforeLaunch {e.VersionId}");
        };
    }

    public void OnUnload() => UnloadCalls = true;
}
