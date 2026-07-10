using System;
using System.Collections.Generic;
using UnityEditor;
using UnityEngine;

namespace Feeder
{
    public static class FModelScaleToColliderService
    {
        public static int ScaleTargets(GameObject basePrefab, string targetPath, IReadOnlyList<GameObject> targets)
        {
            if (basePrefab == null)
                throw new InvalidOperationException("base prefab is null.");
            if (string.IsNullOrEmpty(targetPath))
                throw new InvalidOperationException("target path is empty.");
            if (!(targets?.Count > 0))
                throw new InvalidOperationException("targets list is empty.");

            var col = basePrefab.GetComponent<Collider>() ?? throw new InvalidOperationException("base prefab collider is null.");
            var colSize = GetColliderWorldSize(col);

            int scaledCount = 0;

            for (int i = 0; i < targets.Count; i++)
            {
                var target = targets[i];
                if (target == null)
                {
                    Debug.LogWarning($"[FModelScaleToColliderService] Skipping null at targets[{i}].");
                    continue;
                }

                var root = FPrefabRootResolver.GetRootTransform(target, out GameObject prefabRoot, out bool shouldUnload);
                bool shouldSave = false;

                try
                {
                    var modelRoot = ResolveTargetTransform(root, targetPath);
                    ApplyScale(colSize, modelRoot);

                    if (target.scene.IsValid())
                    {
                        Undo.RecordObject(modelRoot, "Scale Model To Collider");
                        EditorUtility.SetDirty(modelRoot);
                    }
                    else
                    {
                        shouldSave = true;
                    }
                }
                finally
                {
                    if (shouldUnload && prefabRoot != null)
                    {
                        if (shouldSave)
                        {
                            var assetPath = AssetDatabase.GetAssetPath(target);
                            if (string.IsNullOrEmpty(assetPath))
                                throw new InvalidOperationException($"asset path is empty for {target.name}.");

                            PrefabUtility.SaveAsPrefabAsset(prefabRoot, assetPath);
                        }

                        PrefabUtility.UnloadPrefabContents(prefabRoot);
                    }
                }

                scaledCount++;
            }

            AssetDatabase.SaveAssets();
            AssetDatabase.Refresh();
            return scaledCount;
        }

        private static Vector3 GetColliderWorldSize(Collider col)
        {
            Vector3 localSize;
            switch (col)
            {
                case BoxCollider box:
                    localSize = box.size;
                    break;
                case SphereCollider sphere:
                    localSize = Vector3.one * (sphere.radius * 2f);
                    break;
                case CapsuleCollider capsule:
                    localSize = new Vector3(capsule.radius * 2f, capsule.height, capsule.radius * 2f);
                    break;
                default:
                    throw new InvalidOperationException($"unsupported collider type: {col.GetType().Name}.");
            }

            return Vector3.Scale(localSize, col.transform.lossyScale);
        }

        private static Transform ResolveTargetTransform(Transform root, string targetPath)
        {
            if (targetPath == FHierarchyPathOptionsProvider.RootToken)
                return root;

            return FHierarchyPathResolver.ResolveTargetByPath(root, targetPath);
        }

        private static void ApplyScale(Vector3 colliderWorldSize, Transform modelRoot)
        {
            if (modelRoot == null)
                throw new InvalidOperationException("model root is null.");

            var meshBounds = FMeshBoundsUtils.GetCombinedWorldBoundsOrThrow(modelRoot);
            var meshSize = meshBounds.size;

            if (meshSize.x <= 0f || meshSize.y <= 0f || meshSize.z <= 0f)
                throw new InvalidOperationException("mesh bounds size is invalid.");

            float scaleX = colliderWorldSize.x / meshSize.x;
            float scaleY = colliderWorldSize.y / meshSize.y;
            float scaleZ = colliderWorldSize.z / meshSize.z;
            float uniformScale = Mathf.Min(scaleX, scaleY, scaleZ);

            modelRoot.localScale *= uniformScale;
        }
    }
}
