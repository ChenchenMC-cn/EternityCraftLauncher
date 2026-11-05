// SPDX-License-Identifier: MPL-2.0
// Copyright (c) 2026 ChenchenMC <https://github.com/ChenchenMC-cn/EternityCraftLauncher>
using System;
using System.Windows;
using System.Windows.Threading;

namespace ECL.ViewModels;

/// <summary>
/// <see cref="Progress{T}"/> posts its callback to the captured synchronization context.
/// Two things can go wrong with that, and both used to be fatal:
///
/// <list type="number">
/// <item>an exception inside the callback is not caught by the try/catch around the
/// awaited call -- it lands on the dispatcher and pops the crash dialog;</item>
/// <item>if the context was null when the instance was created (a callback built inside
/// a <c>ConfigureAwait(false)</c> continuation), the callback runs <b>inline on the
/// reporting thread</b>, which for a download is a native/pool thread. Touching a bound
/// property from there throws "the calling thread cannot access this object" on a
/// background thread -- and an unhandled background exception kills the process.</item>
/// </list>
///
/// <para>So: always marshal to the dispatcher, and never let a callback throw.</para>
/// </summary>
internal static class SafeProgress
{
    public static Progress<T> Create<T>(Action<T> apply, Action<Exception>? onError = null)
    {
        var app = Application.Current;
        var dispatcher = app?.Dispatcher;

        return new Progress<T>(value =>
        {
            // Headless runs have no Application and no bindings; run inline.
            if (dispatcher == null)
            {
                Run(apply, onError, value);
                return;
            }

            if (dispatcher.CheckAccess())
            {
                Run(apply, onError, value);
                return;
            }

            // Wrong thread: hop over instead of touching WPF state right here.
            try
            {
                dispatcher.BeginInvoke(DispatcherPriority.Background,
                    new Action(() => Run(apply, onError, value)));
            }
            catch
            {
                // Dispatcher shutting down: dropping a progress tick is fine.
            }
        });
    }

    private static void Run<T>(Action<T> apply, Action<Exception>? onError, T value)
    {
        try
        {
            apply(value);
        }
        catch (Exception ex)
        {
            onError?.Invoke(ex);
        }
    }
}
