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
        /// Builds the per-pixel classification cache: for every pixel, the nearest cluster by HSV
        /// distance over ALL clusters and its mask weight. The result depends only on the cluster
        /// reference colors and the mask settings — not on the chosen replacement colors — so it
        /// survives color-picker edits. Runs in parallel; touches no Unity objects.
        /// </summary>
        public static RecolorMaskCache BuildMaskCache(Color[] pixels, IReadOnlyList<RecolorCluster> clusters,
            RecolorMaskSettings mask)
        {
            int n = clusters != null ? clusters.Count : 0;
            var refH = new float[n];
            var refS = new float[n];
            var refV = new float[n];
            for (int c = 0; c < n; c++)
                if (clusters[c] != null)
                    Color.RGBToHSV((Color)clusters[c].color, out refH[c], out refS[c], out refV[c]);

            var cache = new RecolorMaskCache
            {
                clusterIndex = new sbyte[pixels.Length],
                weight = new byte[pixels.Length],
                pixelsPerCluster = new int[n]
            };

            const int chunkSize = 65536;
            int chunkCount = (pixels.Length + chunkSize - 1) / chunkSize;
            object gate = new object();

            System.Threading.Tasks.Parallel.For(0, chunkCount,
                () => (counts: new int[n], opaque: 0),
                (chunk, _, local) =>
                {
                    int start = chunk * chunkSize;
                    int end = Mathf.Min(start + chunkSize, pixels.Length);
                    for (int i = start; i < end; i++)
                    {
                        var pixel = pixels[i];
                        Color.RGBToHSV(pixel, out float h, out float s, out float v);

                        float bestDist = float.MaxValue;
                        int nearest = -1;
                        for (int c = 0; c < n; c++)
                        {
                            if (clusters[c] == null) continue;
                            float d = MaskDistance(h, s, v, refH[c], refS[c], refV[c], mask);
                            if (d < bestDist)
                            {
                                bestDist = d;
                                nearest = c;
                            }
                        }

                        float w = nearest < 0 ? 0f
                            : Mathf.Clamp01((1f + mask.softness - bestDist) / mask.softness);
                        if (w <= 0f)
                            nearest = -1;

                        cache.clusterIndex[i] = (sbyte)nearest;
                        cache.weight[i] = (byte)Mathf.RoundToInt(w * 255f);

                        if (pixel.a >= 0.5f)
                        {
                            local.opaque++;
                            if (nearest >= 0 && cache.weight[i] >= 128)
                                local.counts[nearest]++;
                        }
                    }
                    return local;
                },
                local =>
                {
                    lock (gate)
                    {
                        cache.opaqueCount += local.opaque;
                        for (int c = 0; c < n; c++)
                            cache.pixelsPerCluster[c] += local.counts[c];
                    }
                });

            return cache;
        }

        /// <summary>
        /// Recolors pixels for every changed cluster in one pass, building the classification
        /// cache internally. Prefer the cache overload when the same pixels are recolored repeatedly.
        /// </summary>
        public static Color[] RecolorMulti(Color[] source, IReadOnlyList<RecolorCluster> clusters,
            RecolorMaskSettings mask, bool maskOnly, Action<float> progress = null)
        {
            return RecolorMulti(source, clusters, mask, maskOnly, BuildMaskCache(source, clusters, mask), progress);
        }

        /// <summary>
        /// Recolors pixels using a prebuilt classification cache. Each pixel was assigned to its
        /// nearest cluster (changed or not); only pixels whose cluster is changed are recolored, so
        /// unchanged clusters keep their own pixels and the mask matches the displayed coverage.
        /// Shading is preserved by scaling the target color with a scalar brightness ratio
        /// (pixel luminance / reference luminance, clamped by shadingClamp), so the recolored pixel
        /// keeps the exact hue/saturation of the picked color and only its lightness follows the
        /// source. With <paramref name="maskOnly"/> the mask is returned as grayscale.
        /// </summary>
        public static Color[] RecolorMulti(Color[] source, IReadOnlyList<RecolorCluster> clusters,
            RecolorMaskSettings mask, bool maskOnly, RecolorMaskCache cache, Action<float> progress = null)
        {
            int n = clusters != null ? clusters.Count : 0;
            var isChanged = new bool[n];
            var refLum = new float[n];
            var tgtLin = new Color[n];
            bool anyChanged = false;
            for (int c = 0; c < n; c++)
            {
                if (clusters[c] == null) continue;
                var lin = ((Color)clusters[c].color).linear;
                refLum[c] = Mathf.Max(LinearLuminance(lin), 0.0001f);
                tgtLin[c] = clusters[c].newColor.linear;
                isChanged[c] = clusters[c].Changed;
                anyChanged |= isChanged[c];
            }

            var result = new Color[source.Length];
            if (!anyChanged)
            {
                if (maskOnly)
                    for (int i = 0; i < source.Length; i++)
                        result[i] = new Color(0f, 0f, 0f, 1f);
                else
                    Array.Copy(source, result, source.Length);
                return result;
            }

            int progressStep = Mathf.Max(1, source.Length / 20);
            for (int i = 0; i < source.Length; i++)
            {
                if (progress != null && i % progressStep == 0)
                    progress((float)i / source.Length);

                var pixel = source[i];
                int cluster = cache.clusterIndex[i];
                float weight = cluster >= 0 && isChanged[cluster] ? cache.weight[i] / 255f : 0f;

                if (maskOnly)
                {
                    result[i] = new Color(weight, weight, weight, 1f);
                    continue;
                }

                if (weight <= 0f)
                {
                    result[i] = pixel;
                    continue;
                }

                Color lin = pixel.linear;
                // scalar brightness ratio keeps the target hue/saturation intact and only
                // reproduces the source shading (a per-channel ratio would drag the source hue in)
                float shading = Mathf.Min(LinearLuminance(lin) / refLum[cluster], mask.shadingClamp);
                var recoloredLin = new Color(
                    Mathf.Clamp01(tgtLin[cluster].r * shading),
                    Mathf.Clamp01(tgtLin[cluster].g * shading),
                    Mathf.Clamp01(tgtLin[cluster].b * shading),
                    lin.a);
                var final = Color.Lerp(pixel, recoloredLin.gamma, weight);
                final.a = pixel.a;
                result[i] = final;
            }

            return result;
        }

        /// <summary>Rec. 709 relative luminance of a linear-space color.</summary>
        private static float LinearLuminance(in Color linear)
        {
            return 0.2126f * linear.r + 0.7152f * linear.g + 0.0722f * linear.b;
        }

        private static float MaskDistance(float h, float s, float v,
            float refH, float refS, float refV, in RecolorMaskSettings mask)
        {
            float hueDist = Mathf.Abs(Mathf.DeltaAngle(h * 360f, refH * 360f)) / Mathf.Max(1f, mask.hueToleranceDeg);
            float satDist = Mathf.Abs(s - refS) / mask.satTolerance;
            float valDist = Mathf.Abs(v - refV) / mask.valTolerance;

            // very dark pixels have unreliable hue (eyebrows, lashes) - rely on value distance
            if (v < 0.12f) hueDist = 0f;

            return Mathf.Max(hueDist, Mathf.Max(satDist, valDist));
        }
    }

    /// <summary>
    /// Per-pixel mask classification, index-aligned with the pixel array it was built from.
    /// Depends only on the cluster reference colors and mask settings, so edits to the
    /// replacement colors can reuse it.
    /// </summary>
    public sealed class RecolorMaskCache
    {
        /// <summary>Nearest cluster per pixel, -1 when out of tolerance of every cluster.</summary>
        public sbyte[] clusterIndex;
        /// <summary>Mask weight per pixel, 0..255.</summary>
        public byte[] weight;
        /// <summary>Opaque pixels with weight >= 0.5 per cluster.</summary>
        public int[] pixelsPerCluster;
        public int opaqueCount;

        /// <summary>Fraction of opaque pixels the mask would recolor for the currently changed clusters.</summary>
        public float CoverageOf(IReadOnlyList<RecolorCluster> clusters)
        {
            if (opaqueCount == 0 || clusters == null)
                return 0f;

            int sum = 0;
            int n = Mathf.Min(clusters.Count, pixelsPerCluster.Length);
            for (int c = 0; c < n; c++)
                if (clusters[c] != null && clusters[c].Changed)
                    sum += pixelsPerCluster[c];
            return (float)sum / opaqueCount;
        }
    }
}
#endif
