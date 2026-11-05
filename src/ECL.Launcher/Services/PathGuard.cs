// SPDX-License-Identifier: MPL-2.0
// Copyright (c) 2026 ChenchenMC <https://github.com/ChenchenMC-cn/EternityCraftLauncher>
using System.IO;
using Microsoft.Win32;

namespace ECL.Services;

/// <summary>
/// Guards against the Windows MAX_PATH wall.
///
/// .NET 5+ only gets long-path support for free when the OS has
/// <c>LongPathsEnabled=1</c>; on a default Windows install it is 0, and the failure
/// mode is ugly — extraction stops halfway with a bare IOException that names no
/// file. Rather than prefix hundreds of call sites (which breaks
/// <c>ZipFile.ExtractToDirectory</c> and friends), this checks the real limit and
/// reports which path blew it.
/// </summary>
public static class PathGuard
{
    /// <summary>Windows' classic limit, minus room for the filename itself.</summary>
    private const int SafeDirectoryLimit = 240;

    private static readonly Lazy<bool> LongPathsEnabled = new(() =>
    {
        try
        {
            using var key = Registry.LocalMachine.OpenSubKey(
                @"SYSTEM\CurrentControlSet\Control\FileSystem");
            return Convert.ToInt32(key?.GetValue("LongPathsEnabled") ?? 0) == 1;
        }
        catch
        {
            return false;
        }
    });

    public static bool SupportsLongPaths => LongPathsEnabled.Value;

    /// <summary>
    /// Throws with an actionable message when <paramref name="path"/> cannot be
    /// created on this machine. Call before extracting or writing deep trees.
    /// </summary>
    /// <param name="supportsLongPaths">
    /// Test hook: pass false to exercise the check on a machine that already has
    /// long paths enabled (the check would otherwise be a no-op there).
    /// </param>
    public static void EnsureUsable(string path, string what, bool? supportsLongPaths = null)
    {
        if (supportsLongPaths ?? SupportsLongPaths) return;

        string full;
        try { full = Path.GetFullPath(path); }
        catch (Exception ex)
        {
            throw new IOException($"{what} 的路径无效：{path}", ex);
        }

        if (full.Length <= SafeDirectoryLimit) return;

        throw new IOException(
            $"{what} 的路径太长（{full.Length} 字符，上限约 {SafeDirectoryLimit}）：\n{full}\n\n" +
            "Windows 默认不支持超过 260 字符的路径。两种解决办法：\n" +
            "1. 把游戏数据目录改短一点（设置 → 存储，例如 D:\\MC）；\n" +
            "2. 启用系统长路径支持（管理员 PowerShell 执行）：\n" +
            "   New-ItemProperty -Path 'HKLM:\\SYSTEM\\CurrentControlSet\\Control\\FileSystem' " +
            "-Name LongPathsEnabled -Value 1 -PropertyType DWORD -Force");
    }
}
