// SPDX-License-Identifier: MPL-2.0
// Copyright (c) 2026 ChenchenMC <https://github.com/ChenchenMC-cn/EternityCraftLauncher>
using System;
using System.Collections.Concurrent;
using System.IO;
using System.Net.Http;
using System.Security.Cryptography;
using System.Threading;
using System.Threading.Tasks;

namespace ECL.Services;

/// <summary>
/// Download path that uses nothing but <see cref="HttpClient"/>.
///
/// <para>Exists because "下载就崩启动器" had to be split in two: if the crash stops when
/// this path is used, the fault is in <c>ECL.Native.dll</c> (WinHTTP + a native thread
/// pool); if it persists, the fault is above it. It is also the fallback when the native
/// library is missing entirely.</para>
///
/// <para>Slower than the native batch (no HTTP/2 reuse, no zero-copy), but it writes the
/// same files and verifies the same sizes, so a repaired install is indistinguishable
/// afterwards.</para>
/// </summary>
public static class ManagedDownloader
{
    private static readonly HttpClient Http = CreateClient();

    /// <summary>No bytes for this long = the transfer is dead.</summary>
    private static readonly TimeSpan IdleTimeout = TimeSpan.FromSeconds(60);

    private static HttpClient CreateClient()
    {
        var client = new HttpClient(new SocketsHttpHandler
        {
            ConnectTimeout = TimeSpan.FromSeconds(15),
            PooledConnectionLifetime = TimeSpan.FromMinutes(5),
            MaxConnectionsPerServer = 16,
            AutomaticDecompression = System.Net.DecompressionMethods.None
        })
        {
            Timeout = TimeSpan.FromMinutes(5)   // a 100 MB asset on a bad line; per file
        };

        client.DefaultRequestHeaders.UserAgent.ParseAdd("ECL/1.0 (EternityCraftLauncher)");
        return client;
    }

    public sealed record Result(int Downloaded, int Skipped, int Failed, long Bytes, string FirstError);

    public static async Task<Result> DownloadAsync(
        IReadOnlyList<NativeCore.DownloadItem> items,
        int threads,
        IProgress<DownloadProgress>? progress,
        CancellationToken ct,
        string? tracePath = null,
        Action<string>? log = null)
    {
        if (items.Count == 0) return new Result(0, 0, 0, 0, "");

        int concurrency = Math.Clamp(threads, 1, 32);
        long total = 0;
        foreach (var item in items) if (item.ExpectedSize > 0) total += item.ExpectedSize;

        int downloaded = 0, skipped = 0, failed = 0;
        long bytes = 0;
        string firstError = "";
        int done = 0;

        var traceLock = new object();
        var started = DateTime.UtcNow;
        using var gate = new SemaphoreSlim(concurrency, concurrency);

        async Task RunOne(NativeCore.DownloadItem item)
        {
            await gate.WaitAsync(ct).ConfigureAwait(false);
            try
            {
                ct.ThrowIfCancellationRequested();

                if (await IsUpToDateAsync(item, ct).ConfigureAwait(false))
                {
                    Interlocked.Increment(ref skipped);
                    Interlocked.Add(ref bytes, item.ExpectedSize > 0 ? item.ExpectedSize : 0);
                    return;
                }

                string? dir = Path.GetDirectoryName(item.Destination);
                if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);

                // Download to .part then move: a killed launcher never leaves a
                // half-written jar that looks complete to a size check.
                string part = item.Destination + ".part";

                // HttpClient.Timeout only covers the response *headers* once
                // ResponseHeadersRead is used: a connection that dies mid-body waits
                // forever, which is exactly how one install hung on a 9 MB .part.
                // Re-armed on every successful read, so it means "no data for 60s".
                using var idle = CancellationTokenSource.CreateLinkedTokenSource(ct);
                idle.CancelAfter(IdleTimeout);

                using (var response = await Http.GetAsync(item.Url, HttpCompletionOption.ResponseHeadersRead, idle.Token)
                                              .ConfigureAwait(false))
                {
                    response.EnsureSuccessStatusCode();

                    await using var source = await response.Content.ReadAsStreamAsync(idle.Token).ConfigureAwait(false);
                    await using var target = new FileStream(part, FileMode.Create, FileAccess.Write,
                        FileShare.None, 128 * 1024, useAsync: true);

                    var buffer = new byte[128 * 1024];
                    int read;
                    while (true)
                    {
                        idle.CancelAfter(IdleTimeout);
                        read = await source.ReadAsync(buffer, idle.Token).ConfigureAwait(false);
                        if (read <= 0) break;

                        await target.WriteAsync(buffer.AsMemory(0, read), ct).ConfigureAwait(false);

                        long soFar = Interlocked.Add(ref bytes, read);
                        // File counters are only cosmetic here; bytes are the honest signal.
                        int finished = Volatile.Read(ref done);
                        progress?.Report(new DownloadProgress(finished, items.Count, soFar, total,
                            Speed(soFar, started)));
                    }
                }

                var info = new FileInfo(part);
                if (item.ExpectedSize > 0 && info.Length != item.ExpectedSize)
                {
                    File.Delete(part);
                    throw new IOException($"大小不符：{info.Length} ≠ {item.ExpectedSize}");
                }

                File.Move(part, item.Destination, overwrite: true);
                Interlocked.Increment(ref downloaded);

                if (tracePath != null)
                {
                    lock (traceLock)
                    {
                        try { File.AppendAllText(tracePath, $"{DateTime.Now:HH:mm:ss.fff} OK {info.Length,12} {item.Destination}{Environment.NewLine}"); }
                        catch { }
                    }
                }
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
                // Our own idle timer fired: this file stalled, the rest of the batch did not.
                Interlocked.Increment(ref failed);
                if (firstError.Length == 0) firstError = "下载超时（60 秒没有收到数据）";
                log?.Invoke($"  超时 {Path.GetFileName(item.Destination)}（60 秒无数据）");

                try { File.Delete(item.Destination + ".part"); } catch { }
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                Interlocked.Increment(ref failed);
                if (firstError.Length == 0) firstError = ex.Message;

                log?.Invoke($"  失败 {Path.GetFileName(item.Destination)}：{ex.Message}");
                if (tracePath != null)
                {
                    lock (traceLock)
                    {
                        try { File.AppendAllText(tracePath, $"{DateTime.Now:HH:mm:ss.fff} ERR {ex.GetType().Name} {item.Destination} :: {ex.Message}{Environment.NewLine}"); }
                        catch { }
                    }
                }

                try { File.Delete(item.Destination + ".part"); } catch { }
            }
            finally
            {
                Interlocked.Increment(ref done);
                gate.Release();
            }
        }

        var tasks = new List<Task>(items.Count);
        foreach (var item in items) tasks.Add(RunOne(item));
        await Task.WhenAll(tasks).ConfigureAwait(false);

        return new Result(downloaded, skipped, failed, Interlocked.Read(ref bytes), firstError);
    }

    private static double Speed(long bytes, DateTime started)
    {
        double seconds = (DateTime.UtcNow - started).TotalSeconds;
        return seconds > 0.25 ? bytes / seconds : 0;
    }

    /// <summary>Same rule as the native path: right size means "already there".</summary>
    private static async Task<bool> IsUpToDateAsync(NativeCore.DownloadItem item, CancellationToken ct)
    {
        try
        {
            var info = new FileInfo(item.Destination);
            if (!info.Exists || info.Length == 0) return false;

            if (item.ExpectedSize > 0) return info.Length == item.ExpectedSize;

            // Unknown size (old-format libraries): only trust a hash when we have one.
            if (string.IsNullOrEmpty(item.Sha1)) return true;

            await using var stream = File.OpenRead(item.Destination);
            byte[] hash = await SHA1.HashDataAsync(stream, ct).ConfigureAwait(false);
            return Convert.ToHexString(hash).Equals(item.Sha1, StringComparison.OrdinalIgnoreCase);
        }
        catch
        {
            return false;
        }
    }
}
