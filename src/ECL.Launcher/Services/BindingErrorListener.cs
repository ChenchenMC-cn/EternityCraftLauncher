// SPDX-License-Identifier: MPL-2.0
// Copyright (c) 2026 ChenchenMC <https://github.com/ChenchenMC-cn/EternityCraftLauncher>
using System.Diagnostics;
using System.IO;

namespace ECL.Services;

/// <summary>
/// Routes WPF data-binding failures into a log file.
///
/// <para>Binding errors are invisible in a Release build: a mistyped property name in
/// XAML silently leaves a control empty, with no exception and no console output. This
/// listener turns them into text so a screenshot run can prove the UI is actually bound.
/// Enabled with <c>--binding-log &lt;path&gt;</c>.</para>
/// </summary>
public sealed class BindingErrorListener : TraceListener
{
    private readonly TextWriter _output;
    private readonly object _gate = new();

    private BindingErrorListener(TextWriter output) => _output = output;

    /// <summary>Attaches the listener and returns it (keep a reference so it lives on).</summary>
    public static BindingErrorListener Attach(string path)
    {
        var writer = new StreamWriter(path, append: false) { AutoFlush = true };
        var listener = new BindingErrorListener(writer);
        PresentationTraceSources.Refresh();
        PresentationTraceSources.DataBindingSource.Listeners.Add(listener);
        PresentationTraceSources.DataBindingSource.Switch.Level = SourceLevels.Warning;
        return listener;
    }

    public override void Write(string? message) => Emit(message);

    public override void WriteLine(string? message) => Emit(message);

    private void Emit(string? message)
    {
        if (string.IsNullOrWhiteSpace(message)) return;

        lock (_gate)
        {
            _output.WriteLine($"[{DateTime.Now:HH:mm:ss.fff}] {message}");
        }
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            PresentationTraceSources.DataBindingSource.Listeners.Remove(this);
            _output.Dispose();
        }
        base.Dispose(disposing);
    }
}
