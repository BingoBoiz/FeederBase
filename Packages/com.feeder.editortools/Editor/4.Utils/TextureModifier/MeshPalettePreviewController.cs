#if UNITY_EDITOR
using System.Collections.Generic;
using UnityEditor;
using UnityEngine;

namespace Feeder
{
    /// <summary>Owns temporary preview material, texture, mesh, and renderer restoration.</summary>
    public class MeshPalettePreviewController
    {
        private const string UndoPrefix = "Feeder Texture Modifier";

        private Texture2D previewTexture;
        private Material previewMaterial;
        private Mesh previewMesh;
        private Material[] originalSharedMaterials;
        private Mesh originalMesh;
        private Renderer previewRenderer;
        private readonly List<SwatchMarker> previewMarkers = new List<SwatchMarker>();

        public Texture2D PreviewTexture => previewTexture;
        public List<SwatchMarker> PreviewMarkers => previewMarkers;
        public bool IsActive { get; private set; }

        public SwatchMarker MarkerForRow(int rowIndex)
        {
            for (int i = 0; i < previewMarkers.Count; i++)
                if (previewMarkers[i].number - 1 == rowIndex)
                    return previewMarkers[i];
            return null;
        }

        public void BuildPreview(MeshPaletteColorizerSession session, PaletteColorBuildResult buildResult)
        {
            if (session == null || !session.IsLoaded || buildResult == null)
                return;

            if (previewTexture == null ||
                previewTexture.width != session.TextureWidth ||
                previewTexture.height != session.TextureHeight)
            {
                if (previewTexture != null)
                    Object.DestroyImmediate(previewTexture);

                previewTexture = new Texture2D(session.TextureWidth, session.TextureHeight, TextureFormat.RGBA32, false)
                {
                    hideFlags = HideFlags.HideAndDontSave,
                    filterMode = session.WorkingTexture != null ? session.WorkingTexture.filterMode : FilterMode.Point
                };
            }
            previewTexture.SetPixels32(buildResult.Pixels);
            previewTexture.Apply(false);

            previewMarkers.Clear();
            previewMarkers.AddRange(buildResult.Markers);

            if (previewMesh != null)
                Object.DestroyImmediate(previewMesh);

            previewMesh = MeshColorRemapper.BuildRemappedMesh(
                session.SourceMesh, session.MaterialSlotIndex, buildResult.SwatchUV);
            previewMesh.hideFlags = HideFlags.HideAndDontSave;

            EnsurePreviewAssigned(session);
            FTextureModifierUtils.SetMapTexture(previewMaterial, previewTexture);
        }

        public void Revert(Renderer targetRenderer)
        {
            Renderer renderer = targetRenderer != null ? targetRenderer : previewRenderer;

            if (renderer != null && originalSharedMaterials != null)
            {
                Undo.RecordObject(renderer, UndoPrefix + " Revert");
                renderer.sharedMaterials = originalSharedMaterials;
            }
            originalSharedMaterials = null;

            if (renderer != null && originalMesh != null)
            {
                if (renderer is SkinnedMeshRenderer smr)
                {
                    Undo.RecordObject(smr, UndoPrefix + " Revert Mesh");
                    smr.sharedMesh = originalMesh;
                }
                else
                {
                    var mf = renderer.GetComponent<MeshFilter>();
                    if (mf != null)
                    {
                        Undo.RecordObject(mf, UndoPrefix + " Revert Mesh");
                        mf.sharedMesh = originalMesh;
                    }
                }
            }
            originalMesh = null;
            previewRenderer = null;
            previewMarkers.Clear();
            IsActive = false;
        }

        public void Cleanup()
        {
            if (previewMaterial != null)
                Object.DestroyImmediate(previewMaterial);
            if (previewTexture != null)
                Object.DestroyImmediate(previewTexture);
            if (previewMesh != null)
                Object.DestroyImmediate(previewMesh);

            previewMaterial = null;
            previewTexture = null;
            previewMesh = null;
        }

        private void EnsurePreviewAssigned(MeshPaletteColorizerSession session)
        {
            var targetRenderer = session.TargetRenderer;
            if (targetRenderer == null || session.SourceMaterial == null)
                return;

            previewRenderer = targetRenderer;
            if (originalSharedMaterials == null)
                originalSharedMaterials = targetRenderer.sharedMaterials;

            if (previewMaterial == null)
            {
                previewMaterial = new Material(session.SourceMaterial) { hideFlags = HideFlags.HideAndDontSave };
                previewMaterial.name = session.SourceMaterial.name + " (Preview)";
            }

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

            var mf = targetRenderer.GetComponent<MeshFilter>();
            if (previewMesh != null)
            {
                if (targetRenderer is SkinnedMeshRenderer smr)
                {
                    if (originalMesh == null)
                        originalMesh = smr.sharedMesh;
                    Undo.RecordObject(smr, UndoPrefix + " Preview Mesh");
                    smr.sharedMesh = previewMesh;
                }
                else if (mf != null)
                {
                    if (originalMesh == null)
                        originalMesh = mf.sharedMesh;
                    Undo.RecordObject(mf, UndoPrefix + " Preview Mesh");
                    mf.sharedMesh = previewMesh;
                }
            }

            IsActive = true;
        }
    }
}
#endif
