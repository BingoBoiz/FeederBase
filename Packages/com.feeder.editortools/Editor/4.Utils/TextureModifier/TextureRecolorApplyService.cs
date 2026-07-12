#if UNITY_EDITOR
using System;
using System.Collections.Generic;
using System.IO;
using UnityEditor;
using UnityEngine;

namespace Feeder
{
    /// <summary>Result info produced by <see cref="TextureRecolorApplyService.Apply"/>.</summary>
    public sealed class TextureRecolorApplyResult
    {
        public string WrittenPath;
        public bool ConvertedToPng;
        public UnityEngine.Object PingTarget;
    }

    /// <summary>
    /// Applies a texture recolor at full resolution. PNG sources are overwritten in place;
    /// other formats are exported as a sibling PNG that replaces the texture on the material.
    /// </summary>
    public static class TextureRecolorApplyService
    {
        public static TextureRecolorApplyResult Apply(TextureRecolorSession session, RecolorMaskSettings mask)
        {
            return Apply(session, session != null ? session.Clusters : null, mask);
        }

        /// <summary>
        /// Applies an explicit cluster color mapping to the session's texture. Used by batch apply,
        /// where the active session's clusters recolor other targets' textures.
        /// </summary>
        public static TextureRecolorApplyResult Apply(TextureRecolorSession session,
            IReadOnlyList<RecolorCluster> clusters, RecolorMaskSettings mask)
        {
            if (session == null || !session.IsLoaded)
                throw new InvalidOperationException("No texture is loaded.");

            string sourcePath = session.SourceTexturePath;
            bool isPng = string.Equals(Path.GetExtension(sourcePath), ".png", StringComparison.OrdinalIgnoreCase);
            string outputPath = isPng
                ? sourcePath
                : $"{Path.GetDirectoryName(sourcePath)}/{Path.GetFileNameWithoutExtension(sourcePath)}_recolored.png".Replace('\\', '/');

            try
            {
                string fileName = Path.GetFileName(outputPath);
                EditorUtility.DisplayProgressBar("Texture Recolor", $"Recoloring {fileName}...", 0f);

                var pixels = TextureRecolorService.RecolorMulti(
                    session.FullPixels, clusters, mask, false,
                    p => EditorUtility.DisplayProgressBar("Texture Recolor", $"Recoloring {fileName}...", p * 0.8f));

                EditorUtility.DisplayProgressBar("Texture Recolor", $"Writing {fileName}...", 0.85f);
                var output = new Texture2D(session.FullWidth, session.FullHeight, TextureFormat.RGBA32, false);
                output.SetPixels(pixels);
                output.Apply();
                File.WriteAllBytes(outputPath, output.EncodeToPNG());
                UnityEngine.Object.DestroyImmediate(output);

                EditorUtility.DisplayProgressBar("Texture Recolor", "Importing...", 0.95f);
                AssetDatabase.ImportAsset(outputPath);

                if (!isPng)
                {
                    CopyImporterSettings(sourcePath, outputPath);
                    AssignTextureToMaterial(session, outputPath);
                }

                AssetDatabase.SaveAssets();
            }
            finally
            {
                EditorUtility.ClearProgressBar();
            }

            return new TextureRecolorApplyResult
            {
                WrittenPath = outputPath,
                ConvertedToPng = !isPng,
                PingTarget = AssetDatabase.LoadAssetAtPath<Texture2D>(outputPath)
            };
        }

        private static void CopyImporterSettings(string sourcePath, string destinationPath)
        {
            var srcImporter = AssetImporter.GetAtPath(sourcePath) as TextureImporter;
            var dstImporter = AssetImporter.GetAtPath(destinationPath) as TextureImporter;
            if (srcImporter == null || dstImporter == null) return;

            var settings = new TextureImporterSettings();
            srcImporter.ReadTextureSettings(settings);
            dstImporter.SetTextureSettings(settings);
            dstImporter.maxTextureSize = srcImporter.maxTextureSize;
            dstImporter.textureCompression = srcImporter.textureCompression;

            foreach (var platform in new[] { "Android", "iPhone", "Standalone" })
            {
                var platformSettings = srcImporter.GetPlatformTextureSettings(platform);
                dstImporter.SetPlatformTextureSettings(platformSettings);
            }

            dstImporter.SaveAndReimport();
        }

        private static void AssignTextureToMaterial(TextureRecolorSession session, string texturePath)
        {
            var texture = AssetDatabase.LoadAssetAtPath<Texture2D>(texturePath);
            if (texture == null || session.SourceMaterial == null) return;

            Undo.RecordObject(session.SourceMaterial, "Assign Recolored Texture");
            FTextureModifierUtils.SetMapTexture(session.SourceMaterial, texture);
            EditorUtility.SetDirty(session.SourceMaterial);
        }
    }
}
#endif
