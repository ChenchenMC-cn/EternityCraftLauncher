// SPDX-License-Identifier: MPL-2.0
// Copyright (c) 2026 ChenchenMC <https://github.com/ChenchenMC-cn/EternityCraftLauncher>
using System;
using System.Collections.Generic;
using System.Windows;

namespace ECL.Addons.Api;

/// <summary>
/// ===========================================================================
///  ECL addon contract, API v1.
///
///  Three surfaces, one version number:
///
///    ECL Interface API  -> the manifest (addon.json) + resource files. No code.
///                          Everything a theme pack or any non-C# tool can do.
///    ECL Addition API   -> <see cref="IEclAddon"/>. Runtime behaviour: launch hooks,
///                          background tasks, hotkeys.
///    ECLUi C# API       -> <see cref="IEclUiExtension"/>. WPF elements injected into
///                          named slots of the main window.
///
///  RULES FOR MAINTAINERS
///    - These names are a published ABI. Do not rename, do not change signatures in
///      v1; add v2 members on new interfaces instead.
///    - Do not add WPF types to <see cref="IEclAddon"/>: a headless plugin must be
///      loadable without a window.
///    - Anything a plugin may touch goes through <see cref="IEclHost"/>, so the
///      surface stays auditable.
/// ===========================================================================
/// </summary>
public static class EclApiInfo
{
    /// <summary>Contract version. A plugin compiled against a newer value is rejected.</summary>
    public const int Version = 1;

    /// <summary>Identifier reported to plugins; also written into diagnostics bundles.</summary>
    public const string Name = "ECL Addon API";
}

/// <summary>
/// ECL Addition API: the runtime behaviour half of a plugin.
/// Implementations are constructed by the loader (parameterless ctor) and kept alive
/// for the whole session.
/// </summary>
public interface IEclAddon
{
    /// <summary>Must match the <c>id</c> in addon.json; the loader cross-checks it.</summary>
    string Id { get; }

    /// <summary>Called once, on the UI thread, after the main window exists.</summary>
    void OnLoad(IEclHost host);

    /// <summary>
    /// Called on shutdown. Throwing here is logged and ignored — one misbehaving plugin
    /// must not block the launcher from exiting.
    /// </summary>
    void OnUnload();
}

/// <summary>
/// ECLUi C# API: the visual half of a plugin.
///
/// <para>Implement it in the same assembly as <see cref="IEclAddon"/> (both are optional;
/// a pure UI plugin can implement only this one). The launcher asks for each slot it
/// knows how to host and inserts whatever comes back.</para>
/// </summary>
public interface IEclUiExtension
{
    /// <summary>
    /// Builds the element for a slot, or null to contribute nothing. Called on the UI
    /// thread, possibly more than once (page rebuilds), so do not cache WPF objects.
    /// </summary>
    FrameworkElement? CreateView(EclUiSlot slot);
}

/// <summary>
/// Injection points the main window offers. Adding a slot is a v2 change: v1 plugins
/// must keep working, so unknown slot values are simply never requested.
/// </summary>
public enum EclUiSlot
{
    /// <summary>Extra card on the home page, under the version list.</summary>
    HomeCard = 0,

    /// <summary>Extra card at the top of the settings page.</summary>
    SettingsCard = 1,

    /// <summary>Row below the account list on the accounts page.</summary>
    AccountRow = 2,

    /// <summary>Extra toolbar button next to "Open folder" on the home page.</summary>
    HomeToolbar = 3
}

/// <summary>
/// Everything a plugin is allowed to reach. Deliberately small: no reflection helpers,
/// no file-system root, no access to the account store.
/// </summary>
public interface IEclHost
{
    /// <summary>Launcher version, e.g. <c>1.0.0</c>.</summary>
    string LauncherVersion { get; }

    /// <summary>Active data root (the <c>.minecraft</c> equivalent).</summary>
    string DataRoot { get; }

    /// <summary>Writes into the launcher log with the addon's id as the prefix.</summary>
    void Log(string message);

    /// <summary>
    /// Fires right before a game process is started. Handlers run on a background
    /// thread and must not block for long: the launch waits for them.
    /// </summary>
    event EventHandler<EclLaunchEventArgs>? BeforeLaunch;
}

public sealed class EclLaunchEventArgs : EventArgs
{
    public EclLaunchEventArgs(string versionId, string gameDir, IReadOnlyList<string> jvmArgs)
    {
        VersionId = versionId;
        GameDir = gameDir;
        JvmArgs = jvmArgs;
    }

    public string VersionId { get; }
    public string GameDir { get; }

    /// <summary>Read-only view of the assembled JVM arguments (informational).</summary>
    public IReadOnlyList<string> JvmArgs { get; }
}
