#region License Information (GPL v3)

/*
    ShareX - A program that allows you to take screenshots and share any file type
    Copyright (c) 2007-2026 ShareX Team

    This program is free software; you can redistribute it and/or
    modify it under the terms of the GNU General Public License
    as published by the Free Software Foundation; either version 2
    of the License, or (at your option) any later version.

    This program is distributed in the hope that it will be useful,
    but WITHOUT ANY WARRANTY; without even the implied warranty of
    MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE.  See the
    GNU General Public License for more details.

    You should have received a copy of the GNU General Public License
    along with this program; if not, write to the Free Software
    Foundation, Inc., 51 Franklin Street, Fifth Floor, Boston, MA  02110-1301, USA.

    Optionally you can also view the license at <http://www.gnu.org/licenses/>.
*/

#endregion License Information (GPL v3)

using System;
using System.Threading.Tasks;

namespace ShareX.ScreenCaptureLib
{
    /// <summary>
    /// Converts FP16 linear scRGB desktop frames (the Windows HDR composition space, 1.0 == 80 nits)
    /// into perceptually accurate 8-bit sRGB output.
    ///
    /// Design goals:
    /// - SDR content (UI, text) composed at the display's SDR reference white maps 1:1 and stays untouched.
    /// - HDR highlights above reference white are rolled off with a BT.2390-style EETF evaluated in the
    ///   PQ (ST.2084) domain so highlight detail is compressed smoothly instead of clipping to white.
    /// - Hue is preserved by tone mapping luminance and rescaling RGB, with a soft desaturation step for
    ///   out-of-gamut colors instead of per-channel clipping.
    /// - Ordered dithering is applied before 8-bit quantization to avoid banding in smooth gradients.
    /// </summary>
    public static unsafe class HdrToneMapper
    {
        private const int ToneMapLutSize = 4096;
        private const int EncodeLutSize = 16384;

        // Rec.709 / sRGB luminance weights (scRGB uses sRGB primaries)
        private const float LumR = 0.2126f, LumG = 0.7152f, LumB = 0.0722f;

        // ST.2084 (PQ) constants
        private const float PqM1 = 2610f / 16384f;
        private const float PqM2 = 2523f / 4096f * 128f;
        private const float PqC1 = 3424f / 4096f;
        private const float PqC2 = 2413f / 4096f * 32f;
        private const float PqC3 = 2392f / 4096f * 32f;

        private static readonly byte[] encodeLut = BuildEncodeLut();

        // 8x8 Bayer matrix, normalized to [-0.5, 0.5) and scaled to one 8-bit quantization step
        private static readonly float[] bayer8 = BuildBayerMatrix();

        private static byte[] BuildEncodeLut()
        {
            byte[] lut = new byte[EncodeLutSize];

            for (int i = 0; i < EncodeLutSize; i++)
            {
                float linear = i / (float)(EncodeLutSize - 1);
                lut[i] = (byte)Math.Round(SrgbEncode(linear) * 255f);
            }

            return lut;
        }

        private static float[] BuildBayerMatrix()
        {
            int[] bayer =
            {
                 0, 32,  8, 40,  2, 34, 10, 42,
                48, 16, 56, 24, 50, 18, 58, 26,
                12, 44,  4, 36, 14, 46,  6, 38,
                60, 28, 52, 20, 62, 30, 54, 22,
                 3, 35, 11, 43,  1, 33,  9, 41,
                51, 19, 59, 27, 49, 17, 57, 25,
                15, 47,  7, 39, 13, 45,  5, 37,
                63, 31, 55, 23, 61, 29, 53, 21
            };

            float[] matrix = new float[64];

            for (int i = 0; i < 64; i++)
            {
                matrix[i] = ((bayer[i] + 0.5f) / 64f - 0.5f) / 255f;
            }

            return matrix;
        }

        private static float SrgbEncode(float linear)
        {
            if (linear <= 0.0031308f)
            {
                return 12.92f * linear;
            }

            return 1.055f * MathF.Pow(linear, 1f / 2.4f) - 0.055f;
        }

        private static float PqEncode(float nits)
        {
            float y = MathF.Max(nits, 0f) / 10000f;
            float ym = MathF.Pow(y, PqM1);
            return MathF.Pow((PqC1 + PqC2 * ym) / (1f + PqC3 * ym), PqM2);
        }

        private static float PqDecode(float pq)
        {
            float e = MathF.Pow(MathF.Max(pq, 0f), 1f / PqM2);
            float num = MathF.Max(e - PqC1, 0f);
            float den = PqC2 - PqC3 * e;
            return 10000f * MathF.Pow(num / den, 1f / PqM1);
        }

        /// <summary>
        /// Builds a 1D LUT mapping scene luminance (normalized so 1.0 == SDR reference white) to
        /// tone mapped display luminance in [0, 1], using the BT.2390 EETF hermite spline roll-off
        /// evaluated in the PQ domain.
        /// </summary>
        private static float[] BuildToneMapLut(float sdrWhiteNits, float maxContentNits)
        {
            float[] lut = new float[ToneMapLutSize];

            float maxInputNorm = MathF.Max(maxContentNits / sdrWhiteNits, 1f);

            // Source and target ranges in PQ space. The target peak is SDR reference white:
            // everything at or below it must pass through unchanged, everything above is
            // compressed into the headroom between the knee and the target peak.
            float pqSourceMax = PqEncode(maxContentNits);
            float pqTargetMax = PqEncode(sdrWhiteNits);

            if (pqSourceMax <= pqTargetMax + 1e-6f)
            {
                for (int i = 0; i < ToneMapLutSize; i++)
                {
                    lut[i] = MathF.Min(LutIndexToLuminance(i, maxInputNorm), 1f);
                }

                return lut;
            }

            // BT.2390 knee start, expressed on the normalized [0, 1] PQ source range
            float maxLumNorm = pqTargetMax / pqSourceMax;
            float ks = 1.5f * maxLumNorm - 0.5f;

            for (int i = 0; i < ToneMapLutSize; i++)
            {
                float yNorm = LutIndexToLuminance(i, maxInputNorm);
                float nits = yNorm * sdrWhiteNits;
                float e1 = PqEncode(nits) / pqSourceMax;

                float e2;
                if (e1 < ks)
                {
                    e2 = e1;
                }
                else
                {
                    // Hermite spline roll-off from BT.2390-4
                    float t = (e1 - ks) / (1f - ks);
                    float t2 = t * t;
                    float t3 = t2 * t;
                    e2 = (2f * t3 - 3f * t2 + 1f) * ks
                       + (t3 - 2f * t2 + t) * (1f - ks)
                       + (-2f * t3 + 3f * t2) * maxLumNorm;
                }

                float mappedNits = PqDecode(e2 * pqSourceMax);
                lut[i] = MathF.Min(mappedNits / sdrWhiteNits, 1f);
            }

            return lut;
        }

        private static float LutIndexToLuminance(int index, float maxInputNorm)
        {
            // Square distribution gives more LUT precision near zero where the eye is most sensitive
            float t = index / (float)(ToneMapLutSize - 1);
            return t * t * maxInputNorm;
        }

        /// <summary>
        /// Tone maps an FP16 linear scRGB frame region into 8-bit BGRA output.
        /// </summary>
        /// <param name="source">Pointer to the top-left of the FP16 RGBA source frame.</param>
        /// <param name="sourceRowPitch">Source row pitch in bytes.</param>
        /// <param name="sourceRect">Region of the source frame to convert (in source pixel coordinates).</param>
        /// <param name="destination">Pointer to the top-left of the destination BGRA8 buffer region.</param>
        /// <param name="destinationRowPitch">Destination row pitch in bytes.</param>
        /// <param name="sdrWhiteLevelNits">SDR reference white of the source display in nits.</param>
        /// <param name="displayMaxNits">Reported peak luminance of the source display in nits.</param>
        public static void ConvertScRgbToSdr(IntPtr source, int sourceRowPitch, System.Drawing.Rectangle sourceRect,
            IntPtr destination, int destinationRowPitch, float sdrWhiteLevelNits, float displayMaxNits)
        {
            if (sdrWhiteLevelNits < 80f)
            {
                sdrWhiteLevelNits = 80f;
            }

            float maxContentNits = EstimateMaxContentLuminance(source, sourceRowPitch, sourceRect, displayMaxNits);

            // Pure SDR content: nothing exceeds reference white, so tone mapping and gamut
            // mapping are identity operations and the whole analysis pipeline can be skipped
            if (maxContentNits <= sdrWhiteLevelNits * 1.001f)
            {
                ConvertSdrFastPath(source, sourceRowPitch, sourceRect, destination, destinationRowPitch, sdrWhiteLevelNits);
                return;
            }

            float[] toneMapLut = BuildToneMapLut(sdrWhiteLevelNits, maxContentNits);

            float scRgbToRef = 80f / sdrWhiteLevelNits;
            float maxInputNorm = MathF.Max(maxContentNits / sdrWhiteLevelNits, 1f);
            float lutScale = (ToneMapLutSize - 1) / MathF.Sqrt(maxInputNorm);

            byte* srcBase = (byte*)source;
            byte* dstBase = (byte*)destination;
            byte[] encode = encodeLut;
            float[] bayer = bayer8;

            Parallel.For(0, sourceRect.Height, y =>
            {
                ushort* srcRow = (ushort*)(srcBase + (long)(sourceRect.Y + y) * sourceRowPitch) + sourceRect.X * 4;
                byte* dstRow = dstBase + (long)y * destinationRowPitch;
                int bayerRow = (y & 7) << 3;

                for (int x = 0; x < sourceRect.Width; x++)
                {
                    float r = (float)BitConverter.UInt16BitsToHalf(srcRow[0]) * scRgbToRef;
                    float g = (float)BitConverter.UInt16BitsToHalf(srcRow[1]) * scRgbToRef;
                    float b = (float)BitConverter.UInt16BitsToHalf(srcRow[2]) * scRgbToRef;

                    if (r < 0f) r = 0f;
                    if (g < 0f) g = 0f;
                    if (b < 0f) b = 0f;

                    float lum = LumR * r + LumG * g + LumB * b;

                    if (lum > 1e-6f)
                    {
                        // LUT is indexed on sqrt(luminance) to match the squared LUT distribution
                        float lutPos = MathF.Sqrt(MathF.Min(lum, maxInputNorm)) * lutScale;
                        int lutIndex = (int)lutPos;
                        float frac = lutPos - lutIndex;
                        int lutNext = Math.Min(lutIndex + 1, ToneMapLutSize - 1);
                        float mappedLum = toneMapLut[lutIndex] * (1f - frac) + toneMapLut[lutNext] * frac;

                        float scale = mappedLum / lum;
                        r *= scale;
                        g *= scale;
                        b *= scale;

                        // Soft gamut mapping: desaturate towards the tone mapped luminance until
                        // the color fits, instead of clipping channels independently (hue shifts)
                        float maxChannel = MathF.Max(r, MathF.Max(g, b));
                        if (maxChannel > 1f)
                        {
                            float t = (maxChannel - 1f) / MathF.Max(maxChannel - mappedLum, 1e-6f);
                            if (t > 1f) t = 1f;
                            r += (mappedLum - r) * t;
                            g += (mappedLum - g) * t;
                            b += (mappedLum - b) * t;

                            if (r > 1f) r = 1f;
                            if (g > 1f) g = 1f;
                            if (b > 1f) b = 1f;
                        }
                    }

                    float dither = bayer[bayerRow + (x & 7)];

                    dstRow[0] = EncodeChannel(b, dither, encode);
                    dstRow[1] = EncodeChannel(g, dither, encode);
                    dstRow[2] = EncodeChannel(r, dither, encode);
                    dstRow[3] = 255;

                    srcRow += 4;
                    dstRow += 4;
                }
            });
        }

        private static void ConvertSdrFastPath(IntPtr source, int sourceRowPitch, System.Drawing.Rectangle sourceRect,
            IntPtr destination, int destinationRowPitch, float sdrWhiteLevelNits)
        {
            float scRgbToRef = 80f / sdrWhiteLevelNits;

            byte* srcBase = (byte*)source;
            byte* dstBase = (byte*)destination;
            byte[] encode = encodeLut;
            float[] bayer = bayer8;

            Parallel.For(0, sourceRect.Height, y =>
            {
                ushort* srcRow = (ushort*)(srcBase + (long)(sourceRect.Y + y) * sourceRowPitch) + sourceRect.X * 4;
                byte* dstRow = dstBase + (long)y * destinationRowPitch;
                int bayerRow = (y & 7) << 3;

                for (int x = 0; x < sourceRect.Width; x++)
                {
                    float r = (float)BitConverter.UInt16BitsToHalf(srcRow[0]) * scRgbToRef;
                    float g = (float)BitConverter.UInt16BitsToHalf(srcRow[1]) * scRgbToRef;
                    float b = (float)BitConverter.UInt16BitsToHalf(srcRow[2]) * scRgbToRef;

                    if (r < 0f) r = 0f; else if (r > 1f) r = 1f;
                    if (g < 0f) g = 0f; else if (g > 1f) g = 1f;
                    if (b < 0f) b = 0f; else if (b > 1f) b = 1f;

                    float dither = bayer[bayerRow + (x & 7)];

                    dstRow[0] = EncodeChannel(b, dither, encode);
                    dstRow[1] = EncodeChannel(g, dither, encode);
                    dstRow[2] = EncodeChannel(r, dither, encode);
                    dstRow[3] = 255;

                    srcRow += 4;
                    dstRow += 4;
                }
            });
        }

        private static byte EncodeChannel(float linear, float dither, byte[] encode)
        {
            int index = (int)(linear * (EncodeLutSize - 1));
            float encoded = encode[index] / 255f + dither;

            if (encoded <= 0f) return 0;
            if (encoded >= 1f) return 255;

            return (byte)(encoded * 255f + 0.5f);
        }

        /// <summary>
        /// Estimates the peak content luminance of the frame region (99.99th percentile) so the
        /// tone curve only compresses as much as the content actually requires.
        /// </summary>
        private static float EstimateMaxContentLuminance(IntPtr source, int sourceRowPitch,
            System.Drawing.Rectangle sourceRect, float displayMaxNits)
        {
            const int HistogramSize = 1024;
            const float MaxTrackedNits = 10000f;

            byte* srcBase = (byte*)source;

            // Coarse subsampling is sufficient for a percentile estimate and keeps analysis cheap
            int stepY = Math.Max(sourceRect.Height / 512, 1);
            int stepX = Math.Max(sourceRect.Width / 512, 1);

            int[] histogram = new int[HistogramSize];
            long samples = 0;

            // Bins are distributed on sqrt(luminance); any monotonic transform yields the same
            // percentile, and this avoids two pow() calls per sample that PQ binning would cost
            float binScale = (HistogramSize - 1) / MathF.Sqrt(MaxTrackedNits);

            for (int y = 0; y < sourceRect.Height; y += stepY)
            {
                ushort* srcRow = (ushort*)(srcBase + (long)(sourceRect.Y + y) * sourceRowPitch) + sourceRect.X * 4;

                for (int x = 0; x < sourceRect.Width; x += stepX)
                {
                    ushort* px = srcRow + (long)x * 4;
                    float r = (float)BitConverter.UInt16BitsToHalf(px[0]);
                    float g = (float)BitConverter.UInt16BitsToHalf(px[1]);
                    float b = (float)BitConverter.UInt16BitsToHalf(px[2]);

                    float nits = 80f * (LumR * MathF.Max(r, 0f) + LumG * MathF.Max(g, 0f) + LumB * MathF.Max(b, 0f));
                    int bin = (int)(MathF.Sqrt(MathF.Min(nits, MaxTrackedNits)) * binScale);
                    histogram[bin]++;
                    samples++;
                }
            }

            if (samples == 0)
            {
                return displayMaxNits > 0f ? displayMaxNits : 1000f;
            }

            long threshold = (long)(samples * 0.0001f);
            long count = 0;
            int peakBin = 0;

            for (int i = HistogramSize - 1; i >= 0; i--)
            {
                count += histogram[i];
                if (count > threshold)
                {
                    peakBin = i;
                    break;
                }
            }

            float sqrtPeak = (peakBin + 1) / ((HistogramSize - 1) / MathF.Sqrt(MaxTrackedNits));
            float peakNits = sqrtPeak * sqrtPeak;

            if (displayMaxNits > 0f)
            {
                peakNits = MathF.Min(peakNits, displayMaxNits);
            }

            return MathF.Max(peakNits, 80f);
        }
    }
}
