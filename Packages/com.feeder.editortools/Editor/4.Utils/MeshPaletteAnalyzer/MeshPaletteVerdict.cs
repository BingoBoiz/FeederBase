#if UNITY_EDITOR
using UnityEngine;

namespace Feeder
{
    /// <summary>
    /// Outcome of analyzing whether a mesh's texture region can be reduced to a small
    /// solid-color palette (the input the existing MeshColorRemapper pipeline expects).
    /// </summary>
    public enum MeshPaletteVerdict
    {
        /// <summary>Few flat colors, high coverage, no smooth ramps: safe to convert.</summary>
        PaletteReady,
        /// <summary>Smooth continuous color ramps: breaks when flattened to a palette.</summary>
        Gradient,
        /// <summary>Many distinct colors with high-frequency detail (patterns / photos).</summary>
        Pattern,
        /// <summary>Borderline metrics or unreliable UV region: needs a human check.</summary>
        Uncertain,
        /// <summary>Could not analyze (embedded texture, empty/transparent region, ...).</summary>
        Skipped
    }

    /// <summary>Tunable decision thresholds. Only the classifier's verdict step reads these,
    /// so changing them re-classifies from cached metrics without re-reading any pixels
    /// (except <see cref="downscaleSize"/>, which changes what pixels are sampled).</summary>
    [System.Serializable]
    public struct MeshPaletteThresholds
    {
        /// <summary>Max number of significant flat colors still considered "palette sized".</summary>
        public int maxPaletteColors;
        /// <summary>Fraction of pixels the top colors must cover to count as flat.</summary>
        public float coverageThreshold;
        /// <summary>Fraction of neighbor pairs in the mid (ramp) band above which it reads as a gradient.</summary>
        public float gradFlatThreshold;
        /// <summary>Hard-edge density above which a many-color texture reads as a pattern.</summary>
        public float noiseThreshold;
        /// <summary>Largest texture side (px) analyzed; larger textures are downscaled first.</summary>
        public int downscaleSize;

        public static MeshPaletteThresholds Default => new MeshPaletteThresholds
        {
            maxPaletteColors = 8,
            coverageThreshold = 0.90f,
            gradFlatThreshold = 0.18f,
            noiseThreshold = 0.25f,
            downscaleSize = 256
        };
    }

    /// <summary>Raw, threshold-independent measurements produced once per scan and reused
    /// when thresholds change. All fractions are 0..1.</summary>
    public sealed class MeshPaletteMetrics
    {
        /// <summary>Shares of the significant quantized color buckets, largest first (capped).</summary>
        public float[] topShares = System.Array.Empty<float>();
        /// <summary>Number of quantized color buckets whose share reaches the significance floor.</summary>
        public int significantColorCount;
        /// <summary>Fraction of neighbor pairs whose difference sits in the smooth-ramp band.</summary>
        public float gradientScore;
        /// <summary>Fraction of neighbor pairs that are hard edges.</summary>
        public float noiseScore;
        /// <summary>Average neighbor color difference (informational).</summary>
        public float meanDelta;
        /// <summary>Number of opaque pixels the metrics were built from (0 = empty region).</summary>
        public int sampleCount;

        /// <summary>Sum of the largest <paramref name="topN"/> bucket shares.</summary>
        public float CoverageOfTop(int topN)
        {
            float sum = 0f;
            int n = Mathf.Min(topN, topShares.Length);
            for (int i = 0; i < n; i++) sum += topShares[i];
            return sum;
        }
    }

    /// <summary>Per-(renderer, material slot) analysis result shown in the table and gizmos.</summary>
    public sealed class MeshPaletteClassification
    {
        public Renderer renderer;
        public Mesh mesh;
        public int materialSlot;
        public Texture2D texture;

        public MeshPaletteMetrics metrics;
        public MeshPaletteVerdict verdict;
        public string reason;

        /// <summary>World-space bounds used to draw the scene box.</summary>
        public Bounds worldBounds;

        // Reliability flags surfaced in the row tooltip.
        public bool meshUnreadable;
        public bool tiledUVs;
        public bool outOfBoundsUVs;
        public bool nonAssetTexture;
        public bool analyzedWholeTexture;

        public string DisplayName => renderer != null ? renderer.gameObject.name : "(missing)";
        public string TextureName => texture != null ? texture.name : "(no texture)";
    }
}
#endif
