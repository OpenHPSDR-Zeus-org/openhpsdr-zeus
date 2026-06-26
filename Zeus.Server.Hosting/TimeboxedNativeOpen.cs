// SPDX-License-Identifier: GPL-2.0-or-later
//
// Zeus - OpenHPSDR Protocol-1 / Protocol-2 client.
// Copyright (C) 2026 Brian Keating (EI6LF), Christian Suarez (N9WAR), and contributors.

namespace Zeus.Server;

/// <summary>
/// Runs a potentially-blocking native device open on a throwaway background
/// thread and gives up after a bounded budget.
/// </summary>
internal static class TimeboxedNativeOpen
{
    public static T? Run<T>(Func<T> open, Action<T> dispose, TimeSpan timeout, out Exception? error)
        where T : class
    {
        var gate = new object();
        T? opened = null;
        Exception? workerError = null;
        var produced = false;
        var orphaned = false;

        var worker = new Thread(() =>
        {
            T? local = null;
            Exception? err = null;
            try
            {
                local = open();
            }
            catch (Exception ex)
            {
                err = ex;
            }

            lock (gate)
            {
                if (orphaned)
                {
                    if (local != null)
                    {
                        try { dispose(local); } catch { /* best effort */ }
                    }
                    return;
                }

                opened = local;
                workerError = err;
                produced = true;
            }
        })
        {
            IsBackground = true,
            Name = "zeus-native-open",
        };
        worker.Start();

        if (worker.Join(timeout))
        {
            lock (gate)
            {
                error = workerError;
                return opened;
            }
        }

        lock (gate)
        {
            if (produced)
            {
                error = workerError;
                return opened;
            }

            orphaned = true;
        }

        error = null;
        return null;
    }
}
