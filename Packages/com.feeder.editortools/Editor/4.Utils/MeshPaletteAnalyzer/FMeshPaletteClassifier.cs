#if UNITY_EDITOR
using System;
using System.Collections.Generic;
using UnityEngine;

namespace Feeder
{
    /// <summary>
    /// Stateless core: turns a rectangular block of pixels into threshold-independent
    /// <see cref="MeshPaletteMetrics"/>, then turns metrics + thresholds into a verdict.
    /// Kept free of scene/asset access so it can be unit-tested on raw pixel arrays.
    /// </summary>
    public static class FMeshPaletteClassifier
    {
        // Quantization grid (matches TextureRecolorService.DetectDominantClusters: 16 levels/channel).
        private const int Levels = 16;
        // A bucket must hold at least this share of opaque pixels to count as a "significant" color.
        private const float SignificanceFloor = 0.01f;
        // Neighbor-difference bands (max channel delta, 0..1): below Low = flat, Low..High = ramp, above High = hard edge.
        private const float LowDelta = 0.03f;
        private const float HighDelta = 0.14f;
        // Cap on how many bucket shares we keep for coverage math.
        private const int MaxTopShares = 32;

        /// <summary>
        /// Measures a rectangular pixel block. <paramref name="pixels"/> is row-major, length w*h.
        /// Fully transparent regions yield <see cref="MeshPaletteMetrics.sampleCount"/> == 0.
        /// </summary>
        public static MeshPaletteMetrics ComputeMetrics(Color[] pixels, int w, int h)
        {
            var m = new MeshPaletteMetrics();
            if (pixels == null || pixels.Length == 0 || w <= 0 || h <= 0)
                return m;

            // --- Color histogram over opaque pixels ---
            var counts = new int[Levels * Levels * Levels];
            int opaque = 0;
            for (int i = 0; i < pixels.Length; i++)
            {
                Color c = pixels[i];
                if (c.a < 0.5f) continue;
                int r = Mathf.Min(Levels - 1, (int)(c.r * Levels));
                int g = Mathf.Min(Levels - 1, (int)(c.g * Levels));
                int b = Mathf.Min(Levels - 1, (int)(c.b * Levels));
                counts[(r * Levels + g) * Levels + b]++;
                opaque++;
            }

            m.sampleCount = opaque;
            if (opaque == 0)
                return m;

            var shares = new List<float>();
            int significant = 0;
            for (int i = 0; i < counts.Length; i++)
            {
                if (counts[i] == 0) continue;
                float share = (float)counts[i] / opaque;
                if (share >= SignificanceFloor)
                {
                    significant++;
                    shares.Add(share);
                }
            }
            shares.Sort((a, b) => b.CompareTo(a));
            if (shares.Count > MaxTopShares)
                shares.RemoveRange(MaxTopShares, shares.Count - MaxTopShares);
            m.significantColorCount = significant;
            m.topShares = shares.ToArray();

            // --- Neighbor-difference (gradient / edge) analysis ---
            long pairs = 0;
            long flat = 0, mid = 0, edge = 0;
            double deltaSum = 0.0;
            for (int y = 0; y < h; y++)
            {
                int row = y * w;
                for (int x = 0; x < w; x++)
                {
                    Color c = pixels[row + x];
                    if (c.a < 0.5f) continue;

                    if (x + 1 < w)
                        Accumulate(c, pixels[row + x + 1], ref pairs, ref flat, ref mid, ref edge, ref deltaSum);
                    if (y + 1 < h)
                        Accumulate(c, pixels[row + w + x], ref pairs, ref flat, ref mid, ref edge, ref deltaSum);
                }
            }

            if (pairs > 0)
            {
                m.gradientScore = (float)((double)mid / pairs);
                m.noiseScore = (float)((double)edge / pairs);
                m.meanDelta = (float)(deltaSum / pairs);
            }
            return m;
        }

        private static void Accumulate(Color a, Color b, ref long pairs,
            ref long flat, ref long mid, ref long edge, ref double deltaSum)
        {
            if (b.a < 0.5f) return;
            float d = Mathf.Max(Mathf.Abs(a.r - b.r), Mathf.Max(Mathf.Abs(a.g - b.g), Mathf.Abs(a.b - b.b)));
            pairs++;
            deltaSum += d;
            if (d < LowDelta) flat++;
            else if (d < HighDelta) mid++;
            else edge++;
        }

        /// <summary>Decides the verdict from cached metrics and the current thresholds.</summary>
        public static MeshPaletteVerdict Classify(MeshPaletteMetrics m, MeshPaletteThresholds t, out string reason)
        {
            if (m == null || m.sampleCount == 0)
            {
                reason = "Vùng texture rỗng / trong suốt";
                return MeshPaletteVerdict.Skipped;
            }

            float coverage = m.CoverageOfTop(t.maxPaletteColors);
            bool fewColors = m.significantColorCount <= t.maxPaletteColors;
            bool highCoverage = coverage >= t.coverageThreshold;
            bool flat = m.gradientScore <= t.gradFlatThreshold;

            if (fewColors && highCoverage && flat)
            {
                reason = $"{m.significantColorCount} màu phẳng, phủ {coverage:P0}";
                return MeshPaletteVerdict.PaletteReady;
            }

            if (m.gradientScore > t.gradFlatThreshold)
            {
                reason = $"Gradient (ramp {m.gradientScore:P0})";
                return MeshPaletteVerdict.Gradient;
            }

            if (m.significantColorCount > t.maxPaletteColors && m.noiseScore > t.noiseThreshold)
            {
                reason = $"Pattern ({m.significantColorCount} màu, cạnh {m.noiseScore:P0})";
                return MeshPaletteVerdict.Pattern;
            }

            reason = $"Ranh giới ({m.significantColorCount} màu, phủ {coverage:P0})";
            return MeshPaletteVerdict.Uncertain;
        }

        /// <summary>Extracts the sub-rectangle a submesh samples, from the (downscaled) full pixels.
        /// Returns false and leaves the caller to use the whole texture when the UV region is
        /// unusable (no UVs, tiled/out-of-bounds, or degenerate size).</summary>
        public static bool TryExtractUvRegion(Mesh mesh, int materialSlot, Color[] full, int fullW, int fullH,
            out Color[] region, out int regionW, out int regionH, out bool outOfBounds)
        {
            region = null; regionW = 0; regionH = 0; outOfBounds = false;

            Vector2[] uv = mesh.uv;
            if (uv == null || uv.Length == 0)
                return false;

            int slot = Mathf.Clamp(materialSlot, 0, mesh.subMeshCount - 1);
            int[] tris = mesh.GetTriangles(slot);
            if (tris.Length == 0)
                return false;

            float minU = float.MaxValue, minV = float.MaxValue, maxU = float.MinValue, maxV = float.MinValue;
            for (int i = 0; i < tris.Length; i++)
            {
                int idx = tris[i];
                if (idx < 0 || idx >= uv.Length) continue;
                Vector2 c = uv[idx];
                if (c.x < minU) minU = c.x;
                if (c.x > maxU) maxU = c.x;
                if (c.y < minV) minV = c.y;
                if (c.y > maxV) maxV = c.y;
            }
            if (minU > maxU || minV > maxV)
                return false;

            // Any span beyond a single tile means the mesh wraps/tiles the texture -> region is meaningless.
            if (maxU - minU > 1f || maxV - minV > 1f || minU < -0.001f || minV < -0.001f || maxU > 1.001f || maxV > 1.001f)
            {
                outOfBounds = true;
                return false;
            }

            int x0 = Mathf.Clamp(Mathf.FloorToInt(minU * fullW), 0, fullW - 1);
            int x1 = Mathf.Clamp(Mathf.CeilToInt(maxU * fullW), 0, fullW - 1);
            int y0 = Mathf.Clamp(Mathf.FloorToInt(minV * fullH), 0, fullH - 1);
            int y1 = Mathf.Clamp(Mathf.CeilToInt(maxV * fullH), 0, fullH - 1);

            regionW = x1 - x0 + 1;
            regionH = y1 - y0 + 1;
            if (regionW < 2 || regionH < 2)
                return false; // too small to measure gradients meaningfully -> fall back to whole texture

            region = new Color[regionW * regionH];
            for (int y = 0; y < regionH; y++)
            {
                int src = (y0 + y) * fullW + x0;
                Array.Copy(full, src, region, y * regionW, regionW);
            }
            return true;
        }
    }
}
#endif
