using System;
using System.Collections.Generic;
using UnityEditor;
using UnityEngine;

namespace Feeder
{
    public sealed class ComponentFindHit
    {
        public int TargetListIndex { get; }
        public string TargetRootName { get; }
        public bool IsScene { get; }
        public string PrefabAssetPath { get; }
        public string RelativeHierarchyPath { get; }
        public GameObject SceneGameObject { get; }
        public Component SceneHostComponent { get; }

        public ComponentFindHit(
            int targetListIndex,
            string targetRootName,
            bool isScene,
            string prefabAssetPath,
            string relativeHierarchyPath,
            GameObject sceneGameObject,
            Component sceneHostComponent)
        {
            TargetListIndex = targetListIndex;
            TargetRootName = targetRootName ?? throw new InvalidOperationException("target root name is null.");
            IsScene = isScene;
            PrefabAssetPath = prefabAssetPath;
            RelativeHierarchyPath = relativeHierarchyPath ?? throw new InvalidOperationException("relative path is null.");
            SceneGameObject = sceneGameObject;
            SceneHostComponent = sceneHostComponent;
        }
    }

    public static class FComponentBatchOperations
    {
        public delegate void TargetHierarchyComponentsVisitor(
            int targetIndex,
            GameObject targetListGameObject,
            bool isSceneObject,
            string prefabAssetPathOrNull,
            GameObject loadedHierarchyRoot,
            Component[] componentsInHierarchy);

        public static void VisitEachTargetHierarchyHavingComponents(
            Type componentType,
            IReadOnlyList<GameObject> targetPrefabs,
            TargetHierarchyComponentsVisitor visit)
        {
            if (componentType == null)
                throw new InvalidOperationException("component type is null.");
            if (!(targetPrefabs?.Count > 0))
                throw new InvalidOperationException("target objects is empty.");

            for (int i = 0; i < targetPrefabs.Count; i++)
            {
                GameObject go = targetPrefabs[i];
                if (go == null)
                {
                    Debug.LogWarning($"[FComponentBatchOperations] Skipping null at targetPrefabs[{i}].");
                    continue;
                }

                if (go.scene.IsValid())
                {
                    Component[] targets = go.GetComponentsInChildren(componentType, true);
                    visit(i, go, true, null, go, targets);
                }
                else
                {
                    string path = AssetDatabase.GetAssetPath(go);
                    if (string.IsNullOrEmpty(path))
                    {
                        Debug.LogWarning($"[FComponentBatchOperations] Skipping targetPrefabs[{i}] (no asset path).");
                        continue;
                    }

                    GameObject prefabRoot = PrefabUtility.LoadPrefabContents(path);
                    try
                    {
                        Component[] targets = prefabRoot.GetComponentsInChildren(componentType, true);
                        visit(i, go, false, path, prefabRoot, targets);
                    }
                    finally
                    {
                        PrefabUtility.UnloadPrefabContents(prefabRoot);
                    }
                }
            }
        }

        public static void CollectComponentFindHits(
            Type componentType,
            IReadOnlyList<GameObject> targetPrefabs,
            List<ComponentFindHit> appendTo)
        {
            if (appendTo == null)
                throw new InvalidOperationException("append list is null.");

            VisitEachTargetHierarchyHavingComponents(componentType, targetPrefabs,
                (int targetIndex, GameObject targetListGameObject, bool isSceneObject, string prefabAssetPathOrNull, GameObject loadedHierarchyRoot, Component[] componentsInHierarchy) =>
                {
                    Transform rootTransform = loadedHierarchyRoot.transform;
                    for (int c = 0; c < componentsInHierarchy.Length; c++)
                    {
                        Component comp = componentsInHierarchy[c];
                        Transform hostTransform = comp.transform;
                        string relativePath = FHierarchyPathResolver.BuildRelativePathFromAncestorTransform(rootTransform, hostTransform);
                        if (isSceneObject)
                        {
                            appendTo.Add(new ComponentFindHit(
                                targetIndex,
                                targetListGameObject.name,
                                true,
                                null,
                                relativePath,
                                hostTransform.gameObject,
                                comp));
                        }
                        else
                        {
                            appendTo.Add(new ComponentFindHit(
                                targetIndex,
                                targetListGameObject.name,
                                false,
                                prefabAssetPathOrNull,
                                relativePath,
                                null,
                                null));
                        }
                    }
                });
        }

        public static int AddComponentToTargets(
            Type componentType,
            string hierarchyPath,
            IReadOnlyList<GameObject> targetPrefabs,
            IReadOnlyList<string> perTargetPaths = null)
        {
            if (componentType == null)
                throw new InvalidOperationException("component type is null.");
            if (!typeof(Component).IsAssignableFrom(componentType) || componentType.IsAbstract)
                throw new InvalidOperationException($"component type is invalid: {componentType.FullName}.");
            if (componentType == typeof(Transform) || componentType == typeof(RectTransform))
                throw new InvalidOperationException($"cannot add {componentType.Name} via AddComponent.");
            if (string.IsNullOrEmpty(hierarchyPath))
                throw new InvalidOperationException("selected hierarchy path is empty.");
            if (!(targetPrefabs?.Count > 0))
                throw new InvalidOperationException("target objects is empty.");
            if (perTargetPaths != null && perTargetPaths.Count != targetPrefabs.Count)
                throw new InvalidOperationException("per-target paths count does not match targets count.");

            int addedCount = 0;

            for (int i = 0; i < targetPrefabs.Count; i++)
            {
                var go = targetPrefabs[i];
                if (go == null)
                {
                    Debug.LogWarning($"[FComponentBatchOperations] Skipping null at targetPrefabs[{i}].");
                    continue;
                }

                string pathForTarget = perTargetPaths != null ? perTargetPaths[i] : hierarchyPath;
                if (string.IsNullOrEmpty(pathForTarget))
                {
                    Debug.LogWarning($"[FComponentBatchOperations] '{go.name}' không có node '{hierarchyPath}', bỏ qua.");
                    continue;
                }

                if (go.scene.IsValid())
                {
                    var target = FHierarchyPathResolver.TryResolveTargetByPath(go.transform, pathForTarget);
                    if (target == null)
                    {
                        Debug.LogWarning($"[FComponentBatchOperations] '{go.name}' không có node '{pathForTarget}', bỏ qua.");
                        continue;
                    }

                    if (target.GetComponent(componentType) == null)
                    {
                        Undo.AddComponent(target.gameObject, componentType);
                        addedCount++;
                    }
                }
                else
                {
                    var path = AssetDatabase.GetAssetPath(go);
                    if (string.IsNullOrEmpty(path))
                    {
                        Debug.LogWarning($"[FComponentBatchOperations] Skipping targetPrefabs[{i}] (no asset path).");
                        continue;
                    }

                    var prefabRoot = PrefabUtility.LoadPrefabContents(path);
                    try
                    {
                        var target = FHierarchyPathResolver.TryResolveTargetByPath(prefabRoot.transform, pathForTarget);
                        if (target == null)
                        {
                            Debug.LogWarning($"[FComponentBatchOperations] '{go.name}' không có node '{pathForTarget}', bỏ qua.");
                        }
                        else if (target.GetComponent(componentType) == null)
                        {
                            target.gameObject.AddComponent(componentType);
                            addedCount++;
                            PrefabUtility.SaveAsPrefabAsset(prefabRoot, path);
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
            return addedCount;
        }

        public static int ApplyModifyToCachedComponentHits(
            Type componentType,
            Component previewComponent,
            IReadOnlyCollection<string> modifiedPropertyPaths,
            bool incrementChanges,
            int incrementRate,
            IReadOnlyList<ComponentFindHit> hits)
        {
            if (componentType == null)
                throw new InvalidOperationException("component type is null.");
            if (previewComponent == null)
                throw new InvalidOperationException("preview component is null.");
            if (modifiedPropertyPaths == null || modifiedPropertyPaths.Count == 0)
                throw new InvalidOperationException("no modified properties.");
            if (hits == null)
                throw new InvalidOperationException("hits is null.");

            if (hits.Count == 0)
            {
                Debug.Log("[FComponentBatchOperations] ApplyModify: cache empty, nothing to modify.");
                return 0;
            }

            int modifiedCount = 0;
            string openPrefabPath = null;
            GameObject openPrefabRoot = null;
            bool openPrefabDirty = false;

            try
            {
                for (int i = 0; i < hits.Count; i++)
                {
                    ComponentFindHit hit = hits[i];
                    if (hit.IsScene)
                    {
                        CloseOpenPrefabSessionForBatch(ref openPrefabPath, ref openPrefabRoot, ref openPrefabDirty);

                        Component dst = hit.SceneHostComponent;
                        if (dst == null)
                            throw new InvalidOperationException("scene hit has no SceneHostComponent.");

                        FComponentPropertyApplier.ApplyDifferences(
                            previewComponent,
                            dst,
                            modifiedPropertyPaths,
                            incrementChanges,
                            incrementRate,
                            hit.TargetListIndex);
                        modifiedCount++;
                        continue;
                    }

                    if (hit.PrefabAssetPath != openPrefabPath)
                    {
                        CloseOpenPrefabSessionForBatch(ref openPrefabPath, ref openPrefabRoot, ref openPrefabDirty);

                        GameObject loaded = PrefabUtility.LoadPrefabContents(hit.PrefabAssetPath);
                        openPrefabPath = hit.PrefabAssetPath;
                        openPrefabRoot = loaded;
                        openPrefabDirty = false;
                    }

                    Component prefabDst = ResolveCachedPrefabHitComponent(componentType, openPrefabRoot, hit);
                    FComponentPropertyApplier.ApplyDifferences(
                        previewComponent,
                        prefabDst,
                        modifiedPropertyPaths,
                        incrementChanges,
                        incrementRate,
                        hit.TargetListIndex);
                    modifiedCount++;
                    openPrefabDirty = true;
                }
            }
            finally
            {
                CloseOpenPrefabSessionForBatch(ref openPrefabPath, ref openPrefabRoot, ref openPrefabDirty);
            }

            AssetDatabase.SaveAssets();
            AssetDatabase.Refresh();
            return modifiedCount;
        }

        public static int RemoveComponentFromCachedComponentHits(Type componentType, IReadOnlyList<ComponentFindHit> hits)
        {
            if (componentType == null)
                throw new InvalidOperationException("component type is null.");
            if (hits == null)
                throw new InvalidOperationException("hits is null.");

            if (hits.Count == 0)
            {
                Debug.Log("[FComponentBatchOperations] RemoveComponent: cache empty, nothing to remove.");
                return 0;
            }

            int removedCount = 0;
            string openPrefabPath = null;
            GameObject openPrefabRoot = null;
            bool openPrefabDirty = false;

            try
            {
                for (int i = 0; i < hits.Count; i++)
                {
                    ComponentFindHit hit = hits[i];
                    if (hit.IsScene)
                    {
                        CloseOpenPrefabSessionForBatch(ref openPrefabPath, ref openPrefabRoot, ref openPrefabDirty);

                        Component dst = hit.SceneHostComponent;
                        if (dst == null)
                            throw new InvalidOperationException("scene hit has no SceneHostComponent.");

                        Undo.DestroyObjectImmediate(dst);
                        removedCount++;
                        continue;
                    }

                    if (hit.PrefabAssetPath != openPrefabPath)
                    {
                        CloseOpenPrefabSessionForBatch(ref openPrefabPath, ref openPrefabRoot, ref openPrefabDirty);

                        GameObject loaded = PrefabUtility.LoadPrefabContents(hit.PrefabAssetPath);
                        openPrefabPath = hit.PrefabAssetPath;
                        openPrefabRoot = loaded;
                        openPrefabDirty = false;
                    }

                    Component prefabDst = ResolveCachedPrefabHitComponent(componentType, openPrefabRoot, hit);
                    UnityEngine.Object.DestroyImmediate(prefabDst, true);
                    removedCount++;
                    openPrefabDirty = true;
                }
            }
            finally
            {
                CloseOpenPrefabSessionForBatch(ref openPrefabPath, ref openPrefabRoot, ref openPrefabDirty);
            }

            AssetDatabase.SaveAssets();
            AssetDatabase.Refresh();
            return removedCount;
        }

        // assumes one component of componentType on the resolved host transform
        private static Component ResolveCachedPrefabHitComponent(Type componentType, GameObject loadedPrefabRoot, ComponentFindHit hit)
        {
            Transform rootTransform = loadedPrefabRoot.transform;
            Transform hostTransform = string.IsNullOrEmpty(hit.RelativeHierarchyPath)
                ? rootTransform
                : FHierarchyPathResolver.ResolveTargetByPath(rootTransform, hit.RelativeHierarchyPath);
            Component found = hostTransform.GetComponent(componentType);
            if (found == null)
                throw new InvalidOperationException($"no {componentType.Name} on '{hit.PrefabAssetPath}' at '{hit.RelativeHierarchyPath}'.");

            return found;
        }

        private static void CloseOpenPrefabSessionForBatch(ref string openPrefabPath, ref GameObject openPrefabRoot, ref bool openPrefabDirty)
        {
            if (openPrefabPath == null || openPrefabRoot == null)
                return;

            if (openPrefabDirty)
                PrefabUtility.SaveAsPrefabAsset(openPrefabRoot, openPrefabPath);

            PrefabUtility.UnloadPrefabContents(openPrefabRoot);
            openPrefabPath = null;
            openPrefabRoot = null;
            openPrefabDirty = false;
        }
    }
}
