#if UNITY_EDITOR
using System;
using System.Collections.Generic;
using System.IO;
using UnityEditor;
using UnityEngine;

namespace Feeder
{
    /// <summary>Summary of one batch apply run over the shared target list.</summary>
    public sealed class FRecolorBatchResult
    {
        public int AppliedCount;
        public int SkippedCount;
        public readonly List<string> Lines = new List<string>();

        public override string ToString() => string.Join("\n", Lines);
    }

    /// <summary>
    /// Applies the color edits of the active session to every target in the shared list.
    /// Each target gets its own freshly loaded session, so targets sharing a texture or
    /// palette stay consistent (and shared textures are only rewritten once per batch).
    /// </summary>
    public static class FRecolorBatchService
    {
        /// <summary>
        /// Recolors the main texture of every target with the same cluster color mapping.
        /// Targets whose texture file was already processed in this batch are skipped.
        /// <paramref name="preferredRenderer"/> keeps the user-chosen material slot for the active target.
        /// </summary>
        public static FRecolorBatchResult TextureRecolorApplyAll(
            IReadOnlyList<Renderer> targets,
            IReadOnlyList<RecolorCluster> clusters,
            RecolorMaskSettings mask,
            int maxClusters,
            Renderer preferredRenderer,
            int preferredSlot)
        {
            var result = new FRecolorBatchResult();
            var processedTexturePaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var session = new TextureRecolorSession();

            try
            {
                for (int i = 0; i < targets.Count; i++)
                {
                    var renderer = targets[i];
                    string label = renderer != null ? renderer.name : "<null>";
                    EditorUtility.DisplayProgressBar("Feeder Texture Recolor (Batch)",
                        $"({i + 1}/{targets.Count}) {label}", (float)i / targets.Count);

                    try
                    {
                        int slot = renderer == preferredRenderer ? preferredSlot : FindMainTextureSlot(renderer);
                        if (!session.Load(renderer, slot, maxClusters, out string error))
                        {
                            result.SkippedCount++;
                            result.Lines.Add($" - SKIP {label}: {error}");
                            continue;
                        }

                        if (!processedTexturePaths.Add(session.SourceTexturePath))
                        {
                            result.SkippedCount++;
                            result.Lines.Add($" - SKIP {label}: texture already recolored in this batch ({Path.GetFileName(session.SourceTexturePath)}).");
                            continue;
                        }

                        var applyResult = TextureRecolorApplyService.Apply(session, clusters, mask);
                        result.AppliedCount++;
                        result.Lines.Add($" - OK {label}: {applyResult.WrittenPath}{(applyResult.ConvertedToPng ? " (exported PNG)" : "")}");
                    }
                    catch (Exception ex)
                    {
                        result.SkippedCount++;
                        result.Lines.Add($" - FAIL {label}: {ex.Message}");
                    }
                }
            }
            finally
            {
                session.Reset();
                EditorUtility.ClearProgressBar();
            }

            return result;
        }

        /// <summary>
        /// Applies an original→new palette color mapping to every target. Each target's used colors
        /// are matched against the edits with <paramref name="colorTolerance"/>; targets without a
        /// matching color are skipped.
        /// </summary>
        public static FRecolorBatchResult MeshPaletteApplyAll(
            IReadOnlyList<Renderer> targets,
            IReadOnlyList<UsedColor> edits,
            int colorTolerance,
            string generatedFolder,
            Renderer preferredRenderer,
            int preferredSlot)
        {
            var result = new FRecolorBatchResult();
            var session = new MeshPaletteColorizerSession();

            var changedEdits = new List<UsedColor>();
            if (edits != null)
                for (int i = 0; i < edits.Count; i++)
                    if (edits[i] != null && edits[i].Changed)
                        changedEdits.Add(edits[i]);

            if (changedEdits.Count == 0)
                return result;

            try
            {
                for (int i = 0; i < targets.Count; i++)
                {
                    var renderer = targets[i];
                    string label = renderer != null ? renderer.name : "<null>";
                    EditorUtility.DisplayProgressBar("Feeder Mesh Palette (Batch)",
                        $"({i + 1}/{targets.Count}) {label}", (float)i / targets.Count);

                    try
                    {
                        int slot = renderer == preferredRenderer
                            ? preferredSlot
                            : session.AutoDetectSlot(renderer, FindMainTextureSlot(renderer));

                        if (!session.Load(renderer, slot, colorTolerance, out string error))
                        {
                            result.SkippedCount++;
                            result.Lines.Add($" - SKIP {label}: {error}");
                            continue;
                        }

                        int matched = 0;
                        var usedColors = session.UsedColors;
                        for (int u = 0; u < usedColors.Count; u++)
                        {
                            for (int e = 0; e < changedEdits.Count; e++)
                            {
                                if (FTextureModifierUtils.ColorsClose(usedColors[u].color, changedEdits[e].color, colorTolerance))
                                {
                                    usedColors[u].newColor = changedEdits[e].newColor;
                                    matched++;
                                    break;
                                }
                            }
                        }

                        if (matched == 0)
                        {
                            result.SkippedCount++;
                            result.Lines.Add($" - SKIP {label}: no palette color matches the edited colors.");
                            continue;
                        }

                        var buildResult = PaletteColorChangeBuilder.Build(session.Palette, usedColors, session.ColorTolerance);
                        var applyResult = MeshPaletteApplyService.Apply(session, buildResult, generatedFolder);

                        result.AppliedCount++;
                        string unplaced = applyResult.UnplacedCount > 0 ? $", {applyResult.UnplacedCount} unplaced" : "";
                        result.Lines.Add($" - OK {label}: {matched} color(s) remapped, {applyResult.MeshAction}{unplaced}");
                    }
                    catch (Exception ex)
                    {
                        result.SkippedCount++;
                        result.Lines.Add($" - FAIL {label}: {ex.Message}");
                    }
                }
            }
            finally
            {
                session.Reset();
                EditorUtility.ClearProgressBar();
            }

            return result;
        }

        /// <summary>First material slot whose main texture is a Texture2D, or 0.</summary>
        private static int FindMainTextureSlot(Renderer renderer)
        {
            if (renderer == null) return 0;

            var mats = renderer.sharedMaterials;
            if (mats == null) return 0;

            for (int i = 0; i < mats.Length; i++)
                if (mats[i] != null && FTextureModifierUtils.GetMaterialMainTexture(mats[i]) is Texture2D)
                    return i;
            return 0;
        }
    }
}
#endif
