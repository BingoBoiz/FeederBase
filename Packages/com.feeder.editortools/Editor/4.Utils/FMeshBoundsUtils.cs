using System;
using UnityEngine;

namespace Feeder
{
    public static class FMeshBoundsUtils
    {
        public static Bounds GetCombinedWorldBoundsOrThrow(Transform root)
        {
            if (root == null)
                throw new InvalidOperationException("root is null.");

            var meshRenderers = root.GetComponentsInChildren<MeshRenderer>(true);
            if (meshRenderers.Length == 0)
                throw new InvalidOperationException("no mesh renderer found.");

            Bounds combined = default;
            bool initialized = false;

            for (int i = 0; i < meshRenderers.Length; i++)
            {
                var r = meshRenderers[i];
                if (r == null || !r.enabled || !r.gameObject.activeInHierarchy)
                    continue;

                if (!initialized)
                {
                    combined = r.bounds;
                    initialized = true;
                }
                else
                {
                    combined.Encapsulate(r.bounds);
                }
            }

            if (!initialized)
                throw new InvalidOperationException("no active mesh renderer found.");

            return combined;
        }
    }
}
