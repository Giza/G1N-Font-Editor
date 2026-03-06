using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;

namespace G1N_Font_Editor.Helpers
{
    /// <summary>
    /// Generates Signed Distance Field (SDF) textures from glyph bitmaps.
    /// Uses the 8-point Sequential Signed Distance Transform (8SSEDT) algorithm.
    /// Similar to Unity TextMesh Pro's SDF generation.
    /// </summary>
    public static class SdfGenerator
    {
        private const int INF = 1000000;

        /// <summary>
        /// Generate an SDF bitmap from a high-resolution glyph bitmap.
        /// </summary>
        /// <param name="highResBitmap">High-resolution rendered glyph (white on black/transparent)</param>
        /// <param name="targetWidth">Output SDF width</param>
        /// <param name="targetHeight">Output SDF height</param>
        /// <param name="spread">Distance spread in output pixels (how far from edge the gradient extends)</param>
        /// <param name="upscale">How much the input is upscaled relative to output</param>
        /// <returns>Grayscale byte array: 128=edge, 255=deep inside, 0=far outside</returns>
        public static byte[] GenerateSdf(Bitmap highResBitmap, int targetWidth, int targetHeight, int spread, int upscale)
        {
            int hiW = highResBitmap.Width;
            int hiH = highResBitmap.Height;

            // Extract alpha/luminance from the high-res bitmap
            byte[] pixels = ExtractLuminance(highResBitmap);

            // Threshold to create binary inside/outside map
            bool[] inside = new bool[hiW * hiH];
            byte threshold = 128;
            for (int i = 0; i < pixels.Length; i++)
            {
                inside[i] = pixels[i] >= threshold;
            }

            // Compute distance transform for outside pixels (distance to nearest inside pixel)
            float[] distOutside = ComputeDistanceField(inside, hiW, hiH, false);

            // Compute distance transform for inside pixels (distance to nearest outside pixel)
            float[] distInside = ComputeDistanceField(inside, hiW, hiH, true);

            // Combine: positive outside, negative inside
            // Then downsample to target size
            float spreadHiRes = spread * upscale;
            byte[] result = new byte[targetWidth * targetHeight];

            for (int ty = 0; ty < targetHeight; ty++)
            {
                for (int tx = 0; tx < targetWidth; tx++)
                {
                    // Sample from the center of the corresponding high-res region
                    float totalDist = 0;
                    int sampleCount = 0;

                    int hiStartX = tx * upscale;
                    int hiStartY = ty * upscale;
                    int hiEndX = Math.Min(hiStartX + upscale, hiW);
                    int hiEndY = Math.Min(hiStartY + upscale, hiH);

                    for (int hy = hiStartY; hy < hiEndY; hy++)
                    {
                        for (int hx = hiStartX; hx < hiEndX; hx++)
                        {
                            int idx = hy * hiW + hx;
                            float dist;
                            if (inside[idx])
                            {
                                dist = -distInside[idx]; // Negative = inside
                            }
                            else
                            {
                                dist = distOutside[idx]; // Positive = outside
                            }
                            totalDist += dist;
                            sampleCount++;
                        }
                    }

                    float avgDist = sampleCount > 0 ? totalDist / sampleCount : 0;

                    // Map distance to 0-255 range
                    // 128 = edge, 255 = deep inside, 0 = far outside
                    float normalized = 0.5f - avgDist / (2.0f * spreadHiRes);
                    normalized = Math.Max(0, Math.Min(1, normalized));
                    result[ty * targetWidth + tx] = (byte)(normalized * 255);
                }
            }

            return result;
        }

        /// <summary>
        /// Renders a glyph character as an SDF bitmap.
        /// Handles upscaling, padding, rendering, and SDF computation.
        /// </summary>
        public static SdfResult RenderGlyphSdf(
            char character,
            Font font,
            System.Windows.Media.GlyphTypeface glyphTypeface,
            int spread,
            int upscale,
            bool is8Bpp)
        {
            IDictionary<int, ushort> characterMap = glyphTypeface.CharacterToGlyphMap;
            ushort glyphIndex;
            if (!characterMap.TryGetValue(character, out glyphIndex))
            {
                return null;
            }

            float fontSize = font.Size;

            // Calculate target (output) dimensions based on glyph metrics
            int targetWidth = (int)Math.Ceiling(
                (glyphTypeface.AdvanceWidths[glyphIndex]
                + Math.Abs(glyphTypeface.LeftSideBearings[glyphIndex])
                + Math.Abs(glyphTypeface.RightSideBearings[glyphIndex]))
                * fontSize
            );
            // Ensure even width for 4bpp
            while ((targetWidth % 2 != 0 && !is8Bpp) || targetWidth <= 0)
                targetWidth++;

            int targetHeight = (int)Math.Ceiling(
                (glyphTypeface.Height
                - (glyphTypeface.TopSideBearings[glyphIndex] < 0 ? glyphTypeface.TopSideBearings[glyphIndex] : 0)
                - (glyphTypeface.BottomSideBearings[glyphIndex] < 0 ? glyphTypeface.BottomSideBearings[glyphIndex] : 0))
                * fontSize
            );
            while (targetHeight % 2 != 0 || targetHeight <= 0)
                targetHeight++;

            // Add padding for spread (so SDF gradient extends outside the glyph)
            int paddedTargetW = targetWidth + spread * 2;
            int paddedTargetH = targetHeight + spread * 2;

            // Ensure even dimensions for 4bpp
            while ((paddedTargetW % 2 != 0 && !is8Bpp) || paddedTargetW <= 0)
                paddedTargetW++;
            while (paddedTargetH % 2 != 0 || paddedTargetH <= 0)
                paddedTargetH++;

            // High-res dimensions
            int hiW = paddedTargetW * upscale;
            int hiH = paddedTargetH * upscale;

            // Render at high resolution
            float hiFontSize = fontSize * upscale;
            Font hiFont = new Font(font.FontFamily, hiFontSize, font.Style);

            var measureSize = FontHelper.MeasureSize(character, hiFont);

            int hiPadding = spread * upscale;
            int startX = hiPadding + (int)Math.Ceiling(
                (targetWidth * upscale - measureSize.Width
                - (Math.Abs(glyphTypeface.RightSideBearings[glyphIndex]) * hiFontSize)) / 2);
            int startY = hiPadding +
                (glyphTypeface.TopSideBearings[glyphIndex] < 0
                    ? Math.Abs((int)(glyphTypeface.TopSideBearings[glyphIndex] * hiFontSize))
                    : 0);

            Bitmap hiBmp = new Bitmap(hiW, hiH);
            hiBmp.SetResolution(72, 72);
            using (var g = Graphics.FromImage(hiBmp))
            {
                g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.AntiAliasGridFit;
                g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.HighQuality;
                var rect = new Rectangle(startX, startY, (int)measureSize.Width, (int)measureSize.Height);
                g.DrawString(character.ToString(), hiFont, Brushes.White, rect);
            }

            hiFont.Dispose();

            // Generate SDF
            byte[] sdfData = GenerateSdf(hiBmp, paddedTargetW, paddedTargetH, spread, upscale);
            hiBmp.Dispose();

            // Create the output bitmap
            Bitmap sdfBmp = new Bitmap(paddedTargetW, paddedTargetH);
            for (int y = 0; y < paddedTargetH; y++)
            {
                for (int x = 0; x < paddedTargetW; x++)
                {
                    byte v = sdfData[y * paddedTargetW + x];
                    sdfBmp.SetPixel(x, y, Color.FromArgb(v, v, v, v));
                }
            }

            // Calculate metrics (account for spread padding)
            byte xAdvance = glyphTypeface.AdvanceWidths[glyphIndex] > 0
                ? (byte)Math.Min(127, Math.Round(
                    (glyphTypeface.AdvanceWidths[glyphIndex]
                    + (glyphTypeface.LeftSideBearings[glyphIndex] >= 0 ? 0 : Math.Abs(glyphTypeface.LeftSideBearings[glyphIndex])))
                    * fontSize + spread * 2))
                : (byte)0;

            sbyte xOffset = glyphTypeface.AdvanceWidths[glyphIndex] > 0
                ? (sbyte)Math.Max(-127, Math.Min(127, Math.Round(
                    (glyphTypeface.LeftSideBearings[glyphIndex] < 0 ? 0 : glyphTypeface.LeftSideBearings[glyphIndex] * fontSize * -1)
                    - spread)))
                : (sbyte)Math.Max(-127, Math.Min(127, Math.Round(glyphTypeface.LeftSideBearings[glyphIndex] * fontSize - spread)));

            sbyte baseline = (sbyte)Math.Min(127, Math.Round(glyphTypeface.Baseline * fontSize + spread));

            return new SdfResult
            {
                Bitmap = sdfBmp,
                PixelData = sdfData,
                Width = (byte)paddedTargetW,
                Height = (byte)paddedTargetH,
                XAdvance = xAdvance,
                XOffset = xOffset,
                Baseline = baseline
            };
        }

        /// <summary>
        /// Compute distance field using 8SSEDT (8-point Signed Sequential Euclidean Distance Transform).
        /// </summary>
        /// <param name="isInside">Binary map: true = inside the glyph</param>
        /// <param name="width">Image width</param>
        /// <param name="height">Image height</param>
        /// <param name="invert">If true, compute distance from inside pixels to nearest outside pixel</param>
        private static float[] ComputeDistanceField(bool[] isInside, int width, int height, bool invert)
        {
            int size = width * height;
            int[] distSq = new int[size];

            // Initialize: 0 for "source" pixels, INF for others
            for (int i = 0; i < size; i++)
            {
                bool isSource = invert ? !isInside[i] : isInside[i];
                distSq[i] = isSource ? 0 : INF;
            }

            // dx/dy offsets for neighbor access
            int[] dx = { -1, 0, 1, -1 };
            int[] dy = { -1, -1, -1, 0 };
            int[] distAdd = { 2, 1, 2, 1 }; // Approximate: 1 for orthogonal, ~1.41 for diagonal

            // Forward pass (top-left to bottom-right)
            for (int y = 0; y < height; y++)
            {
                for (int x = 0; x < width; x++)
                {
                    int idx = y * width + x;
                    for (int n = 0; n < 4; n++)
                    {
                        int nx = x + dx[n];
                        int ny = y + dy[n];
                        if (nx >= 0 && nx < width && ny >= 0 && ny < height)
                        {
                            int nIdx = ny * width + nx;
                            int newDist = distSq[nIdx] + distAdd[n];
                            if (newDist < distSq[idx])
                                distSq[idx] = newDist;
                        }
                    }
                }
            }

            // Backward pass (bottom-right to top-left)
            int[] dx2 = { 1, 0, -1, 1 };
            int[] dy2 = { 1, 1, 1, 0 };

            for (int y = height - 1; y >= 0; y--)
            {
                for (int x = width - 1; x >= 0; x--)
                {
                    int idx = y * width + x;
                    for (int n = 0; n < 4; n++)
                    {
                        int nx = x + dx2[n];
                        int ny = y + dy2[n];
                        if (nx >= 0 && nx < width && ny >= 0 && ny < height)
                        {
                            int nIdx = ny * width + nx;
                            int newDist = distSq[nIdx] + distAdd[n];
                            if (newDist < distSq[idx])
                                distSq[idx] = newDist;
                        }
                    }
                }
            }

            // Convert to float distances
            float[] result = new float[size];
            for (int i = 0; i < size; i++)
            {
                result[i] = (float)Math.Sqrt(distSq[i]);
            }
            return result;
        }

        /// <summary>
        /// Extract luminance (brightness) from bitmap as a byte array.
        /// </summary>
        private static byte[] ExtractLuminance(Bitmap bmp)
        {
            int w = bmp.Width;
            int h = bmp.Height;
            byte[] result = new byte[w * h];

            BitmapData bmpData = bmp.LockBits(
                new Rectangle(0, 0, w, h),
                ImageLockMode.ReadOnly,
                PixelFormat.Format32bppArgb
            );

            int stride = bmpData.Stride;
            byte[] pixelData = new byte[stride * h];
            Marshal.Copy(bmpData.Scan0, pixelData, 0, pixelData.Length);
            bmp.UnlockBits(bmpData);

            for (int y = 0; y < h; y++)
            {
                for (int x = 0; x < w; x++)
                {
                    int offset = y * stride + x * 4;
                    byte B = pixelData[offset];
                    byte G = pixelData[offset + 1];
                    byte R = pixelData[offset + 2];
                    byte A = pixelData[offset + 3];

                    // Use max of luminance and alpha for best edge detection
                    byte lum = (byte)(0.299 * R + 0.587 * G + 0.114 * B);
                    result[y * w + x] = Math.Max(lum, A);
                }
            }

            return result;
        }

        /// <summary>
        /// Result of SDF glyph rendering.
        /// </summary>
        public class SdfResult
        {
            public Bitmap Bitmap { get; set; }
            public byte[] PixelData { get; set; }
            public byte Width { get; set; }
            public byte Height { get; set; }
            public byte XAdvance { get; set; }
            public sbyte XOffset { get; set; }
            public sbyte Baseline { get; set; }
        }
    }
}
