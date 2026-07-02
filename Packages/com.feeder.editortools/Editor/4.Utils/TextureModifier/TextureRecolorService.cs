#if UNITY_EDITOR
using System;
using System.Collections.Generic;
using UnityEngine;

namespace Feeder
{
    /// <summary>Mask parameters for HSV-based texture recoloring.</summary>
    [Serializable]
    public struct RecolorMaskSettings
    {
        public float hueToleranceDeg;
        public float satTolerance;
        public float valTolerance;
        public float softness;
        public float shadingClamp;

        public static RecolorMaskSettings Default => new RecolorMaskSettings
        {
            hueToleranceDeg = 30f,
            satTolerance = 0.35f,
            valTolerance = 0.35f,
            softness = 0.3f,
            shadingClamp = 2f
        };
    }

    /// <summary>A dominant color group detected in a texture, with its user-edited replacement.</summary>
    public sealed class RecolorCluster
    {
        public Color32 color;
        public Color newColor;
        public int pixelCount;
        public float coverage;

        public bool Changed => !FTextureModifierUtils.ApproxEqual(newColor, (Color)color);
    }

    /// <summary>
    /// Stateless texture recolor algorithms: reads pixels through a blit (works with any
    /// compression / Read-Write flag), detects dominant color clusters, and recolors pixels
    /// per cluster with an HSV mask while preserving the source shading.
    /// </summary>
    public static class TextureRecolorService
    {
        /// <summary>Reads pixels regardless of the texture's Read/Write import flag or compression.</summary>
        public static Color[] ReadPixelsViaBlit(Texture2D texture)
        {
            var rt = RenderTexture.GetTemporary(texture.width, texture.height, 0,
                RenderTextureFormat.ARGB32, RenderTextureReadWrite.sRGB);
            var previous = RenderTexture.active;
            Graphics.Blit(texture, rt);
            RenderTexture.active = rt;
            var readable = new Texture2D(texture.width, texture.height, TextureFormat.RGBA32, false);
            readable.ReadPixels(new Rect(0, 0, texture.width, texture.height), 0, 0);
            readable.Apply();
            RenderTexture.active = previous;
            RenderTexture.ReleaseTemporary(rt);
            var pixels = readable.GetPixels();
            UnityEngine.Object.DestroyImmediate(readable);
            return pixels;
        }

        /// <summary>Downscales pixels by nearest sampling so the largest side is at most <paramref name="maxSize"/>.</summary>
        public static Color[] Downscale(Color[] full, int fullWidth, int fullHeight, int maxSize,
            out int width, out int height)
        {
            int stride = Mathf.Max(1, Mathf.Max(fullWidth, fullHeight) / maxSize);
            width = Mathf.Max(1, fullWidth / stride);
            height = Mathf.Max(1, fullHeight / stride);
            var result = new Color[width * height];
            for (int y = 0; y < height; y++)
            {
                int srcRow = y * stride * fullWidth;
                for (int x = 0; x < width; x++)
                    result[y * width + x] = full[srcRow + x * stride];
            }
            return result;
        }

        /// <summary>
        /// Detects the dominant color clusters of a texture by quantizing samples into a coarse
        /// RGB grid and merging HSV-similar buckets. Merge thresholds stay well below the default
        /// mask tolerances so the masks of accepted clusters do not fight each other.
        /// </summary>
        public static List<RecolorCluster> DetectDominantClusters(
            Color[] pixels, int maxClusters = 10, float minShare = 0.005f)
        {
            const int levels = 16;
            const float mergeHueDeg = 15f;
            const float mergeSat = 0.15f;
            const float mergeVal = 0.15f;

            var clusters = new List<RecolorCluster>();
            if (pixels == null || pixels.Length == 0)
                return clusters;

            var counts = new int[levels * levels * levels];
            var sums = new Vector3[levels * levels * levels];
            int opaqueSamples = 0;
            int stride = Mathf.Max(1, pixels.Length / 250000);
            for (int i = 0; i < pixels.Length; i += stride)
            {
                var c = pixels[i];
                if (c.a < 0.5f) continue;
                int r = Mathf.Min(levels - 1, (int)(c.r * levels));
                int g = Mathf.Min(levels - 1, (int)(c.g * levels));
                int b = Mathf.Min(levels - 1, (int)(c.b * levels));
                int key = (r * levels + g) * levels + b;
                counts[key]++;
                sums[key] += new Vector3(c.r, c.g, c.b);
                opaqueSamples++;
            }

            if (opaqueSamples == 0)
                return clusters;

            var order = new List<int>();
            for (int i = 0; i < counts.Length; i++)
                if (counts[i] > 0)
                    order.Add(i);
            order.Sort((a, b) => counts[b].CompareTo(counts[a]));

            // accepted cluster accumulators (index-aligned with clusters)
            var accSums = new List<Vector3>();
            var accCounts = new List<int>();

            foreach (int key in order)
            {
                var avg = sums[key] / counts[key];
                var candidate = new Color(avg.x, avg.y, avg.z, 1f);
                Color.RGBToHSV(candidate, out float ch, out float cs, out float cv);

                int mergeInto = -1;
                for (int i = 0; i < clusters.Count; i++)
                {
                    var accAvg = accSums[i] / accCounts[i];
                    Color.RGBToHSV(new Color(accAvg.x, accAvg.y, accAvg.z), out float ah, out float asat, out float av);
                    float hueDist = Mathf.Abs(Mathf.DeltaAngle(ch * 360f, ah * 360f));
                    // dark pixels have unreliable hue - rely on value distance only
                    if (cv < 0.12f && av < 0.12f) hueDist = 0f;
                    if (hueDist <= mergeHueDeg && Mathf.Abs(cs - asat) <= mergeSat && Mathf.Abs(cv - av) <= mergeVal)
                    {
                        mergeInto = i;
                        break;
                    }
                }

                if (mergeInto >= 0)
                {
                    accSums[mergeInto] += sums[key];
                    accCounts[mergeInto] += counts[key];
                    continue;
                }

                if (clusters.Count >= maxClusters)
                    continue;
                if ((float)counts[key] / opaqueSamples < minShare)
                    break; // buckets are sorted by count, the rest are even smaller

                clusters.Add(new RecolorCluster());
                accSums.Add(sums[key]);
                accCounts.Add(counts[key]);
            }

            for (int i = 0; i < clusters.Count; i++)
            {
                var avg = accSums[i] / accCounts[i];
                var c = new Color(avg.x, avg.y, avg.z, 1f);
                clusters[i].color = FTextureModifierUtils.ToColor32(c, 255);
                clusters[i].newColor = (Color)clusters[i].color;
                clusters[i].pixelCount = accCounts[i];
                clusters[i].coverage = (float)accCounts[i] / opaqueSamples;
            }

            clusters.Sort((a, b) => b.pixelCount.CompareTo(a.pixelCount));
            return clusters;
        }

        /// <summary>
        /// Recolors pixels for every changed cluster in one pass. Each pixel is masked against all
        /// changed clusters and the highest-weight cluster wins; the recolor happens in linear space
        /// (target * source / reference, clamped by shadingClamp) so shading is preserved.
        /// With <paramref name="maskOnly"/> the combined mask is returned as grayscale.
        /// </summary>
        public static Color[] RecolorMulti(Color[] source, IReadOnlyList<RecolorCluster> clusters,
            RecolorMaskSettings mask, bool maskOnly, Action<float> progress = null)
        {
            var changed = new List<RecolorCluster>();
            if (clusters != null)
                foreach (var c in clusters)
                    if (c != null && c.Changed)
                        changed.Add(c);

            var result = new Color[source.Length];
            if (changed.Count == 0)
            {
                if (maskOnly)
                    for (int i = 0; i < source.Length; i++)
                        result[i] = new Color(0f, 0f, 0f, 1f);
                else
                    Array.Copy(source, result, source.Length);
                return result;
            }

            int n = changed.Count;
            var refH = new float[n];
            var refS = new float[n];
            var refV = new float[n];
            var refLin = new Color[n];
            var tgtLin = new Color[n];
            for (int i = 0; i < n; i++)
            {
                var reference = (Color)changed[i].color;
                Color.RGBToHSV(reference, out refH[i], out refS[i], out refV[i]);
                var lin = reference.linear;
                lin.r = Mathf.Max(lin.r, 0.001f);
                lin.g = Mathf.Max(lin.g, 0.001f);
                lin.b = Mathf.Max(lin.b, 0.001f);
                refLin[i] = lin;
                tgtLin[i] = changed[i].newColor.linear;
            }

            int progressStep = Mathf.Max(1, source.Length / 20);
            for (int i = 0; i < source.Length; i++)
            {
                if (progress != null && i % progressStep == 0)
                    progress((float)i / source.Length);

                var pixel = source[i];
                Color.RGBToHSV(pixel, out float h, out float s, out float v);

                float bestWeight = 0f;
                int bestCluster = -1;
                for (int c = 0; c < n; c++)
                {
                    float weight = MaskWeight(h, s, v, refH[c], refS[c], refV[c], mask);
                    if (weight > bestWeight)
                    {
                        bestWeight = weight;
                        bestCluster = c;
                    }
                }

                if (maskOnly)
                {
                    result[i] = new Color(bestWeight, bestWeight, bestWeight, 1f);
                    continue;
                }

                if (bestCluster < 0)
                {
                    result[i] = pixel;
                    continue;
                }

                Color lin = pixel.linear;
                var recoloredLin = new Color(
                    Mathf.Clamp01(tgtLin[bestCluster].r * Mathf.Min(lin.r / refLin[bestCluster].r, mask.shadingClamp)),
                    Mathf.Clamp01(tgtLin[bestCluster].g * Mathf.Min(lin.g / refLin[bestCluster].g, mask.shadingClamp)),
                    Mathf.Clamp01(tgtLin[bestCluster].b * Mathf.Min(lin.b / refLin[bestCluster].b, mask.shadingClamp)),
                    lin.a);
                var final = Color.Lerp(pixel, recoloredLin.gamma, bestWeight);
                final.a = pixel.a;
                result[i] = final;
            }

            return result;
        }

        private static float MaskWeight(float h, float s, float v,
            float refH, float refS, float refV, in RecolorMaskSettings mask)
        {
            float hueDist = Mathf.Abs(Mathf.DeltaAngle(h * 360f, refH * 360f)) / Mathf.Max(1f, mask.hueToleranceDeg);
            float satDist = Mathf.Abs(s - refS) / mask.satTolerance;
            float valDist = Mathf.Abs(v - refV) / mask.valTolerance;

            // very dark pixels have unreliable hue (eyebrows, lashes) - rely on value distance
            if (v < 0.12f) hueDist = 0f;

            float d = Mathf.Max(hueDist, Mathf.Max(satDist, valDist));
            return Mathf.Clamp01((1f + mask.softness - d) / mask.softness);
        }
    }
}
#endif
