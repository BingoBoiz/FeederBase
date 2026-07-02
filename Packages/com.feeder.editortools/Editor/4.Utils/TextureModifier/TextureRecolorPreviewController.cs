#if UNITY_EDITOR
using System.Collections.Generic;
using UnityEditor;
using UnityEngine;

namespace Feeder
{
    public enum RecolorPreviewMode { Original, Result, Mask }

    /// <summary>
    /// Owns the temporary preview texture/material for the direct texture recolor tab
    /// and restores the renderer's original materials on revert.
    /// </summary>
    public class TextureRecolorPreviewController
    {
        private const string UndoPrefix = "Feeder Texture Recolor";

        private Texture2D previewTexture;
        private Material previewMaterial;
        private Material[] originalSharedMaterials;
        private Renderer previewRenderer;

        public Texture2D PreviewTexture => previewTexture;
        public bool IsScenePreviewActive { get; private set; }

        /// <summary>Rebuilds the window preview texture from the session's downscaled pixels.</summary>
        public void UpdatePreview(TextureRecolorSession session, RecolorMaskSettings mask, RecolorPreviewMode mode)
        {
            if (session == null || !session.IsLoaded)
                return;

            Color[] pixels;
            switch (mode)
            {
                case RecolorPreviewMode.Original:
                    pixels = session.PreviewPixels;
                    break;
                case RecolorPreviewMode.Mask:
                    pixels = TextureRecolorService.RecolorMulti(session.PreviewPixels, session.Clusters, mask, true);
                    break;
                default:
                    pixels = TextureRecolorService.RecolorMulti(session.PreviewPixels, session.Clusters, mask, false);
                    break;
            }

            if (previewTexture == null ||
                previewTexture.width != session.PreviewWidth ||
                previewTexture.height != session.PreviewHeight)
            {
                if (previewTexture != null)
                    Object.DestroyImmediate(previewTexture);

                previewTexture = new Texture2D(session.PreviewWidth, session.PreviewHeight, TextureFormat.RGBA32, false)
                {
                    hideFlags = HideFlags.HideAndDontSave
                };
            }
            previewTexture.SetPixels(pixels);
            previewTexture.Apply(false);
        }

        /// <summary>Swaps a cloned material with the preview texture into the renderer's slot.</summary>
        public void ApplyScenePreview(TextureRecolorSession session)
        {
            if (session == null || !session.IsLoaded || previewTexture == null)
                return;

            var targetRenderer = session.TargetRenderer;
            previewRenderer = targetRenderer;
            if (originalSharedMaterials == null)
                originalSharedMaterials = targetRenderer.sharedMaterials;

            if (previewMaterial == null)
            {
                previewMaterial = new Material(session.SourceMaterial) { hideFlags = HideFlags.HideAndDontSave };
                previewMaterial.name = session.SourceMaterial.name + " (Preview)";
            }
            FTextureModifierUtils.SetMapTexture(previewMaterial, previewTexture);

            var list = new List<Material>(targetRenderer.sharedMaterials);
            if (list.Count == 0 && session.BaseMaterials != null)
                list = new List<Material>(session.BaseMaterials);

            int slot = Mathf.Max(0, session.MaterialSlotIndex);
            while (list.Count <= slot)
                list.Add(session.SourceMaterial);

            if (list[slot] != previewMaterial)
            {
                Undo.RecordObject(targetRenderer, UndoPrefix + " Preview");
                list[slot] = previewMaterial;
                targetRenderer.sharedMaterials = list.ToArray();
            }

            IsScenePreviewActive = true;
        }

        /// <summary>Restores the renderer's original materials.</summary>
        public void RevertScenePreview(Renderer targetRenderer)
        {
            Renderer renderer = targetRenderer != null ? targetRenderer : previewRenderer;

            if (renderer != null && originalSharedMaterials != null)
            {
                Undo.RecordObject(renderer, UndoPrefix + " Revert");
                renderer.sharedMaterials = originalSharedMaterials;
            }
            originalSharedMaterials = null;
            previewRenderer = null;
            IsScenePreviewActive = false;
        }

        public void Cleanup()
        {
            if (previewMaterial != null)
                Object.DestroyImmediate(previewMaterial);
            if (previewTexture != null)
                Object.DestroyImmediate(previewTexture);

            previewMaterial = null;
            previewTexture = null;
        }
    }
}
#endif
