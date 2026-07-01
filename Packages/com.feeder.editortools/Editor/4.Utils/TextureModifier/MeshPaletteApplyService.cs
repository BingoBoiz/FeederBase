#if UNITY_EDITOR
using System;
using System.Collections.Generic;
using System.IO;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;

namespace Feeder
{
    /// <summary>Result information produced by applying mesh palette color changes.</summary>
    public class MeshPaletteApplyResult
    {
        public Mesh AssignedMesh { get; set; }
        public string SourceTexturePath { get; set; }
        public string WrittenMeshPath { get; set; }
        public bool CreatedMeshAsset { get; set; }
        public string SourceMaterialName { get; set; }
        public string TargetRendererName { get; set; }
        public int UnplacedCount { get; set; }
        public UnityEngine.Object PingTarget { get; set; }

        public string MeshAction => CreatedMeshAsset ? "created mesh asset" : "overwrote mesh asset";
    }

    /// <summary>Applies palette texture pixels and remapped mesh data to project assets.</summary>
    public static class MeshPaletteApplyService
    {
        private const string UndoPrefix = "Feeder Texture Modifier";

        public static MeshPaletteApplyResult Apply(
            MeshPaletteColorizerSession session,
            PaletteColorBuildResult buildResult,
            string generatedFolder)
        {
            if (session == null || !session.IsLoaded)
                throw new InvalidOperationException("No mesh palette colorizer session is loaded.");
            if (buildResult == null)
                throw new ArgumentNullException(nameof(buildResult));

            var resultTexture = new Texture2D(session.Palette.Width, session.Palette.Height, TextureFormat.RGBA32, false);
            resultTexture.SetPixels32(buildResult.Pixels);
            resultTexture.Apply(false);
            byte[] resultPng = resultTexture.EncodeToPNG();
            UnityEngine.Object.DestroyImmediate(resultTexture);

            File.WriteAllBytes(session.SourceTexturePath, resultPng);
            AssetDatabase.ImportAsset(session.SourceTexturePath, ImportAssetOptions.ForceSynchronousImport);

            Mesh remappedMesh = MeshColorRemapper.BuildRemappedMesh(
                session.SourceMesh, session.MaterialSlotIndex, buildResult.SwatchUV);
            if (remappedMesh.vertexCount == 0)
                throw new Exception("The generated mesh is empty. The source mesh may not be readable.");

            Mesh assignedMesh = MeshColorRemapper.SaveOrOverwriteMesh(
                session.SourceMesh, remappedMesh, session.TargetRenderer, generatedFolder,
                out string writtenMeshPath, out bool createdMeshAsset);

            AssignMeshAndSourceMaterial(session, assignedMesh);

            AssetDatabase.SaveAssets();
            AssetDatabase.Refresh();

            var pingTarget = createdMeshAsset
                ? (UnityEngine.Object)assignedMesh
                : AssetDatabase.LoadAssetAtPath<Texture2D>(session.SourceTexturePath);

            return new MeshPaletteApplyResult
            {
                AssignedMesh = assignedMesh,
                SourceTexturePath = session.SourceTexturePath,
                WrittenMeshPath = writtenMeshPath,
                CreatedMeshAsset = createdMeshAsset,
                SourceMaterialName = session.SourceMaterial != null ? session.SourceMaterial.name : "<null>",
                TargetRendererName = session.TargetRenderer != null ? session.TargetRenderer.name : "<null>",
                UnplacedCount = buildResult.UnplacedCount,
                PingTarget = pingTarget
            };
        }

        private static void AssignMeshAndSourceMaterial(MeshPaletteColorizerSession session, Mesh meshToAssign)
        {
            if (meshToAssign == null)
                return;

            var targetRenderer = session.TargetRenderer;
            if (targetRenderer is SkinnedMeshRenderer smr)
            {
                Undo.RecordObject(smr, UndoPrefix + " Apply Mesh");
                smr.sharedMesh = meshToAssign;
                EditorUtility.SetDirty(smr);
            }
            else
            {
                var mf = targetRenderer.GetComponent<MeshFilter>();
                if (mf != null)
                {
                    Undo.RecordObject(mf, UndoPrefix + " Apply Mesh");
                    mf.sharedMesh = meshToAssign;
                    EditorUtility.SetDirty(mf);
                }
            }

            var list = new List<Material>(targetRenderer.sharedMaterials);
            if (list.Count == 0 && session.BaseMaterials != null)
                list = new List<Material>(session.BaseMaterials);

            int slot = Mathf.Max(0, session.MaterialSlotIndex);
            while (list.Count <= slot)
                list.Add(session.SourceMaterial);

            Undo.RecordObject(targetRenderer, UndoPrefix + " Apply Material");
            list[slot] = session.SourceMaterial;
            targetRenderer.sharedMaterials = list.ToArray();
            EditorUtility.SetDirty(targetRenderer);

            if (!Application.isPlaying)
                EditorSceneManager.MarkSceneDirty(targetRenderer.gameObject.scene);
        }
    }
}
#endif
