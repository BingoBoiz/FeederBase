using System;
using System.Collections.Generic;
using UnityEditor;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace Feeder
{
    public static class FSceneMeshHighlightUtils
    {
        /// <summary>
        /// Collects all GameObjects in the scene that have a MeshFilter using the given mesh (reference equality).
        /// </summary>
        public static List<GameObject> FindGameObjectsWithMesh(Scene scene, Mesh mesh)
        {
            if (!scene.IsValid())
                throw new InvalidOperationException("scene is not valid.");
            if (mesh == null)
                throw new InvalidOperationException("mesh is null.");

            var result = new List<GameObject>();
            var roots = scene.GetRootGameObjects();
            for (int i = 0; i < roots.Length; i++)
            {
                var filters = roots[i].GetComponentsInChildren<MeshFilter>(true);
                for (int j = 0; j < filters.Length; j++)
                {
                    if (filters[j].sharedMesh == mesh)
                        result.Add(filters[j].gameObject);
                }
            }
            return result;
        }

        public static List<GameObject> FindGameObjectsWithAnyMesh(Scene scene, IReadOnlyCollection<Mesh> meshes)
        {
            if (!scene.IsValid())
                throw new InvalidOperationException("scene is not valid.");
            if (meshes == null)
                throw new InvalidOperationException("meshes is null.");

            HashSet<Mesh> meshSet = new HashSet<Mesh>();
            foreach (Mesh mesh in meshes)
            {
                if (mesh != null)
                    meshSet.Add(mesh);
            }

            var result = new List<GameObject>();
            if (meshSet.Count == 0)
                return result;

            HashSet<GameObject> seen = new HashSet<GameObject>();
            var roots = scene.GetRootGameObjects();
            for (int i = 0; i < roots.Length; i++)
            {
                var filters = roots[i].GetComponentsInChildren<MeshFilter>(true);
                for (int j = 0; j < filters.Length; j++)
                {
                    MeshFilter filter = filters[j];
                    if (filter == null || !meshSet.Contains(filter.sharedMesh)) continue;
                    if (seen.Add(filter.gameObject))
                        result.Add(filter.gameObject);
                }
            }

            return result;
        }

        // keepVisible stay visible+pickable; MeshHighlightDrawer holders are shown but not pickable so they don't block selection
        public static void IsolateGameObjects(Scene scene, IReadOnlyList<GameObject> keepVisible)
        {
            if (!scene.IsValid())
                throw new InvalidOperationException("scene is not valid.");
            if (keepVisible == null)
                throw new InvalidOperationException("keepVisible is null.");

            var sv = SceneVisibilityManager.instance;
            sv.HideAll();
            sv.DisableAllPicking();
            for (int i = 0; i < keepVisible.Count; i++)
            {
                GameObject go = keepVisible[i];
                if (go == null) continue;
                sv.Show(go, true);
                sv.EnablePicking(go, true);
            }
        }

        /// <summary>
        /// Restores visibility and picking for all (use after IsolateGameObjects).
        /// </summary>
        public static void ShowAllAndEnablePicking()
        {
            var sv = SceneVisibilityManager.instance;
            sv.ShowAll();
            sv.EnableAllPicking();
        }
    }
}
