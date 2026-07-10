using System;
using System.Collections.Generic;
using Sirenix.OdinInspector;
using Sirenix.Serialization;
using TMPro;
using UnityEditor;
using UnityEditorInternal;
using UnityEngine;

namespace Feeder
{
    public sealed class FDeduplicateMaterialTool : FTargetPrefabsToolBase
    {
        private static readonly string[] BaseMapPropertyNames = { "_BaseMap", "_MainTex" };

        [PropertyOrder(80)]
        [OdinSerialize, HideInInspector]
        private List<Material> _allCollectedMaterials = new List<Material>();

        [OdinSerialize, HideInInspector]
        private List<List<Material>> _duplicateGroups = new List<List<Material>>();
        private Dictionary<int, ReorderableList> _reorderableListsByGroupIndex = new Dictionary<int, ReorderableList>();

        protected override string GetDescription()
        {
            return "Quét TargetPrefabs tìm material trùng lặp (cùng base map texture) rồi gộp lại. Mỗi nhóm bên dưới là một tập material trùng nhau.";
        }

        [Title("Settings")]
        [LabelText("Skip TextMeshPro")]
        [ShowInInspector, OdinSerialize]
        private bool skipTextMeshPro = true;

        [LabelText("Skip Disabled GameObjects")]
        [ShowInInspector, OdinSerialize]
        private bool skipDisabledGameObjects = true;

        [OnInspectorGUI]
        private void DrawGuide()
        {
            GUILayout.Space(2);
            FStylesUtils.DrawInfoBox(
                "TargetPrefabs    root chứa MeshRenderer\n" +
                "hai material bị coi là trùng nếu cùng _BaseMap / _MainTex\n" +
                "Resolve          giữ lại một material, gán lại toàn bộ ref còn lại"
            );
            GUILayout.Space(4);
        }

        [Button(ButtonSizes.Large), GUIColor(0.3f, 0.8f, 1f)]
        public void FindDuplicateMaterials()
        {
            if (TargetPrefabs == null || TargetPrefabs.Count == 0)
            {
                Debug.LogWarning("[FDeduplicateMaterialTool] Add at least one TargetObject.");
                _allCollectedMaterials.Clear();
                _duplicateGroups.Clear();
                _reorderableListsByGroupIndex.Clear();
                return;
            }

            HashSet<Material> uniqueMaterials = CollectAllMaterialsFromTargetPrefabs();
            _allCollectedMaterials.Clear();
            _allCollectedMaterials.AddRange(uniqueMaterials);

            Dictionary<Texture2D, List<Material>> textureToMaterials = new Dictionary<Texture2D, List<Material>>();
            List<Material> materialsWithNoBaseMap = new List<Material>();
            GroupMaterialsByBaseMapTexture(_allCollectedMaterials, textureToMaterials, materialsWithNoBaseMap);
            _duplicateGroups.Clear();
            _duplicateGroups.AddRange(KeepOnlyDuplicateGroups(textureToMaterials, materialsWithNoBaseMap));
            _reorderableListsByGroupIndex.Clear();
            EditorUtility.SetDirty(this);

            int groupCount = _duplicateGroups.Count;
            Debug.Log($"<color=green>[FDeduplicateMaterialTool] Collected {_allCollectedMaterials.Count} unique material(s), found {groupCount} duplicate group(s).</color>");
        }

        private HashSet<Material> CollectAllMaterialsFromTargetPrefabs()
        {
            HashSet<Material> collected = new HashSet<Material>();

            for (int i = 0; i < TargetPrefabs.Count; i++)
            {
                GameObject target = TargetPrefabs[i];
                if (target == null)
                {
                    Debug.LogWarning($"[FDeduplicateMaterialTool] Skipping null at TargetPrefabs[{i}].");
                    continue;
                }

                if (PrefabUtility.IsPartOfPrefabAsset(target))
                    continue;

                MeshFilter[] meshFilters = target.GetComponentsInChildren<MeshFilter>(true);
                foreach (MeshFilter meshFilter in meshFilters)
                {
                    GameObject child = meshFilter.gameObject;
                    if (skipDisabledGameObjects && !child.activeInHierarchy)
                        continue;
                    if (skipTextMeshPro && HasTextMeshProComponent(child))
                        continue;

                    MeshRenderer meshRenderer = child.GetComponent<MeshRenderer>();
                    if (meshRenderer == null)
                        continue;

                    Material[] materials = meshRenderer.sharedMaterials;
                    if (materials == null)
                        continue;

                    foreach (Material mat in materials)
                    {
                        if (mat != null)
                            collected.Add(mat);
                    }
                }
            }

            return collected;
        }

        private static Texture2D GetBaseMapTexture(Material material)
        {
            if (material == null)
                return null;
            foreach (string propName in BaseMapPropertyNames)
            {
                if (!material.HasProperty(propName))
                    continue;
                Texture tex = material.GetTexture(propName);
                if (tex is Texture2D tex2d)
                    return tex2d;
            }
            return null;
        }

        private static void GroupMaterialsByBaseMapTexture(
            List<Material> materials,
            Dictionary<Texture2D, List<Material>> textureToMaterials,
            List<Material> materialsWithNoBaseMap)
        {
            textureToMaterials.Clear();
            materialsWithNoBaseMap.Clear();

            foreach (Material mat in materials)
            {
                Texture2D baseMap = GetBaseMapTexture(mat);
                if (baseMap == null)
                {
                    materialsWithNoBaseMap.Add(mat);
                    continue;
                }
                if (!textureToMaterials.TryGetValue(baseMap, out List<Material> list))
                {
                    list = new List<Material>();
                    textureToMaterials[baseMap] = list;
                }
                list.Add(mat);
            }
        }

        private static List<List<Material>> KeepOnlyDuplicateGroups(
            Dictionary<Texture2D, List<Material>> textureToMaterials,
            List<Material> materialsWithNoBaseMap)
        {
            List<List<Material>> groups = new List<List<Material>>();
            foreach (List<Material> list in textureToMaterials.Values)
            {
                if (list.Count >= 2)
                    groups.Add(list);
            }
            if (materialsWithNoBaseMap.Count >= 2)
                groups.Add(materialsWithNoBaseMap);
            return groups;
        }

        [PropertyOrder(100)]
        [OnInspectorGUI]
        private void DrawDuplicateGroups()
        {
            if (_duplicateGroups.Count == 0)
            {
                EditorGUILayout.HelpBox("Add TargetPrefabs (roots with MeshRenderers), then click FindDuplicateMaterials.", MessageType.Info);
                return;
            }

            for (int groupIndex = 0; groupIndex < _duplicateGroups.Count; groupIndex++)
            {
                List<Material> group = _duplicateGroups[groupIndex];
                if (group == null || group.Count == 0)
                    continue;

                Rect headerRect = EditorGUILayout.GetControlRect(false, EditorGUIUtility.singleLineHeight);
                float resolveWidth = 70f;
                Rect titleRect = new Rect(headerRect.x, headerRect.y, headerRect.width - resolveWidth - 4f, headerRect.height);
                Rect resolveRect = new Rect(headerRect.xMax - resolveWidth, headerRect.y, resolveWidth, headerRect.height);
                GUI.Label(titleRect, $"Group {groupIndex + 1} ({group.Count} materials)", EditorStyles.boldLabel);

                EditorGUI.BeginDisabledGroup(group.Count < 2);
                if (GUI.Button(resolveRect, "Resolve"))
                {
                    ResolveGroup(groupIndex);
                    return;
                }
                EditorGUI.EndDisabledGroup();

                if (!_reorderableListsByGroupIndex.TryGetValue(groupIndex, out ReorderableList reorderableList))
                {
                    int capturedGroupIndex = groupIndex;
                    reorderableList = new ReorderableList(group, typeof(Material), true, false, false, false)
                    {
                        drawElementCallback = (Rect rect, int index, bool active, bool focused) =>
                        {
                            Material mat = group[index];
                            float pingWidth = 40f;
                            Rect matRect = new Rect(rect.x, rect.y, rect.width - pingWidth - 4f, rect.height);
                            Rect pingRect = new Rect(rect.xMax - pingWidth, rect.y, pingWidth, rect.height);
                            EditorGUI.ObjectField(matRect, mat, typeof(Material), false);
                            if (GUI.Button(pingRect, "Ping"))
                                PingFirstGameObjectWithMaterial(mat);
                        }
                    };
                    _reorderableListsByGroupIndex[capturedGroupIndex] = reorderableList;
                }

                reorderableList.DoList(EditorGUILayout.GetControlRect(false, reorderableList.GetHeight()));
            }
        }

        private void ResolveGroup(int groupIndex)
        {
            if (groupIndex < 0 || groupIndex >= _duplicateGroups.Count)
                return;

            List<Material> group = _duplicateGroups[groupIndex];
            if (group == null || group.Count < 2)
                return;

            Material materialToKeep = group[0];
            for (int i = 1; i < group.Count; i++)
            {
                Material materialToRemove = group[i];
                ReplaceMaterialInTargetHierarchy(materialToRemove, materialToKeep);
                DeleteMaterialAssetIfProjectAsset(materialToRemove);
            }

            _duplicateGroups.RemoveAt(groupIndex);
            _reorderableListsByGroupIndex.Clear();
            EditorUtility.SetDirty(this);
            AssetDatabase.SaveAssets();
            AssetDatabase.Refresh();
        }

        private void PingFirstGameObjectWithMaterial(Material material)
        {
            if (material == null || TargetPrefabs == null)
                return;
            GameObject first = FindFirstGameObjectWithMaterial(material);
            if (first != null)
            {
                Selection.activeGameObject = first;
                EditorGUIUtility.PingObject(first);
                FocusSceneViewOn(first);
            }
        }

        private static void FocusSceneViewOn(GameObject go)
        {
            if (go == null)
                return;
            SceneView sceneView = SceneView.lastActiveSceneView;
            if (sceneView != null)
            {
                sceneView.Focus();
                sceneView.FrameSelected();
            }
        }

        private GameObject FindFirstGameObjectWithMaterial(Material material)
        {
            if (TargetPrefabs == null || material == null)
                return null;
            for (int i = 0; i < TargetPrefabs.Count; i++)
            {
                GameObject root = TargetPrefabs[i];
                if (root == null)
                    continue;
                GameObject found = FindFirstGameObjectWithMaterialInHierarchy(root.transform, material);
                if (found != null)
                    return found;
            }
            return null;
        }

        private static GameObject FindFirstGameObjectWithMaterialInHierarchy(Transform root, Material material)
        {
            MeshRenderer[] meshRenderers = root.GetComponentsInChildren<MeshRenderer>(true);
            foreach (MeshRenderer mr in meshRenderers)
            {
                if (RendererUsesMaterial(mr, material))
                    return mr.gameObject;
            }
            SkinnedMeshRenderer[] skinnedRenderers = root.GetComponentsInChildren<SkinnedMeshRenderer>(true);
            foreach (SkinnedMeshRenderer sr in skinnedRenderers)
            {
                if (RendererUsesMaterial(sr, material))
                    return sr.gameObject;
            }
            return null;
        }

        private static bool RendererUsesMaterial(Renderer renderer, Material material)
        {
            Material[] shared = renderer.sharedMaterials;
            for (int i = 0; i < shared.Length; i++)
            {
                if (shared[i] == material)
                    return true;
            }
            return false;
        }

        private void ReplaceMaterialInTargetHierarchy(Material oldMaterial, Material newMaterial)
        {
            if (TargetPrefabs == null)
                return;
            for (int i = 0; i < TargetPrefabs.Count; i++)
            {
                GameObject root = TargetPrefabs[i];
                if (root == null)
                    continue;
                ReplaceMaterialOnRenderersInHierarchy(root.transform, oldMaterial, newMaterial);
            }
        }

        private static void ReplaceMaterialOnRenderersInHierarchy(Transform root, Material oldMaterial, Material newMaterial)
        {
            MeshRenderer[] meshRenderers = root.GetComponentsInChildren<MeshRenderer>(true);
            foreach (MeshRenderer mr in meshRenderers)
                ReplaceMaterialOnRenderer(mr, oldMaterial, newMaterial);
            SkinnedMeshRenderer[] skinnedRenderers = root.GetComponentsInChildren<SkinnedMeshRenderer>(true);
            foreach (SkinnedMeshRenderer sr in skinnedRenderers)
                ReplaceMaterialOnRenderer(sr, oldMaterial, newMaterial);
        }

        private static void ReplaceMaterialOnRenderer(Renderer renderer, Material oldMaterial, Material newMaterial)
        {
            Material[] shared = renderer.sharedMaterials;
            bool changed = false;
            for (int i = 0; i < shared.Length; i++)
            {
                if (shared[i] == oldMaterial)
                {
                    shared[i] = newMaterial;
                    changed = true;
                }
            }
            if (changed)
            {
                Undo.RecordObject(renderer, "Deduplicate Material");
                renderer.sharedMaterials = shared;
                EditorUtility.SetDirty(renderer);
            }
        }

        private static void DeleteMaterialAssetIfProjectAsset(Material material)
        {
            if (material == null)
                return;
            string path = AssetDatabase.GetAssetPath(material);
            if (string.IsNullOrEmpty(path) || !path.StartsWith("Assets/"))
                return;
            AssetDatabase.DeleteAsset(path);
        }

        private static bool HasTextMeshProComponent(GameObject go)
        {
            if (go == null)
                return false;
            return go.GetComponent<TextMeshProUGUI>() != null || go.GetComponent<TextMeshPro>() != null;
        }
    }
}
