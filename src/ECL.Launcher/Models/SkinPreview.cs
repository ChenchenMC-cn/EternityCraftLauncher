// SPDX-License-Identifier: MPL-2.0
// Copyright (c) 2026 ChenchenMC <https://github.com/ChenchenMC-cn/EternityCraftLauncher>
using System;
using System.IO;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace ECL.Models;

/// <summary>
/// Renders a flat "front view" of a Minecraft skin (and the cape) for the account page.
///
/// <para>This is the classic 16x32 paper-doll layout every launcher shows: head on top,
/// body in the middle, arms to the sides, legs at the bottom. Coordinates are the
/// standard skin UVs, all in a 64x64 sheet; the 64x32 pre-1.8 sheet simply lacks the
/// left-side parts and the second layer, so those are skipped or mirrored.</para>
///
/// <para>Pure pixel work like <see cref="SkinHead"/>: no render thread, no layout.</para>
/// </summary>
public static class SkinPreview
{
    private const int DollWidth = 16;
    private const int DollHeight = 32;

    public static string? LastError { get; private set; }

    public static BitmapSource? Front(string? skinPath)
    {
        LastError = null;
        var sheet = Load(skinPath);
        if (sheet == null) return null;

        try
        {
            var (pixels, width, height) = sheet.Value;
            bool modern = width >= 64 && height >= 64;

            var canvas = new byte[DollWidth * DollHeight * 4];

            // Layer 0: base parts. Layer 1: the overlay ("hat"/"jacket") drawn on top.
            Blit(pixels, width, height, 8, 8, 8, 8, canvas, DollWidth, 4, 0);        // head
            Blit(pixels, width, height, 20, 20, 8, 12, canvas, DollWidth, 4, 8);     // body
            Blit(pixels, width, height, 44, 20, 4, 12, canvas, DollWidth, 0, 8);     // right arm
            Blit(pixels, width, height, 4, 20, 4, 12, canvas, DollWidth, 4, 20);     // right leg

            if (modern)
            {
                Blit(pixels, width, height, 36, 52, 4, 12, canvas, DollWidth, 12, 8);   // left arm
                Blit(pixels, width, height, 20, 52, 4, 12, canvas, DollWidth, 8, 20);   // left leg
            }
            else
            {
                // Legacy sheets have no left limbs; mirroring keeps the doll symmetric.
                Blit(pixels, width, height, 44, 20, 4, 12, canvas, DollWidth, 12, 8);
                Blit(pixels, width, height, 4, 20, 4, 12, canvas, DollWidth, 8, 20);
            }

            if (modern)
            {
                Blit(pixels, width, height, 40, 8, 8, 8, canvas, DollWidth, 4, 0);       // hat
                Blit(pixels, width, height, 20, 36, 8, 12, canvas, DollWidth, 4, 8);     // jacket
                Blit(pixels, width, height, 44, 36, 4, 12, canvas, DollWidth, 0, 8);     // right sleeve
                Blit(pixels, width, height, 52, 52, 4, 12, canvas, DollWidth, 12, 8);    // left sleeve
                Blit(pixels, width, height, 4, 36, 4, 12, canvas, DollWidth, 4, 20);     // right trouser
                Blit(pixels, width, height, 4, 52, 4, 12, canvas, DollWidth, 8, 20);     // left trouser
            }

            return Build(canvas, DollWidth, DollHeight);
        }
        catch (Exception ex)
        {
            LastError = ex.GetType().Name + ": " + ex.Message;
            return null;
        }
    }

    /// <summary>Cape front face, 10x16 at (1,1) on the cape sheet.</summary>
    public static BitmapSource? Cape(string? capePath)
    {
        LastError = null;
        var sheet = Load(capePath);
        if (sheet == null) return null;

        try
        {
            var (pixels, width, height) = sheet.Value;
            if (width < 12 || height < 18)
            {
                LastError = $"cape too small: {width}x{height}";
                return null;
            }

            const int w = 10, h = 16;
            var canvas = new byte[w * h * 4];
            Blit(pixels, width, height, 1, 1, w, h, canvas, w, 0, 0);
            return Build(canvas, w, h);
        }
        catch (Exception ex)
        {
            LastError = ex.GetType().Name + ": " + ex.Message;
            return null;
        }
    }

    // --- plumbing ----------------------------------------------------------

    private static (byte[] Pixels, int Width, int Height)? Load(string? path)
    {
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
        {
            LastError = "file not found";
            return null;
        }

        try
        {
            using var stream = File.OpenRead(path);
            var decoder = BitmapDecoder.Create(stream,
                BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.OnLoad);
            BitmapSource frame = decoder.Frames[0];

            if (frame.Format != PixelFormats.Bgra32)
                frame = new FormatConvertedBitmap(frame, PixelFormats.Bgra32, null, 0);

            int w = frame.PixelWidth, h = frame.PixelHeight;
            if (w < 16 || h < 16)
            {
                LastError = $"too small: {w}x{h}";
                return null;
            }

            var pixels = new byte[w * h * 4];
            frame.CopyPixels(pixels, w * 4, 0);
            return (pixels, w, h);
        }
        catch (Exception ex)
        {
            LastError = ex.GetType().Name + ": " + ex.Message;
            return null;
        }
    }

    /// <summary>Straight-alpha blit of one block onto the canvas.</summary>
    private static void Blit(byte[] sheet, int sheetWidth, int sheetHeight,
        int sx, int sy, int w, int h,
        byte[] canvas, int canvasWidth, int dx, int dy)
    {
        // Both axes matter: a 16x16 sheet used to read past the buffer for the leg
        // blocks at y=20 and throw IndexOutOfRange.
        if (sx < 0 || sy < 0 || sx + w > sheetWidth || sy + h > sheetHeight) return;

        for (int y = 0; y < h; y++)
        {
            for (int x = 0; x < w; x++)
            {
                int src = ((sy + y) * sheetWidth + sx + x) * 4;
                int dst = ((dy + y) * canvasWidth + dx + x) * 4;

                int aTop = sheet[src + 3];
                if (aTop == 0) continue;

                if (aTop == 255)
                {
                    canvas[dst] = sheet[src];
                    canvas[dst + 1] = sheet[src + 1];
                    canvas[dst + 2] = sheet[src + 2];
                    canvas[dst + 3] = 255;
                    continue;
                }

                int aBottom = canvas[dst + 3];
                int outA = aTop + aBottom * (255 - aTop) / 255;
                if (outA == 0) continue;

                for (int c = 0; c < 3; c++)
                {
                    int top = sheet[src + c] * aTop;
                    int bottom = canvas[dst + c] * aBottom * (255 - aTop) / 255;
                    canvas[dst + c] = (byte)Math.Clamp((top + bottom) / outA, 0, 255);
                }
                canvas[dst + 3] = (byte)outA;
            }
        }
    }

    private static BitmapSource Build(byte[] canvas, int width, int height)
    {
        var image = BitmapSource.Create(width, height, 96, 96,
            PixelFormats.Bgra32, null, canvas, width * 4);
        image.Freeze();
        return image;
    }
}
