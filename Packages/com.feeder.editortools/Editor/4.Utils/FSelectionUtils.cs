using System.Collections.Generic;
using UnityEditor;
using UnityEngine;

namespace Feeder
{
    /// <summary>
    /// Helpers to pull typed objects out of the current editor Selection, and to
    /// select/ping results in the Project window after a tool operation.
    /// </summary>
    public static class FSelectionUtils
    {
        /// <summary>All selected objects (assets or scene objects), nulls removed, deduped, order preserved.</summary>
        public static List<Object> CollectAssetsAndSceneObjects()
        {
            var result = new List<Object>();
            var seen = new HashSet<Object>();
            Object[] selection = Selection.objects;
            if (selection == null) return result;
            foreach (Object obj in selection)
                if (obj != null && seen.Add(obj))
                    result.Add(obj);
            return result;
        }

        /// <summary>Selected GameObjects (scene objects and prefab assets), nulls removed, deduped.</summary>
        public static List<GameObject> CollectGameObjects()
        {
            var result = new List<GameObject>();
            var seen = new HashSet<GameObject>();
            GameObject[] selection = Selection.gameObjects;
            if (selection == null) return result;
            foreach (GameObject go in selection)
                if (go != null && seen.Add(go))
                    result.Add(go);
            return result;
        }

        /// <summary>Meshes selected directly, plus meshes on MeshFilter/SkinnedMeshRenderer in selected GameObject hierarchies.</summary>
        public static List<Mesh> CollectMeshes()
        {
            var result = new List<Mesh>();
            var seen = new HashSet<Mesh>();
            Object[] selection = Selection.objects;
            if (selection == null) return result;

            for (int i = 0; i < selection.Length; i++)
            {
                if (selection[i] is Mesh mesh)
                {
                    if (mesh != null && seen.Add(mesh)) result.Add(mesh);
                }
                else if (selection[i] is GameObject go)
                {
                    foreach (MeshFilter mf in go.GetComponentsInChildren<MeshFilter>(true))
                        if (mf.sharedMesh != null && seen.Add(mf.sharedMesh)) result.Add(mf.sharedMesh);
                    foreach (SkinnedMeshRenderer smr in go.GetComponentsInChildren<SkinnedMeshRenderer>(true))
                        if (smr.sharedMesh != null && seen.Add(smr.sharedMesh)) result.Add(smr.sharedMesh);
                }
            }
            return result;
        }

        /// <summary>MeshRenderers on selected GameObjects and their children (including inactive), deduped.</summary>
        public static List<MeshRenderer> CollectMeshRenderers()
        {
            var result = new List<MeshRenderer>();
            var seen = new HashSet<MeshRenderer>();
            GameObject[] selection = Selection.gameObjects;
            if (selection == null) return result;
            foreach (GameObject go in selection)
            {
                if (go == null) continue;
                foreach (MeshRenderer mr in go.GetComponentsInChildren<MeshRenderer>(true))
                    if (mr != null && seen.Add(mr))
                        result.Add(mr);
            }
            return result;
        }

        /// <summary>First selected object that is a ScriptableObject, or null.</summary>
        public static ScriptableObject FirstScriptableObject()
        {
            Object[] selection = Selection.objects;
            if (selection == null) return null;
            foreach (Object obj in selection)
                if (obj is ScriptableObject so)
                    return so;
            return null;
        }

        /// <summary>
        /// Sets the editor Selection to the given objects (capped so a huge batch doesn't flood the
        /// inspector) and pings the first one in the Project window.
        /// </summary>
        public static void SelectAndPing(IReadOnlyList<Object> objects, int selectCap = 64)
        {
            if (objects == null) return;
            var picked = new List<Object>();
            for (int i = 0; i < objects.Count && picked.Count < selectCap; i++)
                if (objects[i] != null)
                    picked.Add(objects[i]);
            if (picked.Count == 0) return;
            Selection.objects = picked.ToArray();
            EditorGUIUtility.PingObject(picked[0]);
        }

        /// <summary>Null-safe ping in the Project window.</summary>
        public static void Ping(Object obj)
        {
            if (obj != null)
                EditorGUIUtility.PingObject(obj);
        }
    }
}
