using System;
using System.Collections.Generic;
using System.IO;
using Sirenix.OdinInspector;
using UnityEditor;
using UnityEditor.Formats.Fbx.Exporter;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace Feeder
{
    public sealed class FRepackModelsTool : FTargetMeshesToolBase
    {
        protected override string GetDescription()
        {
            return "Export từng Mesh thành file FBX cùng thư mục, thay thế tham chiếu trong scene rồi chuyển mesh gốc vào Assets/_FeederTrash (revert được).";
        }

        [OnInspectorGUI]
        private void DrawGuide()
        {
            GUILayout.Space(2);
            FStylesUtils.DrawInfoBox(
                "TargetMeshes    kéo các Mesh asset cần đóng gói vào đây\n" +
                "Repack          xuất FBX, cập nhật scene ref, chuyển mesh gốc vào trash\n" +
                "Revert          khôi phục mesh gốc từ Assets/_FeederTrash (FBX giữ nguyên)"
            );
            GUILayout.Space(4);
        }

        [NonSerialized] private FAssetRevertService.RevertOperation _lastOp;

        [Button(ButtonSizes.Large), GUIColor(0.3f, 0.8f, 1f)]
        public void Repack()
        {
            ValidateInput();

            var op = FAssetRevertService.Begin("Repack Models",
                "Scene mesh-reference rewires nằm trong Undo (Ctrl+Z); FBX đã export KHÔNG bị xoá khi revert.");
            int exported = 0;
            for (int i = 0; i < TargetMeshes.Count; i++)
            {
                var mesh = TargetMeshes[i];
                if (mesh == null)
                {
                    Debug.LogWarning($"[FRepackModelsTool] Skipping null at TargetMeshes[{i}].");
                    continue;
                }

                string meshAssetPath = AssetDatabase.GetAssetPath(mesh);
                if (string.IsNullOrEmpty(meshAssetPath))
                {
                    Debug.LogWarning($"[FRepackModelsTool] Mesh is not an asset: {mesh.name}. Skipping.");
                    continue;
                }

                string fbxAssetPath = GetFbxAssetPathForMesh(mesh, meshAssetPath);
                string fullPath = Path.Combine(Path.GetDirectoryName(Application.dataPath), fbxAssetPath).Replace('/', Path.DirectorySeparatorChar);
                fullPath = Path.GetFullPath(fullPath);

                GameObject tempGo = CreateTemporaryGameObjectWithMeshOnly(mesh);
                try
                {
                    string result = ModelExporter.ExportObject(fullPath, tempGo);
                    if (string.IsNullOrEmpty(result))
                    {
                        Debug.LogWarning($"[FRepackModelsTool] Export failed for mesh: {mesh.name}");
                        continue;
                    }
                    exported++;
                    Debug.Log($"[FRepackModelsTool] Exported: {result}");
                }
                finally
                {
                    UnityEngine.Object.DestroyImmediate(tempGo);
                }

                AssetDatabase.Refresh();
                ApplyModelImporterSettingsIfUnderAssets(fullPath);

                Mesh fbxMesh = GetFirstMeshFromModel(fbxAssetPath);
                if (fbxMesh == null)
                {
                    Debug.LogWarning($"[FRepackModelsTool] No mesh in imported FBX: {fbxAssetPath}. Skipping ref replace and delete.");
                    continue;
                }

                ReplaceSceneReferencesFromMeshToMesh(mesh, fbxMesh);
                MoveMeshAssetToTrashIfStandalone(op, meshAssetPath);
            }
            _lastOp = op.IsEmpty ? null : op;

            Debug.Log($"<color=green>Repack done. Exported {exported} FBX file(s).</color>");
        }

        private bool HasRevertableOp => _lastOp != null && !_lastOp.IsEmpty;

        [Button("Revert Last Repack (restore source meshes)", ButtonSizes.Medium), EnableIf(nameof(HasRevertableOp))]
        private void RevertLastRepack()
        {
            if (FAssetRevertService.Revert(_lastOp, out string report))
                _lastOp = null;
            Debug.Log(report);
        }

        private static string GetFbxAssetPathForMesh(Mesh mesh, string meshAssetPath)
        {
            string dir = Path.GetDirectoryName(meshAssetPath).Replace('\\', '/');
            string baseName;
            if (meshAssetPath.EndsWith(".fbx", StringComparison.OrdinalIgnoreCase))
                baseName = SanitizeFileName(mesh.name) + "_Repack";
            else
                baseName = Path.GetFileNameWithoutExtension(meshAssetPath);
            if (string.IsNullOrEmpty(baseName))
                baseName = mesh.name;
            return dir + "/" + baseName + ".fbx";
        }

        private void ValidateInput()
        {
            if (TargetMeshes == null || TargetMeshes.Count == 0)
                throw new InvalidOperationException("TargetMeshes is empty. Add at least one Mesh.");
        }

        // mesh only: no MeshRenderer so FBX contains geometry only, no materials
        private static GameObject CreateTemporaryGameObjectWithMeshOnly(Mesh mesh)
        {
            var go = new GameObject(mesh?.name ?? "MeshExport");
            var mf = go.AddComponent<MeshFilter>();
            mf.sharedMesh = mesh;
            return go;
        }

        private static void ApplyModelImporterSettingsIfUnderAssets(string fullPath)
        {
            if (string.IsNullOrEmpty(fullPath))
                return;
            string dataPath = Path.GetFullPath(Application.dataPath);
            string normalizedFull = Path.GetFullPath(fullPath);
            if (!normalizedFull.StartsWith(dataPath, StringComparison.OrdinalIgnoreCase))
                return;
            string relative = normalizedFull.Length > dataPath.Length ? normalizedFull.Substring(dataPath.Length).TrimStart(Path.DirectorySeparatorChar, '/') : string.Empty;
            string assetPath = "Assets/" + relative.Replace('\\', '/');
            AssetDatabase.ImportAsset(assetPath);
            var importer = AssetImporter.GetAtPath(assetPath) as ModelImporter;
            if (importer == null)
                return;
            importer.useFileUnits = false;
            importer.bakeAxisConversion = false;
            importer.importBlendShapes = false;
            importer.importVisibility = false;
            importer.importCameras = false;
            importer.importLights = false;
            importer.preserveHierarchy = false;
            importer.sortHierarchyByName = true;
            importer.importAnimation = false;
            importer.materialImportMode = ModelImporterMaterialImportMode.None;
            importer.SaveAndReimport();
        }

        private static Mesh GetFirstMeshFromModel(string modelAssetPath)
        {
            UnityEngine.Object[] all = AssetDatabase.LoadAllAssetsAtPath(modelAssetPath);
            if (all == null)
                return null;
            foreach (var o in all)
            {
                if (o is Mesh m)
                    return m;
            }
            return null;
        }

        private static void ReplaceSceneReferencesFromMeshToMesh(Mesh fromMesh, Mesh toMesh)
        {
            for (int s = 0; s < SceneManager.sceneCount; s++)
            {
                var scene = SceneManager.GetSceneAt(s);
                if (!scene.isLoaded)
                    continue;
                var roots = scene.GetRootGameObjects();
                foreach (var root in roots)
                {
                    foreach (var mf in root.GetComponentsInChildren<MeshFilter>(true))
                    {
                        if (mf.sharedMesh != fromMesh)
                            continue;
                        var mr = mf.GetComponent<MeshRenderer>();
                        if (mr != null)
                        {
                            Undo.RecordObject(mr, "Repack FBX: replace mesh ref");
                            SetRendererMaterialsToMatchSubMeshCount(mr, toMesh.subMeshCount);
                        }
                        Undo.RecordObject(mf, "Repack FBX: replace mesh ref");
                        mf.sharedMesh = toMesh;
                        EditorSceneManager.MarkSceneDirty(scene);
                    }
                    foreach (var smr in root.GetComponentsInChildren<SkinnedMeshRenderer>(true))
                    {
                        if (smr.sharedMesh != fromMesh)
                            continue;
                        Undo.RecordObject(smr, "Repack FBX: replace mesh ref");
                        SetRendererMaterialsToMatchSubMeshCount(smr, toMesh.subMeshCount);
                        smr.sharedMesh = toMesh;
                        EditorSceneManager.MarkSceneDirty(scene);
                    }
                }
            }
        }

        // avoid "more materials than submeshes" warning after replacing mesh
        private static void SetRendererMaterialsToMatchSubMeshCount(Renderer renderer, int subMeshCount)
        {
            if (renderer == null || subMeshCount <= 0)
                return;
            Material[] current = renderer.sharedMaterials;
            if (current?.Length == subMeshCount)
                return;
            var newMats = new Material[subMeshCount];
            Material fallback = current?.Length > 0 ? current[0] : null;
            for (int i = 0; i < subMeshCount; i++)
                newMats[i] = (current != null && i < current.Length) ? current[i] : fallback;
            renderer.sharedMaterials = newMats;
        }

        private static void MoveMeshAssetToTrashIfStandalone(FAssetRevertService.RevertOperation op, string meshAssetPath)
        {
            if (!meshAssetPath.EndsWith(".asset", StringComparison.OrdinalIgnoreCase))
                return;
            FAssetRevertService.MoveToTrash(op, meshAssetPath);
        }

        private static string SanitizeFileName(string name)
        {
            if (string.IsNullOrWhiteSpace(name))
                return string.Empty;
            foreach (var ch in Path.GetInvalidFileNameChars())
                name = name.Replace(ch, '_');
            return name.Trim();
        }
    }
}
