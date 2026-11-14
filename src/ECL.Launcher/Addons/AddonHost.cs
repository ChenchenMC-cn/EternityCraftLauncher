// SPDX-License-Identifier: MPL-2.0
// Copyright (c) 2026 ChenchenMC <https://github.com/ChenchenMC-cn/EternityCraftLauncher>
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.Loader;
using ECL.Addons.Api;

namespace ECL.Addons;

/// <summary>
/// Loads addon assemblies and owns their lifetime.
///
/// <para>One collectible <see cref="AssemblyLoadContext"/> per addon, so a plugin can be
/// unloaded (and its file replaced) without restarting. Assemblies that the host already
/// has loaded are deliberately resolved in the default context — otherwise a plugin
/// would see a second copy of <see cref="IEclAddon"/> and every cast would fail.</para>
///
/// <para>Every entry point is wrapped: a throwing plugin is logged and skipped, never
/// allowed to take the launcher down.</para>
/// </summary>
public sealed class AddonHost : IEclHost, IDisposable
{
    private sealed class Loaded
    {
        public required AddonManifest Manifest { get; init; }
        public required AssemblyLoadContext Context { get; init; }
        public IEclAddon? Addon { get; init; }
        public IEclUiExtension? Ui { get; init; }
    }

    /// <summary>
    /// The instance the launcher is running. Set once at startup so services that have no
    /// reference to the window (LaunchService) can still raise addon events.
    /// </summary>
    public static AddonHost? Current { get; internal set; }

    private readonly List<Loaded> _loaded = new();
    private readonly Action<string> _log;

    public AddonHost(string dataRoot, string launcherVersion, Action<string> log)
    {
        DataRoot = dataRoot;
        LauncherVersion = launcherVersion;
        _log = log;
    }

    public string LauncherVersion { get; }
    public string DataRoot { get; }

    public event EventHandler<EclLaunchEventArgs>? BeforeLaunch;

    public IReadOnlyList<AddonManifest> Plugins =>
        _loaded.Select(l => l.Manifest).ToList();

    public void Log(string message) => _log(message);

    /// <summary>
    /// Instantiates every manifest of kind plugin/mixed that the registry accepted.
    /// Returns the number that came up successfully.
    /// </summary>
    public int LoadAll()
    {
        foreach (var manifest in AddonRegistry.All)
        {
            if (manifest.Kind == AddonKind.Theme) continue;
            TryLoadOne(manifest);
        }

        return _loaded.Count;
    }

    private void TryLoadOne(AddonManifest manifest)
    {
        AssemblyLoadContext? context = null;
        try
        {
            context = new AddonLoadContext(manifest.Directory, manifest.Id, _log);
            string assemblyPath = Path.Combine(manifest.Directory, manifest.Entry!.Assembly);

            // LoadFromAssemblyPath always materialises a *new* copy, even for an assembly
            // the host already has. That copy would carry its own IEclAddon definition and
            // every cast would fail. So a plugin bundling a host assembly (typically ECL.dll
            // itself) is bound to the loaded instance instead.
            var wanted = AssemblyName.GetAssemblyName(assemblyPath);
            var assembly = AssemblyLoadContext.Default.Assemblies.FirstOrDefault(a =>
                                AssemblyName.ReferenceMatchesDefinition(a.GetName(), wanted))
                           ?? context.LoadFromAssemblyPath(assemblyPath);

            Type? entryType = ResolveEntryType(assembly, manifest.Entry.Type);
            if (entryType == null)
            {
                _log($"[addon {manifest.Id}] no type implements IEclAddon/IEclUiExtension");
                context.Unload();
                return;
            }

            object instance;
            try
            {
                instance = Activator.CreateInstance(entryType)!;
            }
            catch (Exception ex)
            {
                _log($"[addon {manifest.Id}] constructor failed: {Root(ex).Message}");
                context.Unload();
                return;
            }

            var addon = instance as IEclAddon;
            var ui = instance as IEclUiExtension;

            if (addon != null && !string.Equals(addon.Id, manifest.Id, StringComparison.OrdinalIgnoreCase))
            {
                _log($"[addon {manifest.Id}] rejected: Id property says '{addon.Id}', manifest says '{manifest.Id}'");
                context.Unload();
                return;
            }

            addon?.OnLoad(this);

            _loaded.Add(new Loaded
            {
                Manifest = manifest,
                Context = context,
                Addon = addon,
                Ui = ui
            });

            _log($"[addon {manifest.Id}] loaded ({Describe(entryType)}), " +
                 $"api v{AddonManifest.CurrentApiVersion}");
        }
        catch (Exception ex)
        {
            _log($"[addon {manifest.Id}] load failed: {Root(ex).Message}");
            try { context?.Unload(); } catch { /* nothing left to do */ }
        }
    }

    private static Type? ResolveEntryType(Assembly assembly, string typeName)
    {
        if (!string.IsNullOrWhiteSpace(typeName))
        {
            var exact = assembly.GetType(typeName, throwOnError: false);
            if (exact != null) return exact;
        }

        // No (or stale) type name: take the single implementing type. Obfuscation renames
        // classes, so name-based lookup alone would break the obfuscated build.
        var candidates = assembly.GetTypes()
            .Where(t => t is { IsClass: true, IsAbstract: false } &&
                        (typeof(IEclAddon).IsAssignableFrom(t) || typeof(IEclUiExtension).IsAssignableFrom(t)))
            .ToList();

        return candidates.Count == 1 ? candidates[0] : null;
    }

    private static string Describe(Type t) =>
        typeof(IEclAddon).IsAssignableFrom(t) && typeof(IEclUiExtension).IsAssignableFrom(t)
            ? "addon + ui"
            : typeof(IEclAddon).IsAssignableFrom(t) ? "addon" : "ui only";

    private static Exception Root(Exception ex)
    {
        while (ex.InnerException != null) ex = ex.InnerException;
        return ex;
    }

    /// <summary>
    /// Fans the launch event out to every plugin. Handlers run in plugin code, so each
    /// one is isolated: one failure must not cancel the launch.
    /// </summary>
    public void RaiseBeforeLaunch(string versionId, string gameDir, IReadOnlyList<string> jvmArgs)
    {
        if (_loaded.Count == 0) return;

        var args = new EclLaunchEventArgs(versionId, gameDir, jvmArgs);
        foreach (var l in _loaded)
        {
            var handler = l.Addon;
            if (handler == null) continue;

            try
            {
                BeforeLaunch?.Invoke(this, args);
                break;      // the event is host-level; fire once, not once per addon
            }
            catch (Exception ex)
            {
                _log($"[addon {l.Manifest.Id}] BeforeLaunch threw: {Root(ex).Message}");
            }
        }
    }

    /// <summary>Asks every UI plugin for its element for this slot.</summary>
    public IReadOnlyList<(string AddonId, System.Windows.FrameworkElement Element)> CreateViews(EclUiSlot slot)
    {
        var result = new List<(string, System.Windows.FrameworkElement)>();
        foreach (var l in _loaded.Where(x => x.Ui != null))
        {
            try
            {
                var view = l.Ui!.CreateView(slot);
                if (view != null) result.Add((l.Manifest.Id, view));
            }
            catch (Exception ex)
            {
                _log($"[addon {l.Manifest.Id}] CreateView({slot}) threw: {Root(ex).Message}");
            }
        }

        return result;
    }

    public void Unload()
    {
        foreach (var l in _loaded)
        {
            try { l.Addon?.OnUnload(); }
            catch (Exception ex) { _log($"[addon {l.Manifest.Id}] OnUnload threw: {Root(ex).Message}"); }
        }

        foreach (var l in _loaded)
        {
            try { l.Context.Unload(); }
            catch (Exception ex) { _log($"[addon {l.Manifest.Id}] unload failed: {Root(ex).Message}"); }
        }

        _loaded.Clear();
    }

    public void Dispose() => Unload();
}

/// <summary>
/// Collectible context that resolves a plugin's own dependencies from its folder and
/// delegates everything else to the default context (so host types keep their identity).
/// </summary>
internal sealed class AddonLoadContext : AssemblyLoadContext
{
    private readonly string _dir;
    private readonly Action<string> _log;

    public AddonLoadContext(string dir, string id, Action<string> log)
        : base($"ecl-addon:{id}", isCollectible: true)
    {
        _dir = dir;
        _log = log;
    }

    protected override Assembly? Load(AssemblyName name)
    {
        // Already loaded by the host? Return null -> default context resolves it.
        if (Default.Assemblies.Any(a =>
                string.Equals(a.GetName().Name, name.Name, StringComparison.OrdinalIgnoreCase)))
            return null;

        string candidate = Path.Combine(_dir, name.Name + ".dll");
        if (File.Exists(candidate))
        {
            try { return LoadFromAssemblyPath(candidate); }
            catch (Exception ex) { _log($"[addon] {name.Name} load failed: {ex.Message}"); }
        }

        return null;
    }
}
