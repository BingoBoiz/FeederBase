using System;
using System.Collections.Generic;
using UnityEditor;
using UnityEngine;

namespace Feeder
{
    public static class FNameOffsetAdjusterService
    {
        public static int AdjustOffset(IReadOnlyList<GameObject> targets, string holderPath, float offsetY)
        {
            if (!(targets?.Count > 0))
                throw new InvalidOperationException("targets list is empty.");
            if (string.IsNullOrEmpty(holderPath))
                throw new InvalidOperationException("holder path is empty.");

            int adjustedCount = 0;

            for (int i = 0; i < targets.Count; i++)
            {
                var target = targets[i];
                if (target == null)
                {
                    Debug.LogWarning($"[FNameOffsetAdjusterService] Skipping null at targets[{i}].");
                    continue;
                }

                var root = FPrefabRootResolver.GetRootTransform(target, out GameObject prefabRoot, out bool shouldUnload);
                bool shouldSave = false;

                try
                {
                    var holder = ResolveHolderTransform(root, holderPath);
                    AdjustHolderOffset(holder, offsetY);

                    if (target.scene.IsValid())
                    {
                        Undo.RecordObject(holder, "Adjust Name Offset");
                        EditorUtility.SetDirty(holder);
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

                adjustedCount++;
            }

            AssetDatabase.SaveAssets();
            AssetDatabase.Refresh();
            return adjustedCount;
        }

        private static Transform ResolveHolderTransform(Transform root, string holderPath)
        {
            if (holderPath == FHierarchyPathOptionsProvider.RootToken)
                return root;

            return FHierarchyPathResolver.ResolveTargetByPath(root, holderPath);
        }

        private static void AdjustHolderOffset(Transform holder, float offsetY)
        {
            if (holder == null)
                throw new InvalidOperationException("holder is null.");
            if (holder.childCount < 2)
                throw new InvalidOperationException("holder must have at least 2 children.");

            var nameChild = holder.GetChild(0);
            var meshChild = holder.GetChild(1);

            var combined = FMeshBoundsUtils.GetCombinedWorldBoundsOrThrow(meshChild);
            var minWorld = new Vector3(combined.center.x, combined.min.y, combined.center.z);
            float minLocalY = holder.InverseTransformPoint(minWorld).y;

            if (minLocalY < 0f)
            {
                var meshLocalPos = meshChild.localPosition;
                meshLocalPos.y -= minLocalY;
                meshChild.localPosition = meshLocalPos;

                combined = FMeshBoundsUtils.GetCombinedWorldBoundsOrThrow(meshChild);
            }

            var topWorld = new Vector3(combined.center.x, combined.max.y, combined.center.z);
            float topLocalY = holder.InverseTransformPoint(topWorld).y;

            var lp = nameChild.localPosition;
            lp.y = topLocalY + offsetY;
            nameChild.localPosition = lp;
        }
    }
}
