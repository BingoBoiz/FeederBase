using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using Sirenix.OdinInspector;
using UnityEditor;
using UnityEngine;
using Object = UnityEngine.Object;

namespace Feeder
{
    public sealed class FDataFillerTool : FTargetScriptableObjectToolBase
    {
        [Serializable]
        private sealed class MatchPreviewRow
        {
            [TableColumnWidth(200, Resizable = true)]
            [ReadOnly]
            public string EnumName;

            [TableColumnWidth(80, Resizable = false)]
            [ReadOnly]
            [SuffixLabel("%")]
            [DisplayAsString]
            public string Score;

            [AssetSelector(Paths = "Assets")]
            public Sprite Sprite;
        }

        private enum MatchStatus { Matched, FallbackDefault, Skipped }

        private sealed class EnumMatchResult
        {
            public object EnumValue;
            public string EnumName;
            public Sprite AssignedSprite;
            public float BestScore;
            public MatchStatus Status;
        }

        protected override string GetDescription()
        {
            return "Tự điền Dictionary<Enum, Sprite> trong ScriptableObject bằng cách khớp mờ tên enum với sprite trong TargetAssets. " +
                   "Asset Default (optional) dùng cho enum không khớp. Preview Match xem bảng map trước; Fill Dictionary ghi vào field đã chọn.";
        }

        [PropertyOrder(-910)]
        [AssetSelector(Paths = "Assets")]
        [ShowInInspector]
        public new ScriptableObject TargetSO
        {
            get => base.TargetSO;
            set => base.TargetSO = value;
        }

        [PropertyOrder(-900)]
        [ListDrawerSettings(ShowFoldout = true, DraggableItems = true, ShowIndexLabels = true, NumberOfItemsPerPage = 10)]
        [OnValueChanged(nameof(HandleTargetAssetsChanged))]
        [ShowInInspector]
        public List<Object> TargetAssets
        {
            get => GetDataContainer().TargetAssets;
            set
            {
                FDataContainer container = GetDataContainer();
                container.TargetAssets.Clear();
                if (value != null)
                    container.TargetAssets.AddRange(value);
                FDataPersistenceService.SaveData(container);
            }
        }

        [PropertyOrder(-880)]
        [PropertySpace(SpaceBefore = 8)]
        [LabelText("Dictionary Field")]
        [ValueDropdown(nameof(GetDictionaryFields))]
        [ShowInInspector]
        public string SelectedFieldName;

        [PropertyOrder(-870)]
        [PropertySpace(SpaceBefore = 6)]
        [LabelText("Match Threshold (0–1)")]
        [PropertyRange(0f, 1f)]
        [ShowInInspector]
        private float _matchThreshold
        {
            get => FToolPrefs.GetFloat(nameof(FDataFillerTool), nameof(_matchThreshold), 0.8f);
            set => FToolPrefs.SetFloat(nameof(FDataFillerTool), nameof(_matchThreshold), value);
        }

        [PropertyOrder(-860)]
        [PropertySpace(SpaceBefore = 6)]
        [LabelText("Override Existing Values")]
        [Tooltip("If true, will override existing sprite values. If false, will skip enum values that already have sprites.")]
        [ShowInInspector]
        public bool OverrideExistingValues
        {
            get => FToolPrefs.GetBool(nameof(FDataFillerTool), nameof(OverrideExistingValues), true);
            set => FToolPrefs.SetBool(nameof(FDataFillerTool), nameof(OverrideExistingValues), value);
        }

        [PropertyOrder(-855)]
        [PropertySpace(SpaceBefore = 6)]
        [LabelText("Asset Default (Optional)")]
        [AssetSelector(Paths = "Assets")]
        [ShowInInspector]
        public Sprite AssetDefault;

        [PropertyOrder(-850)]
        [OnInspectorGUI]
        private void DrawGuide()
        {
            GUILayout.Space(2);
            FStylesUtils.DrawInfoBox(
                "Target SO         ScriptableObject chứa Dictionary<Enum, Sprite>\n" +
                "Target Assets     kéo sprite cần khớp vào đây\n" +
                "Match Threshold   ngưỡng độ khớp tối thiểu (0–1), thường để 0.8–0.9\n" +
                "Override          ghi đè sprite đã có hay bỏ qua\n" +
                "Asset Default     sprite dự phòng cho enum không khớp (để trống = null)\n" +
                "Preview Match     xem bảng enum → sprite kèm % khớp (null = thiếu)\n" +
                "Fill Dictionary   ghi kết quả khớp vào field đã chọn"
            );
            GUILayout.Space(4);
        }

        [PropertyOrder(50)]
        [PropertySpace(SpaceBefore = 10)]
        [ButtonGroup("FillActions")]
        [Button("Preview Match", ButtonSizes.Medium)]
        private void PreviewMatch()
        {
            if (!TryResolveDictionaryField(out FieldInfo fieldInfo, out Type dictionaryType, out Type keyType))
                return;

            List<Sprite> sprites = CollectSpritesFromTargetAssets();
            object dictionary = fieldInfo.GetValue(TargetSO);
            List<EnumMatchResult> matches = ComputeMatches(keyType, sprites, dictionary, dictionaryType);

            _previewRows = matches.Select(m => new MatchPreviewRow
            {
                EnumName = m.EnumName,
                Score    = FormatMatchScore(m),
                Sprite   = m.AssignedSprite,
            }).ToList();
        }

        [PropertyOrder(50)]
        [ButtonGroup("FillActions")]
        [Button("Fill Dictionary", ButtonSizes.Medium)]
        [GUIColor(0.3f, 0.8f, 1f)]
        public void FillDictionary()
        {
            if (!TryResolveDictionaryField(out FieldInfo fieldInfo, out Type dictionaryType, out Type keyType))
                return;

            List<Sprite> sprites = CollectSpritesFromTargetAssets();

            Undo.RegisterCompleteObjectUndo(TargetSO, "Fill Dictionary");
            object dictionary = fieldInfo.GetValue(TargetSO);
            if (dictionary == null)
            {
                dictionary = Activator.CreateInstance(dictionaryType);
                fieldInfo.SetValue(TargetSO, dictionary);
            }

            List<EnumMatchResult> matches = ComputeMatches(keyType, sprites, dictionary, dictionaryType);
            MethodInfo setItem = dictionaryType.GetMethod("set_Item");

            foreach (EnumMatchResult match in matches)
            {
                if (match.Status == MatchStatus.Skipped)
                {
                    Debug.Log($"<color=yellow>Skipping '{match.EnumValue}' — already has sprite (override disabled)</color>");
                    continue;
                }

                setItem.Invoke(dictionary, new[] { match.EnumValue, match.AssignedSprite });
                LogMatchResult(match);
            }

            EditorUtility.SetDirty(TargetSO);
            AssetDatabase.SaveAssets();
            Debug.Log("Dictionary filled successfully!");
        }

        [PropertyOrder(100)]
        [PropertySpace(SpaceBefore = 10)]
        [ShowIf(nameof(HasPreviewRows))]
        [TableList(ShowIndexLabels = true, IsReadOnly = false, NumberOfItemsPerPage = 15, AlwaysExpanded = true, ShowPaging = true)]
        [LabelText("Enum → Sprite preview")]
        [SerializeField]
        private List<MatchPreviewRow> _previewRows = new List<MatchPreviewRow>();

        private bool HasPreviewRows => _previewRows?.Count > 0;

        private void HandleTargetAssetsChanged()
        {
            FDataPersistenceService.SaveData(GetDataContainer());
        }

        private bool TryResolveDictionaryField(out FieldInfo fieldInfo, out Type dictionaryType, out Type keyType)
        {
            fieldInfo = null;
            dictionaryType = null;
            keyType = null;

            if (TargetSO == null || string.IsNullOrEmpty(SelectedFieldName))
            {
                Debug.LogError("Target ScriptableObject or field not set!");
                return false;
            }

            fieldInfo = TargetSO.GetType()
                .GetField(SelectedFieldName, BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
            if (fieldInfo == null)
            {
                Debug.LogError("Field not found!");
                return false;
            }

            dictionaryType = fieldInfo.FieldType;
            if (!dictionaryType.IsGenericType || dictionaryType.GetGenericTypeDefinition() != typeof(Dictionary<,>))
            {
                Debug.LogError("Selected field is not a Dictionary!");
                return false;
            }

            keyType = dictionaryType.GetGenericArguments()[0];
            Type valueType = dictionaryType.GetGenericArguments()[1];
            if (!keyType.IsEnum || valueType != typeof(Sprite))
            {
                Debug.LogError("Dictionary must be Dictionary<Enum, Sprite>!");
                return false;
            }

            return true;
        }

        // Supports both direct Sprite assets and Texture2D parent assets (loads all Sprite sub-assets).
        private List<Sprite> CollectSpritesFromTargetAssets()
        {
            if (TargetAssets == null)
                throw new InvalidOperationException("TargetAssets is null.");

            List<Sprite> sprites = new List<Sprite>(TargetAssets.Count);
            for (int i = 0; i < TargetAssets.Count; i++)
            {
                Object asset = TargetAssets[i];
                if (asset == null) continue;

                if (asset is Sprite sprite)
                {
                    sprites.Add(sprite);
                    continue;
                }

                if (asset is Texture2D)
                {
                    string path = AssetDatabase.GetAssetPath(asset);
                    Object[] subAssets = AssetDatabase.LoadAllAssetsAtPath(path);
                    foreach (Object sub in subAssets)
                    {
                        if (sub is Sprite subSprite)
                            sprites.Add(subSprite);
                    }
                }
            }

            if (sprites.Count == 0)
                Debug.LogWarning("No sprites found in TargetAssets. Make sure you added Sprite or Texture2D assets.");

            return sprites;
        }

        // Globally-optimal greedy assignment:
        // 1. Score all (enum, sprite) pairs
        // 2. Sort descending by score
        // 3. Assign from highest score — skip if enum or sprite already taken
        // This ensures the best match globally wins regardless of enum definition order.
        private List<EnumMatchResult> ComputeMatches(Type keyType, List<Sprite> sprites, object dictionary, Type dictionaryType)
        {
            Array enumValues = Enum.GetValues(keyType);
            int enumCount  = enumValues.Length;
            int spriteCount = sprites.Count;

            string[] normalizedEnums = new string[enumCount];
            for (int i = 0; i < enumCount; i++)
                normalizedEnums[i] = FuzzyMatchUtils.Normalize(enumValues.GetValue(i).ToString());

            string[] normalizedSprites = new string[spriteCount];
            for (int i = 0; i < spriteCount; i++)
                normalizedSprites[i] = sprites[i] != null ? FuzzyMatchUtils.Normalize(sprites[i].name) : null;

            // Determine which enums to skip (override disabled + already has a non-null sprite)
            Sprite[] skipSprites = new Sprite[enumCount];
            if (!OverrideExistingValues && dictionary != null)
            {
                MethodInfo containsKey = dictionaryType.GetMethod("ContainsKey");
                MethodInfo getItem     = dictionaryType.GetMethod("get_Item");
                for (int i = 0; i < enumCount; i++)
                {
                    object enumValue = enumValues.GetValue(i);
                    bool hasKey = (bool)containsKey.Invoke(dictionary, new[] { enumValue });
                    if (!hasKey) continue;
                    Sprite existing = (Sprite)getItem.Invoke(dictionary, new[] { enumValue });
                    if (existing != null)
                        skipSprites[i] = existing;
                }
            }

            // Build all (enumIdx, spriteIdx, score) pairs for non-skipped enums
            var candidates = new List<(int enumIdx, int spriteIdx, float score)>();
            for (int e = 0; e < enumCount; e++)
            {
                if (skipSprites[e] != null) continue;
                for (int s = 0; s < spriteCount; s++)
                {
                    if (normalizedSprites[s] == null) continue;
                    float score = FuzzyMatchUtils.Similarity(normalizedEnums[e], normalizedSprites[s]);
                    candidates.Add((e, s, score));
                }
            }

            // Track best raw score per enum for display on fallback rows
            float[] bestRawScores = new float[enumCount];
            foreach ((int e, int s, float score) in candidates)
            {
                if (score > bestRawScores[e])
                    bestRawScores[e] = score;
            }

            // Sort descending — highest-confidence pairs assigned first
            candidates.Sort((a, b) => b.score.CompareTo(a.score));

            var assignedEnums   = new HashSet<int>();
            var assignedSprites = new HashSet<int>();
            var assignments     = new Dictionary<int, (int spriteIdx, float score)>();

            foreach ((int e, int s, float score) in candidates)
            {
                if (score < _matchThreshold) break;
                if (assignedEnums.Contains(e) || assignedSprites.Contains(s)) continue;

                assignments[e] = (s, score);
                assignedEnums.Add(e);
                assignedSprites.Add(s);
            }

            // Build result in enum definition order
            var results = new List<EnumMatchResult>(enumCount);
            for (int i = 0; i < enumCount; i++)
            {
                object enumValue = enumValues.GetValue(i);
                string enumName  = enumValue.ToString();

                if (skipSprites[i] != null)
                {
                    results.Add(new EnumMatchResult
                    {
                        EnumValue      = enumValue,
                        EnumName       = enumName,
                        AssignedSprite = skipSprites[i],
                        Status         = MatchStatus.Skipped,
                    });
                    continue;
                }

                if (assignments.TryGetValue(i, out (int spriteIdx, float score) match))
                {
                    results.Add(new EnumMatchResult
                    {
                        EnumValue      = enumValue,
                        EnumName       = enumName,
                        AssignedSprite = sprites[match.spriteIdx],
                        BestScore      = match.score,
                        Status         = MatchStatus.Matched,
                    });
                    continue;
                }

                results.Add(new EnumMatchResult
                {
                    EnumValue      = enumValue,
                    EnumName       = enumName,
                    AssignedSprite = AssetDefault,
                    BestScore      = bestRawScores[i],
                    Status         = MatchStatus.FallbackDefault,
                });
            }

            return results;
        }

        private string FormatMatchScore(EnumMatchResult match)
        {
            switch (match.Status)
            {
                case MatchStatus.Skipped:         return "skip";
                case MatchStatus.Matched:          return $"{match.BestScore * 100f:F1}";
                case MatchStatus.FallbackDefault:
                    if (AssetDefault != null)      return "default";
                    return match.BestScore > 0f ? $"{match.BestScore * 100f:F1}" : "—";
                default:                           return "—";
            }
        }

        private void LogMatchResult(EnumMatchResult match)
        {
            switch (match.Status)
            {
                case MatchStatus.Matched:
                    Debug.Log($"<color=cyan>Match: '{match.EnumValue}' → '{match.AssignedSprite.name}' ({match.BestScore * 100f:F1}%)</color>");
                    break;
                case MatchStatus.FallbackDefault when AssetDefault != null:
                    Debug.Log($"<color=orange>Default: '{match.EnumValue}' → '{AssetDefault.name}' (best: {match.BestScore * 100f:F1}%)</color>");
                    break;
                default:
                    Debug.Log($"<color=red>No match for '{match.EnumValue}' (best: {match.BestScore * 100f:F1}%)</color>");
                    break;
            }
        }

        private IEnumerable<string> GetDictionaryFields()
        {
            if (TargetSO == null)
                return Enumerable.Empty<string>();

            return TargetSO.GetType()
                .GetFields(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance)
                .Where(f => f.FieldType.IsGenericType &&
                            f.FieldType.GetGenericTypeDefinition() == typeof(Dictionary<,>) &&
                            f.FieldType.GetGenericArguments()[0].IsEnum &&
                            f.FieldType.GetGenericArguments()[1] == typeof(Sprite))
                .Select(f => f.Name);
        }
    }
}
