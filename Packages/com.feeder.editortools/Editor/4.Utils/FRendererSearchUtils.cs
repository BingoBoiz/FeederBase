using System.Collections.Generic;
using UnityEditor;
using UnityEngine;

namespace Feeder
{
    /// <summary>
    /// Finds scene objects using a given material and focuses the Scene view on them.
    /// Shared by the deduplicate material/texture tools.
    /// </summary>
    public static class FRendererSearchUtils
    {
        /// <summary>First GameObject under any root whose MeshRenderer/SkinnedMeshRenderer uses the material.</summary>
        public static GameObject FindFirstGameObjectWithMaterial(IReadOnlyList<GameObject> roots, Material material)
        {
            if (roots == null || material == null)
                return null;
            for (int i = 0; i < roots.Count; i++)
            {
                GameObject root = roots[i];
                if (root == null)
                    continue;
                GameObject found = FindInHierarchy(root.transform, material);
                if (found != null)
                    return found;
            }
            return null;
        }

        /// <summary>Selects the GameObject, pings it and frames it in the last active Scene view.</summary>
        public static void SelectPingAndFocus(GameObject go)
        {
            if (go == null)
                return;
            Selection.activeGameObject = go;
            EditorGUIUtility.PingObject(go);
            SceneView sceneView = SceneView.lastActiveSceneView;
            if (sceneView != null)
            {
                sceneView.Focus();
                sceneView.FrameSelected();
            }
        }

        private static GameObject FindInHierarchy(Transform root, Material material)
        {
            foreach (MeshRenderer mr in root.GetComponentsInChildren<MeshRenderer>(true))
                if (RendererUsesMaterial(mr, material))
                    return mr.gameObject;
            foreach (SkinnedMeshRenderer sr in root.GetComponentsInChildren<SkinnedMeshRenderer>(true))
                if (RendererUsesMaterial(sr, material))
                    return sr.gameObject;
            return null;
        }

        private static bool RendererUsesMaterial(Renderer renderer, Material material)
        {
            Material[] shared = renderer.sharedMaterials;
            for (int i = 0; i < shared.Length; i++)
                if (shared[i] == material)
                    return true;
            return false;
        }
    }
}
