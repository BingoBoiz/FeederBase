using System;
using System.Collections.Generic;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace Feeder
{
    public static class FPrefabBatchOperations
    {
        public const string KeyIsActive = "m_IsActive";
        public const string KeyLayer = "m_Layer";
        public const string KeyTag = "m_TagString";
        public const string KeyLocalPosition = "m_LocalPosition";
        public const string KeyLocalRotation = "m_LocalRotation";
        public const string KeyLocalScale = "m_LocalScale";

        private static bool IsGameObjectKey(string key) => key == KeyIsActive || key == KeyLayer || key == KeyTag;

        public static int ApplyNodeStateToTargets(
            GameObject previewNode,
            string hierarchyPath,
            IReadOnlyCollection<string> modifiedPropertyPaths,
            IReadOnlyList<GameObject> targetPrefabs)
        {
            if (previewNode == null)
                throw new InvalidOperationException("preview node is null.");
            if (string.IsNullOrEmpty(hierarchyPath))
                throw new InvalidOperationException("selected hierarchy path is empty.");
            if (modifiedPropertyPaths == null || modifiedPropertyPaths.Count == 0)
                throw new InvalidOperationException("no modified properties.");
            if (!(targetPrefabs?.Count > 0))
                throw new InvalidOperationException("target objects is empty.");

            int appliedCount = 0;

            for (int i = 0; i < targetPrefabs.Count; i++)
            {
                GameObject go = targetPrefabs[i];
                if (go == null)
                {
                    Debug.LogWarning($"[FPrefabBatchOperations] Skipping null at targetPrefabs[{i}].");
                    continue;
                }

                if (go.scene.IsValid())
                {
                    Transform node = FHierarchyPathResolver.TryResolveTargetByPath(go.transform, hierarchyPath);
                    if (node == null)
                    {
                        Debug.LogWarning($"[FPrefabBatchOperations] '{go.name}' không có node '{hierarchyPath}', bỏ qua.");
                        continue;
                    }

                    ApplyModifiedStateToNode(previewNode, node, modifiedPropertyPaths);
                    EditorSceneManager.MarkSceneDirty(go.scene);
                    appliedCount++;
                }
                else
                {
                    string path = AssetDatabase.GetAssetPath(go);
                    if (string.IsNullOrEmpty(path))
                    {
                        Debug.LogWarning($"[FPrefabBatchOperations] Skipping targetPrefabs[{i}] (no asset path).");
                        continue;
                    }

                    GameObject prefabRoot = PrefabUtility.LoadPrefabContents(path);
                    try
                    {
                        Transform node = FHierarchyPathResolver.TryResolveTargetByPath(prefabRoot.transform, hierarchyPath);
                        if (node == null)
                        {
                            Debug.LogWarning($"[FPrefabBatchOperations] '{go.name}' không có node '{hierarchyPath}', bỏ qua.");
                        }
                        else
                        {
                            ApplyModifiedStateToNode(previewNode, node, modifiedPropertyPaths);
                            PrefabUtility.SaveAsPrefabAsset(prefabRoot, path);
                            appliedCount++;
                        }
                    }
                    finally
                    {
                        PrefabUtility.UnloadPrefabContents(prefabRoot);
                    }
                }
            }

            AssetDatabase.SaveAssets();
            AssetDatabase.Refresh();
            return appliedCount;
        }

        public static int DeleteNodeFromTargets(string hierarchyPath, IReadOnlyList<GameObject> targetPrefabs)
        {
            if (string.IsNullOrEmpty(hierarchyPath))
                throw new InvalidOperationException("selected hierarchy path is empty.");
            if (!(targetPrefabs?.Count > 0))
                throw new InvalidOperationException("target objects is empty.");

            int deletedCount = 0;

            for (int i = 0; i < targetPrefabs.Count; i++)
            {
                GameObject go = targetPrefabs[i];
                if (go == null)
                {
                    Debug.LogWarning($"[FPrefabBatchOperations] Skipping null at targetPrefabs[{i}].");
                    continue;
                }

                if (go.scene.IsValid())
                {
                    Transform node = FHierarchyPathResolver.TryResolveTargetByPath(go.transform, hierarchyPath);
                    if (node == null)
                    {
                        Debug.LogWarning($"[FPrefabBatchOperations] '{go.name}' không có node '{hierarchyPath}', bỏ qua.");
                        continue;
                    }

                    if (PrefabUtility.IsPartOfPrefabInstance(node.gameObject))
                    {
                        Debug.LogWarning($"[FPrefabBatchOperations] '{go.name}/{hierarchyPath}' là child của prefab instance trong scene, không thể xóa — hãy xóa trong prefab asset.");
                        continue;
                    }

                    Scene scene = go.scene;
                    Undo.DestroyObjectImmediate(node.gameObject);
                    EditorSceneManager.MarkSceneDirty(scene);
                    deletedCount++;
                }
                else
                {
                    string path = AssetDatabase.GetAssetPath(go);
                    if (string.IsNullOrEmpty(path))
                    {
                        Debug.LogWarning($"[FPrefabBatchOperations] Skipping targetPrefabs[{i}] (no asset path).");
                        continue;
                    }

                    GameObject prefabRoot = PrefabUtility.LoadPrefabContents(path);
                    try
                    {
                        Transform node = FHierarchyPathResolver.TryResolveTargetByPath(prefabRoot.transform, hierarchyPath);
                        if (node == null)
                        {
                            Debug.LogWarning($"[FPrefabBatchOperations] '{go.name}' không có node '{hierarchyPath}', bỏ qua.");
                        }
                        else
                        {
                            UnityEngine.Object.DestroyImmediate(node.gameObject);
                            PrefabUtility.SaveAsPrefabAsset(prefabRoot, path);
                            deletedCount++;
                        }
                    }
                    finally
                    {
                        PrefabUtility.UnloadPrefabContents(prefabRoot);
                    }
                }
            }

            AssetDatabase.SaveAssets();
            AssetDatabase.Refresh();
            return deletedCount;
        }

        private static void ApplyModifiedStateToNode(GameObject previewNode, Transform node, IReadOnlyCollection<string> keys)
        {
            var srcGo = new SerializedObject(previewNode);
            var srcTr = new SerializedObject(previewNode.transform);
            var dstGo = new SerializedObject(node.gameObject);
            var dstTr = new SerializedObject(node);

            foreach (string key in keys)
            {
                SerializedObject src = IsGameObjectKey(key) ? srcGo : srcTr;
                SerializedObject dst = IsGameObjectKey(key) ? dstGo : dstTr;
                dst.CopyFromSerializedProperty(src.FindProperty(key));
            }

            dstGo.ApplyModifiedProperties();
            dstTr.ApplyModifiedProperties();
            EditorUtility.SetDirty(node.gameObject);
        }
    }
}
