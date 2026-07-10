using System;
using UnityEditor;
using UnityEngine;

namespace Feeder
{
    public static class FPrefabRootResolver
    {
        public static Transform GetRootTransform(GameObject go, out GameObject prefabRoot, out bool shouldUnload)
        {
            prefabRoot = null;
            shouldUnload = false;

            if (go?.scene.IsValid() ?? false)
            {
                return go.transform;
            }

            if (go == null)
                throw new InvalidOperationException("target object is null.");

            var path = AssetDatabase.GetAssetPath(go);
            if (string.IsNullOrEmpty(path))
                throw new InvalidOperationException($"asset path is empty for {go.name}.");

            prefabRoot = PrefabUtility.LoadPrefabContents(path);
            shouldUnload = true;
            return prefabRoot?.transform ?? throw new InvalidOperationException("prefab root is null.");
        }
    }
}
