#if UNITY_EDITOR
using System.Collections.Generic;
using UnityEditor;
using UnityEngine;

namespace Feeder
{
    /// <summary>Loaded target data for direct texture recoloring.</summary>
    public class TextureRecolorSession
    {
        private const int PreviewMaxSize = 512;

        public Renderer TargetRenderer { get; private set; }
        public int MaterialSlotIndex { get; private set; }
        public Material SourceMaterial { get; private set; }
        public Material[] BaseMaterials { get; private set; }
        public Texture2D SourceTexture { get; private set; }
        public string SourceTexturePath { get; private set; }

        public Color[] FullPixels { get; private set; }
        public int FullWidth { get; private set; }
        public int FullHeight { get; private set; }

        public Color[] PreviewPixels { get; private set; }
        public int PreviewWidth { get; private set; }
        public int PreviewHeight { get; private set; }

        public List<RecolorCluster> Clusters { get; } = new List<RecolorCluster>();
        public bool SourceIsLinear { get; private set; }

        public bool IsLoaded =>
            TargetRenderer != null && SourceMaterial != null && SourceTexture != null && FullPixels != null;

        public bool AnyChanged
        {
            get
            {
                for (int i = 0; i < Clusters.Count; i++)
                    if (Clusters[i].Changed)
                        return true;
                return false;
            }
        }

        public void Reset()
        {
            TargetRenderer = null;
            MaterialSlotIndex = 0;
            SourceMaterial = null;
            BaseMaterials = null;
            SourceTexture = null;
            SourceTexturePath = null;
            FullPixels = null;
            FullWidth = FullHeight = 0;
            PreviewPixels = null;
            PreviewWidth = PreviewHeight = 0;
            Clusters.Clear();
            SourceIsLinear = false;
        }

        public void ResetClusterEdits()
        {
            for (int i = 0; i < Clusters.Count; i++)
                Clusters[i].newColor = (Color)Clusters[i].color;
        }

        public bool Load(Renderer renderer, int materialSlot, int maxClusters, out string error)
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
                error = "The material has no texture (_BaseMap/_MainTex).";
                return false;
            }

            var texture2D = tex as Texture2D;
            if (texture2D == null)
            {
                error = $"The material texture \"{tex.name}\" is not a Texture2D.";
                return false;
            }

            string sourceTexturePath = AssetDatabase.GetAssetPath(texture2D);
            if (string.IsNullOrEmpty(sourceTexturePath))
            {
                error = "The texture is not a project asset, so it cannot be overwritten.";
                return false;
            }

            var importer = AssetImporter.GetAtPath(sourceTexturePath) as TextureImporter;
            if (importer == null)
            {
                error = "The texture is embedded in another asset (e.g. an FBX or a built-in texture) and cannot be overwritten. Extract it to its own image file first.";
                return false;
            }

            TargetRenderer = renderer;
            MaterialSlotIndex = materialSlot;
            SourceMaterial = sourceMaterial;
            BaseMaterials = (Material[])mats.Clone();
            SourceTexture = texture2D;
            SourceTexturePath = sourceTexturePath;
            SourceIsLinear = !importer.sRGBTexture;

            FullWidth = texture2D.width;
            FullHeight = texture2D.height;
            FullPixels = TextureRecolorService.ReadPixelsViaBlit(texture2D);

            PreviewPixels = TextureRecolorService.Downscale(
                FullPixels, FullWidth, FullHeight, PreviewMaxSize, out int pw, out int ph);
            PreviewWidth = pw;
            PreviewHeight = ph;

            Clusters.Clear();
            Clusters.AddRange(TextureRecolorService.DetectDominantClusters(FullPixels, maxClusters));

            return true;
        }
    }
}
#endif
