// SPDX-License-Identifier: MPL-2.0
// Copyright (c) 2026 ChenchenMC <https://github.com/ChenchenMC-cn/EternityCraftLauncher>
using System;
using System.IO;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace ECL.Models;

/// <summary>
/// Turns a 64x64 skin PNG into the 8x8 face launchers show as an avatar.
///
/// <para>The head occupies (8,8)-(16,16); the hat overlay sits at (40,8). Compositing
/// both gives the same picture PCL and the vanilla launcher show, including a hat when
/// the skin has one.</para>
///
/// <para>Done on raw pixels rather than through <c>CroppedBitmap</c> +
/// <c>RenderTargetBitmap</c>: that route threw <c>ArgumentNullException (Parameter
/// 'key')</c> when called from the headless self-test. Straight alpha blending is a
/// dozen lines of arithmetic and works on any thread.</para>
/// </summary>
public static class SkinHead
{
    private const int FaceSize = 8;
    private const int SheetSize = 64;

    /// <summary>Last failure, for the self-test and the log. Never shown to users.</summary>
    public static string? LastError { get; private set; }

    /// <summary>Returns null when the file is not a skin this code can crop.</summary>
    public static BitmapSource? FromSkinFile(string? pngPath)
    {
        LastError = null;

        if (string.IsNullOrWhiteSpace(pngPath) || !File.Exists(pngPath))
        {
            LastError = "file not found";
            return null;
        }

        try
        {
            int width, height;
            byte[] sheet;

            using (var stream = File.OpenRead(pngPath))
            {
                var decoder = BitmapDecoder.Create(stream,
                    BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.OnLoad);
                BitmapSource frame = decoder.Frames[0];

                // Anything that is not 32-bit BGRA is converted first; CopyPixels on the
                // raw frame would hand back the file's own format (indexed, 16bpp...).
                if (frame.Format != PixelFormats.Bgra32)
                    frame = new FormatConvertedBitmap(frame, PixelFormats.Bgra32, null, 0);

                width = frame.PixelWidth;
                height = frame.PixelHeight;

                if (width < 16 || height < 16)
                {
                    LastError = $"too small: {width}x{height}";
                    return null;
                }

                sheet = new byte[width * height * 4];
                frame.CopyPixels(sheet, width * 4, 0);
            }

            var face = Block(sheet, width, 8, 8, FaceSize);

            // The second layer only exists on the 64x64 format.
            if (width >= SheetSize && height >= SheetSize)
                BlendOver(face, Block(sheet, width, 40, 8, FaceSize));

            var result = BitmapSource.Create(FaceSize, FaceSize, 96, 96,
                PixelFormats.Bgra32, null, face, FaceSize * 4);
            result.Freeze();
            return result;
        }
        catch (Exception ex)
        {
            LastError = ex.GetType().Name + ": " + ex.Message;
            return null;
        }
    }

    /// <summary>Writes the face next to the skin so the next start needs no network.</summary>
    public static string? SaveFace(string skinPath, string destPngPath)
    {
        var face = FromSkinFile(skinPath);
        if (face == null) return null;

        try
        {
            string? dir = Path.GetDirectoryName(destPngPath);
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);

            var encoder = new PngBitmapEncoder();
            encoder.Frames.Add(BitmapFrame.Create(face));

            using var stream = File.Create(destPngPath);
            encoder.Save(stream);
            return destPngPath;
        }
        catch (Exception ex)
        {
            LastError = ex.GetType().Name + ": " + ex.Message;
            return null;
        }
    }

    /// <summary>Copies a square block out of the sheet, BGRA order.</summary>
    private static byte[] Block(byte[] sheet, int stride, int left, int top, int size)
    {
        var block = new byte[size * size * 4];
        for (int y = 0; y < size; y++)
        {
            int srcRow = ((top + y) * stride + left) * 4;
            Array.Copy(sheet, srcRow, block, y * size * 4, size * 4);
        }
        return block;
    }

    /// <summary>Straight-alpha "source over destination", applied in place.</summary>
    private static void BlendOver(byte[] below, byte[] above)
    {
        for (int i = 0; i < below.Length; i += 4)
        {
            int aTop = above[i + 3];
            if (aTop == 0) continue;

            if (aTop == 255)
            {
                below[i] = above[i];
                below[i + 1] = above[i + 1];
                below[i + 2] = above[i + 2];
                below[i + 3] = 255;
                continue;
            }

            int aBottom = below[i + 3];
            int outA = aTop + aBottom * (255 - aTop) / 255;
            if (outA == 0) continue;

            for (int c = 0; c < 3; c++)
            {
                int top = above[i + c] * aTop;
                int bottom = below[i + c] * aBottom * (255 - aTop) / 255;
                below[i + c] = (byte)Math.Clamp((top + bottom) / outA, 0, 255);
            }
            below[i + 3] = (byte)outA;
        }
    }
}
