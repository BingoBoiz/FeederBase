#if UNITY_EDITOR
using System.Collections.Generic;
using Feeder.MB.Core;
using UnityEditor;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace Feeder
{
    /// <summary>
    /// Runs a scan over scene / selection renderers, resolves each mesh+material+texture,
    /// restricts analysis to the UV region the mesh actually samples, and stores the
    /// resulting <see cref="MeshPaletteClassification"/> list. Texture pixels are read once
    /// per texture per scan (via a blit) and cached for the duration of that scan only.
    /// </summary>
    public sealed class MeshPaletteAnalysisSession
    {
        private struct PixelData
        {
            public Color[] pixels;
            public int width;
            public int height;
        }

        public List<MeshPaletteClassification> Results { get; } = new List<MeshPaletteClassification>();
        public bool HasResults => Results.Count > 0;

        /// <summary>Scans every MeshRenderer / SkinnedMeshRenderer in the active scene.</summary>
        public void AnalyzeScene(MeshPaletteThresholds thresholds)
        {
            var renderers = new List<Renderer>();
            Scene scene = SceneManager.GetActiveScene();
            if (scene.IsValid())
            {
                foreach (GameObject root in scene.GetRootGameObjects())
                    CollectRenderers(root, renderers);
            }
            Analyze(renderers, thresholds);
        }

        /// <summary>Scans the current Selection (and its children).</summary>
        public void AnalyzeSelection(MeshPaletteThresholds thresholds)
        {
            var renderers = new List<Renderer>();
            foreach (GameObject go in Selection.gameObjects)
                CollectRenderers(go, renderers);
            Analyze(renderers, thresholds);
        }

        private static void CollectRenderers(GameObject root, List<Renderer> into)
        {
            if (root == null) return;
            into.AddRange(root.GetComponentsInChildren<MeshRenderer>(true));
            into.AddRange(root.GetComponentsInChildren<SkinnedMeshRenderer>(true));
        }

        private void Analyze(List<Renderer> renderers, MeshPaletteThresholds thresholds)
        {
            Results.Clear();
            var seen = new HashSet<Renderer>();
            var pixelCache = new Dictionary<Texture2D, PixelData>();

            try
            {
                for (int i = 0; i < renderers.Count; i++)
                {
                    Renderer r = renderers[i];
                    if (r == null || !seen.Add(r)) continue;

                    if (EditorUtility.DisplayCancelableProgressBar(
                            "Mesh Palette Analyzer",
                            $"Đang phân tích {r.gameObject.name} ({i + 1}/{renderers.Count})",
                            (i + 1f) / Mathf.Max(1, renderers.Count)))
                        break;

                    AnalyzeRenderer(r, thresholds, pixelCache);
                }
            }
            finally
            {
                EditorUtility.ClearProgressBar();
            }

            SortResults();
        }

        private void AnalyzeRenderer(Renderer r, MeshPaletteThresholds thresholds,
            Dictionary<Texture2D, PixelData> pixelCache)
        {
            Mesh mesh = FTextureModifierUtils.GetRendererMesh(r);
            if (mesh == null) return;

            Material[] mats = r.sharedMaterials;
            if (mats == null || mats.Length == 0) return;

            for (int slot = 0; slot < mats.Length; slot++)
            {
                Material mat = mats[slot];
                if (mat == null) continue;

                var c = new MeshPaletteClassification
                {
                    renderer = r,
                    mesh = mesh,
                    materialSlot = slot,
                    worldBounds = r.bounds
                };

                var texture = FTextureModifierUtils.GetMaterialMainTexture(mat) as Texture2D;
                c.texture = texture;

                if (texture == null)
                {
                    c.verdict = MeshPaletteVerdict.PaletteReady;
                    c.reason = "Material màu trơn (không texture)";
                    Results.Add(c);
                    continue;
                }

                string path = AssetDatabase.GetAssetPath(texture);
                var importer = string.IsNullOrEmpty(path) ? null : AssetImporter.GetAtPath(path) as TextureImporter;
                if (importer == null)
                {
                    c.nonAssetTexture = true;
                    c.verdict = MeshPaletteVerdict.Skipped;
                    c.reason = "Texture nhúng / không phải asset";
                    Results.Add(c);
                    continue;
                }

                // Reliability flags.
                string prop = mat.HasProperty("_BaseMap") ? "_BaseMap"
                    : mat.HasProperty("_MainTex") ? "_MainTex" : null;
                if (prop != null &&
                    (mat.GetTextureScale(prop) != Vector2.one || mat.GetTextureOffset(prop) != Vector2.zero))
                    c.tiledUVs = true;
                c.meshUnreadable = !mesh.isReadable;

                PixelData data = GetPixels(texture, thresholds.downscaleSize, pixelCache);

                Color[] region = data.pixels;
                int rw = data.width;
                int rh = data.height;
                bool useUvRegion = false;
                if (!c.tiledUVs && mesh.isReadable)
                    useUvRegion = FMeshPaletteClassifier.TryExtractUvRegion(mesh, slot,
                        data.pixels, data.width, data.height, out region, out rw, out rh, out _);

                if (!useUvRegion)
                {
                    // Re-check out-of-bounds explicitly for a readable mesh so we can flag it.
                    if (mesh.isReadable && !c.tiledUVs)
                    {
                        Rect uvB = new Rect();
                        c.outOfBoundsUVs = MB_Utility.hasOutOfBoundsUVs(mesh, ref uvB);
                    }
                    region = data.pixels; rw = data.width; rh = data.height;
                    c.analyzedWholeTexture = true;
                }

                c.metrics = FMeshPaletteClassifier.ComputeMetrics(region, rw, rh);
                ApplyVerdict(c, thresholds);
                Results.Add(c);
            }
        }

        /// <summary>Re-runs only the (cheap) verdict step on all results using new thresholds.
        /// Does not re-read pixels, so <see cref="MeshPaletteThresholds.downscaleSize"/> changes
        /// take effect only after a fresh Analyze.</summary>
        public void Reclassify(MeshPaletteThresholds thresholds)
        {
            foreach (MeshPaletteClassification c in Results)
            {
                if (c.metrics == null) continue; // solid-color / skipped verdicts stay fixed
                ApplyVerdict(c, thresholds);
            }
            SortResults();
        }

        private static void ApplyVerdict(MeshPaletteClassification c, MeshPaletteThresholds thresholds)
        {
            c.verdict = FMeshPaletteClassifier.Classify(c.metrics, thresholds, out string reason);
            c.reason = reason;

            // An unreliable UV region can't confidently claim "ready" -> soften to Uncertain.
            if (c.verdict == MeshPaletteVerdict.PaletteReady &&
                (c.tiledUVs || c.outOfBoundsUVs || c.meshUnreadable || c.analyzedWholeTexture))
            {
                c.verdict = MeshPaletteVerdict.Uncertain;
                c.reason += " · UV không tin cậy";
            }
        }

        private PixelData GetPixels(Texture2D texture, int downscaleSize, Dictionary<Texture2D, PixelData> cache)
        {
            if (cache.TryGetValue(texture, out PixelData cached))
                return cached;

            Color[] full = TextureRecolorService.ReadPixelsViaBlit(texture);
            int w = texture.width, h = texture.height;

            PixelData data;
            if (Mathf.Max(w, h) > downscaleSize)
            {
                Color[] small = TextureRecolorService.Downscale(full, w, h, downscaleSize, out int sw, out int sh);
                data = new PixelData { pixels = small, width = sw, height = sh };
            }
            else
            {
                data = new PixelData { pixels = full, width = w, height = h };
            }

            cache[texture] = data;
            return data;
        }

        private void SortResults()
        {
            // Group by verdict severity so problems surface first.
            Results.Sort((a, b) =>
            {
                int va = VerdictOrder(a.verdict);
                int vb = VerdictOrder(b.verdict);
                if (va != vb) return va.CompareTo(vb);
                return string.Compare(a.DisplayName, b.DisplayName, System.StringComparison.Ordinal);
            });
        }

        private static int VerdictOrder(MeshPaletteVerdict v)
        {
            switch (v)
            {
                case MeshPaletteVerdict.Gradient: return 0;
                case MeshPaletteVerdict.Pattern: return 1;
                case MeshPaletteVerdict.Uncertain: return 2;
                case MeshPaletteVerdict.PaletteReady: return 3;
                default: return 4; // Skipped
            }
        }

        public int CountOf(MeshPaletteVerdict v)
        {
            int n = 0;
            foreach (MeshPaletteClassification c in Results)
                if (c.verdict == v) n++;
            return n;
        }

        public void Clear() => Results.Clear();
    }
}
#endif
