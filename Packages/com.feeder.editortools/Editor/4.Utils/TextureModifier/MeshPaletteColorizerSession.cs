#if UNITY_EDITOR
using System;
using System.Collections.Generic;
using UnityEditor;
using UnityEngine;

namespace Feeder
{
    /// <summary>Loaded target data for mesh palette colorizing.</summary>
    public class MeshPaletteColorizerSession
    {
        private static readonly List<UsedColor> EmptyUsedColors = new List<UsedColor>();

        public Renderer TargetRenderer { get; private set; }
        public int MaterialSlotIndex { get; private set; }
        public int ColorTolerance { get; private set; }

        public PaletteTexture Palette { get; private set; }
        public Texture2D WorkingTexture { get; private set; }
        public string SourceTexturePath { get; private set; }
        public Material SourceMaterial { get; private set; }
        public Material[] BaseMaterials { get; private set; }
        public Mesh SourceMesh { get; private set; }
        public MeshColorAnalysis Analysis { get; private set; }

        public bool IsLoaded => TargetRenderer != null && Palette != null && SourceMaterial != null && SourceMesh != null;
        public List<UsedColor> UsedColors => Analysis.usedColors ?? EmptyUsedColors;
        public int TextureWidth => Palette != null ? Palette.Width : 0;
        public int TextureHeight => Palette != null ? Palette.Height : 0;

        public void Reset()
        {
            if (WorkingTexture != null)
                UnityEngine.Object.DestroyImmediate(WorkingTexture);

            TargetRenderer = null;
            MaterialSlotIndex = 0;
            ColorTolerance = 0;
            Palette = null;
            WorkingTexture = null;
            SourceTexturePath = null;
            SourceMaterial = null;
            BaseMaterials = null;
            SourceMesh = null;
            Analysis = default;
        }

        public int AutoDetectSlot(Renderer renderer, int fallbackSlot = 0)
        {
            if (renderer == null) return fallbackSlot;

            var mats = renderer.sharedMaterials;
            if (mats == null) return fallbackSlot;

            for (int i = 0; i < mats.Length; i++)
            {
                var m = mats[i];
                if (m == null) continue;

                if (m.name.IndexOf("StorePalle", StringComparison.OrdinalIgnoreCase) >= 0)
                    return i;

                var t = FTextureModifierUtils.GetMaterialMainTexture(m);
                if (t != null && t.name.IndexOf("StorePalle", StringComparison.OrdinalIgnoreCase) >= 0)
                    return i;
            }

            return fallbackSlot;
        }

        public bool Load(Renderer renderer, int materialSlot, int colorTolerance, out string error)
        {
            Reset();
            error = null;

            if (renderer == null)
            {
                error = "The renderer is missing.";
                return false;
            }

            var mats = renderer.sharedMaterials;
            if (mats == null || mats.Length == 0)
            {
                error = "The renderer has no materials.";
                return false;
            }

            materialSlot = Mathf.Clamp(materialSlot, 0, mats.Length - 1);
            var sourceMaterial = mats[materialSlot];
            if (sourceMaterial == null)
            {
                error = "The selected material slot is empty.";
                return false;
            }

            Texture tex = FTextureModifierUtils.GetMaterialMainTexture(sourceMaterial);
            if (tex == null)
            {
                error = "The material has no palette texture (_BaseMap/_MainTex).";
                return false;
            }

            string sourceTexturePath = AssetDatabase.GetAssetPath(tex);
            var loadedPalette = PaletteTexture.LoadFromPath(sourceTexturePath, out string paletteError);
            if (loadedPalette == null)
            {
                error = paletteError;
                return false;
            }

            Mesh sourceMesh = FTextureModifierUtils.GetRendererMesh(renderer);
            if (sourceMesh == null)
            {
                error = "The renderer has no Mesh (MeshFilter/SkinnedMeshRenderer).";
                return false;
            }

            TargetRenderer = renderer;
            MaterialSlotIndex = materialSlot;
            ColorTolerance = colorTolerance;
            Palette = loadedPalette;
            SourceTexturePath = sourceTexturePath;
            SourceMaterial = sourceMaterial;
            BaseMaterials = (Material[])mats.Clone();
            SourceMesh = sourceMesh;

            WorkingTexture = new Texture2D(Palette.Width, Palette.Height, TextureFormat.RGBA32, false)
            {
                hideFlags = HideFlags.HideAndDontSave,
                filterMode = FilterMode.Point
            };
            WorkingTexture.SetPixels32(Palette.Pixels);
            WorkingTexture.Apply(false);

            Analysis = MeshColorRemapper.AnalyzeUsedColors(
                SourceMesh, MaterialSlotIndex, Palette.Width, Palette.Height, Palette.Pixels, ColorTolerance);

            return true;
        }
    }
}
#endif
