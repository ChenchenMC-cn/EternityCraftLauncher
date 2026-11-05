// SPDX-License-Identifier: MPL-2.0
// Copyright (c) 2026 ChenchenMC <https://github.com/ChenchenMC-cn/EternityCraftLauncher>
using System.Runtime.InteropServices;

namespace ECL.Services;

/// <summary>
/// Thin, allocation aware wrapper over ECL.Native.dll (C++ core): parallel SHA-1,
/// parallel HTTP transfers and zip extraction all run off the UI thread.
/// </summary>
public static class NativeCore
{
    private const string Dll = "ECL.Native.dll";

    public const int Ok = 0;
    public const int ErrCancel = 6;

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    public delegate void ProgressCallback(int filesDone, int filesTotal, long bytesDone, long bytesTotal, IntPtr user);

    [DllImport(Dll, CallingConvention = CallingConvention.StdCall, CharSet = CharSet.Unicode)]
    private static extern IntPtr ECL_Version();

    [DllImport(Dll, CallingConvention = CallingConvention.StdCall, CharSet = CharSet.Unicode)]
    private static extern int ECL_DownloadBatch(IntPtr[] urls, IntPtr[] dests, long[] sizes,
        int count, int threads, ProgressCallback? cb, IntPtr user);

    [DllImport(Dll, CallingConvention = CallingConvention.StdCall, CharSet = CharSet.Unicode)]
    private static extern int ECL_Unzip(string zipPath, string destDir, ProgressCallback? cb, IntPtr user);

    [DllImport(Dll, CallingConvention = CallingConvention.StdCall)]
    private static extern void ECL_Cancel();

    [DllImport(Dll, CallingConvention = CallingConvention.StdCall)]
    private static extern int ECL_LastCacheHits();

    [DllImport(Dll, CallingConvention = CallingConvention.StdCall, CharSet = CharSet.Unicode)]
    private static extern int ECL_LastError(System.Text.StringBuilder buf, int bufLen);

    [DllImport(Dll, CallingConvention = CallingConvention.StdCall, CharSet = CharSet.Unicode)]
    private static extern int ECL_HashFile(string path, int algo, byte[] outHex, int outHexLen);

    public const int AlgoSha1 = 1;

    public static bool IsAvailable(out string version)
    {
        try
        {
            version = Marshal.PtrToStringUTF8(ECL_Version()) ?? "";
            return !string.IsNullOrEmpty(version);
        }
        catch (DllNotFoundException)
        {
            version = "";
            return false;
        }
        catch (EntryPointNotFoundException)
        {
            version = "";
            return false;
        }
    }

    public static void Cancel() => ECL_Cancel();

    public static int LastCacheHits() => ECL_LastCacheHits();

    public static string LastError()
    {
        var sb = new System.Text.StringBuilder(768);
        try { ECL_LastError(sb, sb.Capacity); } catch { return ""; }
        return sb.ToString();
    }

    public static string Sha1File(string path)
    {
        var buf = new byte[48];
        int rc = ECL_HashFile(path, AlgoSha1, buf, buf.Length);
        if (rc != Ok) return "";
        int len = Array.IndexOf(buf, (byte)0);
        if (len < 0) len = buf.Length;
        return System.Text.Encoding.ASCII.GetString(buf, 0, len);
    }

    public sealed record DownloadItem(string Url, string Destination, long ExpectedSize, string Sha1);

    public sealed record BatchResult(bool Success, bool Cancelled, int CacheHits, string Error, TimeSpan Elapsed)
    {
        public double MegaBytesPerSecond { get; init; }
    }

    /// <summary>
    /// Downloads all items in parallel inside the native engine. Progress is
    /// reported through <paramref name="progress"/> on whatever thread it was
    /// created on (pass a Progress&lt;T&gt; built on the UI thread).
    /// </summary>
    public static async Task<BatchResult> DownloadAsync(
        IReadOnlyList<DownloadItem> items,
        int threads,
        IProgress<DownloadProgress>? progress,
        CancellationToken token)
    {
        if (items.Count == 0) return new BatchResult(true, false, 0, "", TimeSpan.Zero);

        var urls = new IntPtr[items.Count];
        var dests = new IntPtr[items.Count];
        var sizes = new long[items.Count];
        var sw = System.Diagnostics.Stopwatch.StartNew();
        double speed = 0;

        try
        {
            for (int i = 0; i < items.Count; i++)
            {
                urls[i] = Marshal.StringToHGlobalUni(items[i].Url);
                dests[i] = Marshal.StringToHGlobalUni(items[i].Destination);
                sizes[i] = items[i].ExpectedSize;
            }

            ProgressCallback cb = (done, total, bytesDone, bytesTotal, _) =>
            {
                double elapsed = sw.Elapsed.TotalSeconds;
                if (elapsed > 0.25)
                {
                    double current = bytesDone / elapsed;
                    speed = speed <= 0 ? current : speed * 0.75 + current * 0.25;
                }
                progress?.Report(new DownloadProgress(done, total, bytesDone, bytesTotal, speed));
            };

            var (rc, hits) = await Task.Run(() =>
            {
                using var reg = token.Register(Cancel);
                int r = ECL_DownloadBatch(urls, dests, sizes, items.Count, threads, cb, IntPtr.Zero);
                return (r, LastCacheHits());
            }, CancellationToken.None).ConfigureAwait(false);

            sw.Stop();
            double totalMb = 0;
            for (int i = 0; i < items.Count; i++) if (sizes[i] > 0) totalMb += sizes[i];
            double mbps = sw.Elapsed.TotalSeconds > 0.1 ? totalMb / 1024.0 / 1024.0 / sw.Elapsed.TotalSeconds : 0;

            return new BatchResult(rc == Ok, rc == ErrCancel, hits, rc == Ok ? "" : LastError(), sw.Elapsed)
            {
                MegaBytesPerSecond = mbps
            };
        }
        finally
        {
            foreach (var p in urls) if (p != IntPtr.Zero) Marshal.FreeHGlobal(p);
            foreach (var p in dests) if (p != IntPtr.Zero) Marshal.FreeHGlobal(p);
        }
    }

    public static Task<(bool Success, string Error)> UnzipAsync(string zipPath, string destDir, CancellationToken token)
    {
        return Task.Run(() =>
        {
            using var reg = token.Register(Cancel);
            int rc = ECL_Unzip(zipPath, destDir, null, IntPtr.Zero);
            return (rc == Ok, rc == Ok ? "" : LastError());
        }, CancellationToken.None);
    }
}

public readonly record struct DownloadProgress(int FilesDone, int FilesTotal, long BytesDone, long BytesTotal, double BytesPerSecond)
{
    public double Fraction => BytesTotal > 0 ? Math.Clamp((double)BytesDone / BytesTotal, 0, 1) : 0;
}
