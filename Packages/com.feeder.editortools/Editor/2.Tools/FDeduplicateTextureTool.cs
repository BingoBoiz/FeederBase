using System;
using System.Collections.Generic;
using System.Security.Cryptography;
using Sirenix.OdinInspector;
using Sirenix.Serialization;
using TMPro;
using UnityEditor;
using UnityEditorInternal;
using UnityEngine;

namespace Feeder
{
    public sealed class FDeduplicateTextureTool : FTargetPrefabsToolBase
    {
        private const int TexturePreviewSize = 96;

        // Base Map property names: URP = _BaseMap, Built-in = _MainTex
        private static readonly string[] BaseMapPropertyNames = { "_BaseMap", "_MainTex" };

        private List<Texture2D> _allCollectedTextures = new List<Texture2D>();

        private List<SimilarGroup> _similarGroups;
        private Dictionary<int, ReorderableList> _reorderableListsByGroupIndex = new Dictionary<int, ReorderableList>();

        protected override string GetDescription()
        {
            return "Quét TargetPrefabs tìm texture giống nhau (cùng độ phân giải + pixel) rồi gộp lại. Chỉ quét Base Map (_BaseMap / _MainTex) của từng material.";
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
                "TargetPrefabs    root chứa MeshFilter + MeshRenderer\n" +
                "chỉ quét slot _BaseMap / _MainTex của mỗi material\n" +
                "hai texture bị coi là giống nếu cùng resolution và cùng pixel\n" +
                "Resolve          chọn texture gốc, các ref còn lại trỏ về texture đó"
            );
            GUILayout.Space(4);
        }

        [Button(ButtonSizes.Large), GUIColor(0.3f, 0.8f, 1f)]
        public void FindSimilarTexture()
        {
            if (TargetPrefabs == null || TargetPrefabs.Count == 0)
            {
                Debug.LogWarning("[FDeduplicateTextureTool] Add at least one TargetObject.");
                _similarGroups = null;
                _reorderableListsByGroupIndex.Clear();
                return;
            }

            Dictionary<Texture2D, List<MaterialTextureSlot>> textureToSlots = CollectTextureSlotsFromTargetPrefabs();

            _allCollectedTextures.Clear();
            if (textureToSlots != null)
            {
                foreach (Texture2D tex in textureToSlots.Keys)
                {
                    if (tex != null)
                        _allCollectedTextures.Add(tex);
                }
            }

            _similarGroups = ComputeSimilarTextureGroupsWithSlots(textureToSlots);
            _reorderableListsByGroupIndex.Clear();
            int uniqueCount = _allCollectedTextures.Count;
            int groupCount = _similarGroups?.Count ?? 0;
            Debug.Log($"<color=green>[FDeduplicateTextureTool] Collected {uniqueCount} unique texture(s), found {groupCount} similar group(s).</color>");
        }

        private Dictionary<Texture2D, List<MaterialTextureSlot>> CollectTextureSlotsFromTargetPrefabs()
        {
            Dictionary<Texture2D, List<MaterialTextureSlot>> textureToSlots = new Dictionary<Texture2D, List<MaterialTextureSlot>>();

            for (int i = 0; i < TargetPrefabs.Count; i++)
            {
                GameObject target = TargetPrefabs[i];
                if (target == null)
                {
                    Debug.LogWarning($"[FDeduplicateTextureTool] Skipping null at TargetPrefabs[{i}].");
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
                        if (mat == null)
                            continue;
                        Texture2D baseMapTex = GetBaseMapTexture(mat, out string propertyName);
                        if (baseMapTex == null)
                            continue;
                        if (!textureToSlots.TryGetValue(baseMapTex, out List<MaterialTextureSlot> slots))
                        {
                            slots = new List<MaterialTextureSlot>();
                            textureToSlots.Add(baseMapTex, slots);
                        }
                        slots.Add(new MaterialTextureSlot { Material = mat, PropertyName = propertyName });
                    }
                }
            }

            return textureToSlots;
        }

        private static Texture2D GetBaseMapTexture(Material material, out string usedPropertyName)
        {
            usedPropertyName = null;
            if (material == null)
                return null;
            foreach (string propName in BaseMapPropertyNames)
            {
                if (!material.HasProperty(propName))
                    continue;
                Texture tex = material.GetTexture(propName);
                if (tex is Texture2D tex2d)
                {
                    usedPropertyName = propName;
                    return tex2d;
                }
            }
            return null;
        }

        private static bool HasTextMeshProComponent(GameObject go)
        {
            if (go == null)
                return false;
            return go.GetComponent<TextMeshProUGUI>() != null || go.GetComponent<TextMeshPro>() != null;
        }

        private List<SimilarGroup> ComputeSimilarTextureGroupsWithSlots(Dictionary<Texture2D, List<MaterialTextureSlot>> textureToSlots)
        {
            if (textureToSlots == null || textureToSlots.Count == 0)
                return new List<SimilarGroup>();

            List<Texture2D> textures = new List<Texture2D>(textureToSlots.Keys);
            Dictionary<(int w, int h), List<Texture2D>> byResolution = new Dictionary<(int w, int h), List<Texture2D>>();

            foreach (Texture2D tex in textures)
            {
                if (tex == null)
                    continue;
                (int w, int h) key = (tex.width, tex.height);
                if (!byResolution.TryGetValue(key, out List<Texture2D> list))
                {
                    list = new List<Texture2D>();
                    byResolution.Add(key, list);
                }
                list.Add(tex);
            }

            List<SimilarGroup> result = new List<SimilarGroup>();
            foreach (KeyValuePair<(int w, int h), List<Texture2D>> kvp in byResolution)
            {
                if (kvp.Value.Count < 2)
                    continue;

                Dictionary<string, List<Texture2D>> byHash = new Dictionary<string, List<Texture2D>>();
                foreach (Texture2D tex in kvp.Value)
                {
                    if (tex == null)
                        continue;
                    string hash = ComputePixelHash(tex);
                    if (hash == null)
                        continue;
                    if (!byHash.TryGetValue(hash, out List<Texture2D> group))
                    {
                        group = new List<Texture2D>();
                        byHash.Add(hash, group);
                    }
                    group.Add(tex);
                }

                foreach (List<Texture2D> group in byHash.Values)
                {
                    if (group.Count < 2)
                        continue;
                    SimilarGroup similarGroup = new SimilarGroup();
                    foreach (Texture2D tex in group)
                    {
                        textureToSlots.TryGetValue(tex, out List<MaterialTextureSlot> slots);
                        similarGroup.Rows.Add(new GroupRow { Texture = tex, Slots = slots ?? new List<MaterialTextureSlot>() });
                    }
                    result.Add(similarGroup);
                }
            }

            return result;
        }

        private static int CountUniqueMaterialsInRow(GroupRow row)
        {
            if (row?.Slots == null || row.Slots.Count == 0)
                return 0;
            HashSet<Material> seen = new HashSet<Material>();
            foreach (MaterialTextureSlot slot in row.Slots)
            {
                if (slot.Material != null)
                    seen.Add(slot.Material);
            }
            return seen.Count;
        }

        private static void DrawUniqueMaterialsFromSlots(Rect matRect, List<MaterialTextureSlot> slots)
        {
            if (slots == null || slots.Count == 0)
                return;
            HashSet<Material> drawn = new HashSet<Material>();
            float y = matRect.y;
            foreach (MaterialTextureSlot slot in slots)
            {
                if (slot.Material == null || drawn.Contains(slot.Material))
                    continue;
                drawn.Add(slot.Material);
                Rect lineRect = new Rect(matRect.x, y, matRect.width, EditorGUIUtility.singleLineHeight);
                EditorGUI.ObjectField(lineRect, slot.Material, typeof(Material), false);
                y += EditorGUIUtility.singleLineHeight + 2f;
            }
        }

        [PropertyOrder(100)]
        [OnInspectorGUI]
        private void DrawSimilarGroups()
        {
            if (_similarGroups == null || _similarGroups.Count == 0)
            {
                EditorGUILayout.HelpBox("Add TargetPrefabs (roots with MeshRenderers), then click FindSimilarTexture.", MessageType.Info);
                return;
            }

            for (int groupIndex = 0; groupIndex < _similarGroups.Count; groupIndex++)
            {
                SimilarGroup group = _similarGroups[groupIndex];
                if (group == null || group.Rows == null || group.Rows.Count == 0)
                    continue;

                EditorGUILayout.Space(8f);

                Rect headerRect = EditorGUILayout.GetControlRect(false, EditorGUIUtility.singleLineHeight);
                float resolveWidth = 70f;
                Rect titleRect = new Rect(headerRect.x, headerRect.y, headerRect.width - resolveWidth - 4f, headerRect.height);
                Rect resolveRect = new Rect(headerRect.xMax - resolveWidth, headerRect.y, resolveWidth, headerRect.height);

                GUI.Label(titleRect, $"Group {groupIndex + 1} ({group.Rows.Count} textures)", EditorStyles.boldLabel);

                EditorGUI.BeginDisabledGroup(group.Rows.Count < 2);
                if (GUI.Button(resolveRect, "Resolve"))
                {
                    ResolveGroup(groupIndex);
                    return;
                }
                EditorGUI.EndDisabledGroup();

                if (!_reorderableListsByGroupIndex.TryGetValue(groupIndex, out ReorderableList reorderableList))
                {
                    reorderableList = new ReorderableList(group.Rows, typeof(GroupRow), true, true, true, true)
                    {
                        drawHeaderCallback = (Rect r) =>
                        {
                            float colTex = TexturePreviewSize + 8f;
                            GUI.Label(new Rect(r.x, r.y, colTex, r.height), "Texture");
                            GUI.Label(new Rect(r.x + colTex, r.y, r.width - colTex, r.height), "Materials (unique)");
                        },
                        elementHeightCallback = (int index) =>
                        {
                            float texBlock = TexturePreviewSize + 8f + EditorGUIUtility.singleLineHeight + 4f;
                            int uniqueMatCount = CountUniqueMaterialsInRow(group.Rows[index]);
                            float matBlock = uniqueMatCount * (EditorGUIUtility.singleLineHeight + 2f) + 8f;
                            return Mathf.Max(texBlock, matBlock);
                        },
                        drawElementCallback = (Rect rect, int index, bool active, bool focused) =>
                        {
                            GroupRow row = group.Rows[index];
                            float colTex = TexturePreviewSize + 8f;
                            Rect texRect = new Rect(rect.x + 4f, rect.y + 4f, TexturePreviewSize, TexturePreviewSize);
                            Rect objFieldRect = new Rect(texRect.x, texRect.yMax + 2f, texRect.width, EditorGUIUtility.singleLineHeight);
                            Rect matRect = new Rect(rect.x + colTex, rect.y + 4f, rect.width - colTex - 4f, rect.height - 8f);

                            if (row.Texture != null)
                            {
                                EditorGUI.DrawPreviewTexture(texRect, row.Texture, null, ScaleMode.ScaleToFit);
                                EditorGUI.DrawRect(texRect, new Color(0.5f, 0.5f, 0.5f, 0.3f));
                            }
                            else
                            {
                                EditorGUI.DrawRect(texRect, new Color(0.2f, 0.2f, 0.2f));
                                GUI.Label(texRect, "Missing", EditorStyles.centeredGreyMiniLabel);
                            }

                            Texture2D newTex = (Texture2D)EditorGUI.ObjectField(objFieldRect, row.Texture, typeof(Texture2D), false);
                            if (newTex != row.Texture)
                                row.Texture = newTex;

                            DrawUniqueMaterialsFromSlots(matRect, row.Slots);
                        }
                    };
                    _reorderableListsByGroupIndex[groupIndex] = reorderableList;
                }

                reorderableList.DoList(EditorGUILayout.GetControlRect(false, reorderableList.GetHeight()));
            }
        }

        private void ResolveGroup(int groupIndex)
        {
            if (_similarGroups == null || groupIndex < 0 || groupIndex >= _similarGroups.Count)
                return;

            SimilarGroup group = _similarGroups[groupIndex];
            if (group?.Rows == null || group.Rows.Count < 2)
                return;

            GroupRow first = group.Rows[0];
            Texture2D keepTexture = first.Texture;
            if (keepTexture == null)
            {
                Debug.LogWarning("[FDeduplicateTextureTool] First row has no texture; resolve skipped.");
                return;
            }

            string keepPath = AssetDatabase.GetAssetPath(keepTexture);
            HashSet<string> pathsToDelete = new HashSet<string>();

            for (int i = 1; i < group.Rows.Count; i++)
            {
                GroupRow row = group.Rows[i];
                if (row.Slots != null)
                {
                    foreach (MaterialTextureSlot slot in row.Slots)
                    {
                        if (slot.Material == null)
                            continue;
                        Undo.RecordObject(slot.Material, "Deduplicate Texture");
                        slot.Material.SetTexture(slot.PropertyName, keepTexture);
                        EditorUtility.SetDirty(slot.Material);
                    }
                }

                if (row.Texture != null)
                {
                    string path = AssetDatabase.GetAssetPath(row.Texture);
                    if (!string.IsNullOrEmpty(path) && path.StartsWith("Assets/") && path != keepPath)
                        pathsToDelete.Add(path);
                }
            }

            foreach (string path in pathsToDelete)
                AssetDatabase.DeleteAsset(path);

            _similarGroups.RemoveAt(groupIndex);
            _reorderableListsByGroupIndex.Remove(groupIndex);
            _reorderableListsByGroupIndex.Clear();
            AssetDatabase.SaveAssets();
            AssetDatabase.Refresh();
        }

        private static string ComputePixelHash(Texture2D texture)
        {
            if (texture == null || texture.width <= 0 || texture.height <= 0)
                return null;

            int w = texture.width;
            int h = texture.height;
            RenderTexture rt = RenderTexture.GetTemporary(w, h, 0, RenderTextureFormat.ARGB32, RenderTextureReadWrite.Default);
            try
            {
                RenderTexture prev = RenderTexture.active;
                Graphics.Blit(texture, rt);
                RenderTexture.active = rt;
                try
                {
                    Texture2D temp = new Texture2D(w, h, TextureFormat.RGBA32, false);
                    try
                    {
                        temp.ReadPixels(new Rect(0, 0, w, h), 0, 0);
                        temp.Apply();
                        byte[] raw = temp.GetRawTextureData();
                        return raw?.Length > 0 ? HashBytes(raw) : null;
                    }
                    finally
                    {
                        DestroyImmediate(temp);
                    }
                }
                finally
                {
                    RenderTexture.active = prev;
                }
            }
            finally
            {
                RenderTexture.ReleaseTemporary(rt);
            }
        }

        private static string HashBytes(byte[] data)
        {
            if (data == null || data.Length == 0)
                return string.Empty;
            byte[] hash = MD5.Create().ComputeHash(data);
            return BitConverter.ToString(hash).Replace("-", "");
        }

        [Serializable]
        private sealed class SimilarGroup
        {
            public List<GroupRow> Rows = new List<GroupRow>();
        }

        [Serializable]
        private sealed class GroupRow
        {
            public Texture2D Texture;
            public List<MaterialTextureSlot> Slots = new List<MaterialTextureSlot>();
        }

        [Serializable]
        private sealed class MaterialTextureSlot
        {
            public Material Material;
            public string PropertyName;
        }
    }
}
