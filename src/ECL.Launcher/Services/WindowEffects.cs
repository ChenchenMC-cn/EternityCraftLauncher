// SPDX-License-Identifier: MPL-2.0
// Copyright (c) 2026 ChenchenMC <https://github.com/ChenchenMC-cn/EternityCraftLauncher>
using System.Runtime.InteropServices;

namespace ECL.Services;

/// <summary>
/// Window composition effects for the acrylic theme.
///
/// <para>Blur: two different accent states, deliberately.</para>
///
/// <list type="bullet">
/// <item>Windows 11: <c>ACCENT_ENABLE_ACRYLICBLURBEHIND</c> (4) with
/// <c>ACCENT_FLAG_DRAW_BORDERS</c>. This is the real acrylic and it composites
/// correctly there.</item>
/// <item>Windows 10: <c>ACCENT_ENABLE_BLURBEHIND</c> (3) without flags. Acrylic was
/// only ever half-finished on 10 — with the border flag it can leave whole halves of
/// the window unpainted (users report "一边白一边黑"), and dragging stutters because
/// DWM re-blurs on every move. Plain blur costs nothing and cannot wedge the
/// compositor.</item>
/// </list>
///
/// <para>The WPF surfaces carry their own alpha (ThemeManager's acrylic palette), so
/// the blur still shows through on 10 — just without the noise texture.</para>
///
/// <para>Rounded corners: <c>DWMWA_WINDOW_CORNER_PREFERENCE</c> is honoured on
/// Windows 11; on Windows 10 the call is a harmless no-op and the window stays
/// square — that is an OS limitation, not something a launcher can backport.</para>
/// </summary>
public static class WindowEffects
{
    private const int AccentEnableBlurBehind = 3;          // Windows 10: stable
    private const int AccentEnabledAcrylicBlurBehind = 4;  // Windows 11 only
    private const int DwmwaWindowCornerPreference = 33;
    private const int DwmcpRound = 2;

    [StructLayout(LayoutKind.Sequential)]
    private struct AccentPolicy
    {
        public int AccentState;
        public uint AccentFlags;
        public uint GradientColor;   // AABBGGRR
        public int AnimationId;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct WindowCompositionAttributeData
    {
        public int Attribute;               // WCA_ACCENT_POLICY = 19
        public IntPtr Data;
        public int SizeOfData;
        public IntPtr Unused;
    }

    [DllImport("user32.dll")]
    private static extern bool SetWindowCompositionAttribute(IntPtr hwnd, ref WindowCompositionAttributeData data);

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int value, int size);

    /// <summary>
    /// Enables acrylic blur + rounded corners. Returns a short status string for
    /// the log ("blur on, corners=ok/no-op").
    /// </summary>
    public static string EnableAcrylic(IntPtr hwnd, uint tintAbgr)
    {
        string corners;
        try
        {
            int pref = DwmcpRound;
            int hr = DwmSetWindowAttribute(hwnd, DwmwaWindowCornerPreference, ref pref, sizeof(int));
            // 0 = applied (Win11), 1/E_INVALIDARG etc. = not supported (Win10).
            corners = hr == 0 ? "round" : $"square(hr=0x{hr:X8})";
        }
        catch (Exception ex)
        {
            corners = "square(" + ex.GetType().Name + ")";
        }

        // Windows 11 is build 22000 and up. Everything older gets the plain blur.
        bool windows11 = Environment.OSVersion.Version.Build >= 22000;

        string blur;
        try
        {
            // On 10 the tint is ignored by BLURBEHIND, but a fully transparent
            // gradient has been seen to composite as solid black on some drivers.
            // Keep a floor so the surface can never end up opaque black.
            //
            // The floor is a max(), not an OR: ORing 0x60 into an alpha of 0xB4 gives
            // 0xF4 (96% opaque), which would defeat the tint the caller asked for.
            uint tint = tintAbgr;
            if (!windows11)
            {
                uint current = (tintAbgr >> 24) & 0xFF;
                if (current < 0x60) tint = (tint & 0x00FFFFFFu) | 0x60000000u;
            }

            var policy = new AccentPolicy
            {
                AccentState = windows11 ? AccentEnabledAcrylicBlurBehind : AccentEnableBlurBehind,
                AccentFlags = windows11 ? 2u : 0u,
                GradientColor = tint
            };
            IntPtr ptr = Marshal.AllocHGlobal(Marshal.SizeOf(policy));
            try
            {
                Marshal.StructureToPtr(policy, ptr, false);
                var data = new WindowCompositionAttributeData
                {
                    Attribute = 19,
                    Data = ptr,
                    SizeOfData = Marshal.SizeOf(policy)
                };
                blur = SetWindowCompositionAttribute(hwnd, ref data) ? "on" : "unsupported";
            }
            finally
            {
                Marshal.FreeHGlobal(ptr);
            }
        }
        catch (Exception ex)
        {
            blur = "failed(" + ex.GetType().Name + ")";
        }

        // The build number matters: "一边白一边黑" reports are Windows 10 only, and
        // without this line there is no way to tell which path a user hit.
        return $"{(windows11 ? "acrylic" : "blur")} {blur}, corners {corners}, build {Environment.OSVersion.Version.Build}";
    }
}
