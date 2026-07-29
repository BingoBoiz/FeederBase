using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using Sirenix.OdinInspector;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using Object = UnityEngine.Object;

namespace Feeder
{
    public sealed class FDataFillerTool : FTargetAssetsToolBase
    {
        [Serializable]
        private sealed class MatchPreviewRow
        {
            [TableColumnWidth(200, Resizable = true)]
            [ReadOnly]
            public string Key;

            [TableColumnWidth(80, Resizable = false)]
            [ReadOnly]
            [DisplayAsString]
            public string Score;

            public Object Asset;
        }

        private enum MatchStatus { Matched, FallbackDefault, Skipped }

        private enum FieldKind { EnumDict, StringDict, ListLike, Array, HashSet }

        private sealed class FillTargetInfo
        {
            public FieldInfo Field;
            public FieldKind Kind;
            public Type KeyType;      // enum type for EnumDict, string for StringDict, else null
            public Type ElementType;  // UnityEngine.Object subclass being filled

            public bool IsDict => Kind == FieldKind.EnumDict || Kind == FieldKind.StringDict;
        }

        private sealed class KeyMatchResult
        {
            public object Key;
            public string KeyName;
            public Object AssignedAsset;
            public float BestScore;
            public MatchStatus Status;
        }

        protected override string GetDescription()
        {
            return "Tự điền field chứa nhiều asset (Dictionary<Enum,T>, Dictionary<string,T>, List<T>, T[], HashSet<T>) " +
                   "trên ScriptableObject / Component bằng asset trong Target Assets. Dictionary khớp mờ tên key với tên asset; " +
                   "List/Array/HashSet nhận toàn bộ asset hợp kiểu. Field chứa Component thì kéo prefab vào Target Assets — " +
                   "tool tự lấy component ở prefab root. Preview Match xem trước; Fill Field ghi vào field đã chọn.";
        }

        // ── Target: SO, GameObject (chọn Component bên trong), hoặc Component kéo trực tiếp ──

        [PropertyOrder(-910)]
        [LabelText("Target")]
        [InlineButton(nameof(UseSelectionAsTarget), "Use Selection")]
        [OnValueChanged(nameof(HandleTargetChanged))]
        [ShowInInspector]
        public Object Target
        {
            get => GetDataContainer().DataFillerTarget;
            set
            {
                FDataContainer container = GetDataContainer();
                container.DataFillerTarget = value;
                FDataPersistenceService.SaveData(container);
            }
        }

        [PropertyOrder(-905)]
        [ShowIf(nameof(TargetIsGameObject))]
        [LabelText("Component")]
        [ValueDropdown(nameof(GetComponentOptions))]
        [OnValueChanged(nameof(HandleComponentChanged))]
        [ShowInInspector]
        public Component TargetComponent
        {
            get => GetDataContainer().DataFillerComponent;
            set
            {
                FDataContainer container = GetDataContainer();
                container.DataFillerComponent = value;
                FDataPersistenceService.SaveData(container);
            }
        }

        private bool TargetIsGameObject => Target is GameObject;

        /// <summary>Object whose field gets filled: the SO/Component itself, or the picked Component of a GameObject.</summary>
        private Object ResolveHost() => Target is GameObject ? TargetComponent : Target;

        private bool HasHost => ResolveHost() != null;

        private IEnumerable<ValueDropdownItem<Component>> GetComponentOptions()
        {
            if (Target is not GameObject go)
                yield break;
            foreach (Component comp in go.GetComponents<Component>())
            {
                if (comp == null) continue; // missing script
                if (EnumerateFillableFields(comp.GetType()).Any())
                    yield return new ValueDropdownItem<Component>(comp.GetType().Name, comp);
            }
        }

        private void UseSelectionAsTarget()
        {
            Object obj = FSelectionUtils.FirstObject();
            if (obj == null)
            {
                Debug.LogWarning("[Feeder] Chưa chọn gì trong Hierarchy/Project.");
                return;
            }
            Target = obj;
            HandleTargetChanged();
        }

        private void HandleTargetChanged()
        {
            TargetComponent = null;
            SelectedFieldName = string.Empty;
            _previewRows.Clear();
        }

        private void HandleComponentChanged()
        {
            SelectedFieldName = string.Empty;
            _previewRows.Clear();
        }

        // ── Field + options ──

        [PropertyOrder(-880)]
        [PropertySpace(SpaceBefore = 8)]
        [ShowIf(nameof(HasHost))]
        [LabelText("Field")]
        [ValueDropdown(nameof(GetFillableFieldOptions))]
        [ShowInInspector]
        public string SelectedFieldName
        {
            get => FToolPrefs.GetString(nameof(FDataFillerTool), nameof(SelectedFieldName), string.Empty);
            set => FToolPrefs.SetString(nameof(FDataFillerTool), nameof(SelectedFieldName), value);
        }

        [PropertyOrder(-870)]
        [PropertySpace(SpaceBefore = 6)]
        [ShowIf(nameof(SelectedFieldIsDict))]
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
        [Tooltip("Dictionary: ghi đè value đã có (off = skip key đã có giá trị). List/Array/HashSet: replace toàn bộ (off = chỉ append asset chưa có).")]
        [ShowInInspector]
        public bool OverrideExistingValues
        {
            get => FToolPrefs.GetBool(nameof(FDataFillerTool), nameof(OverrideExistingValues), true);
            set => FToolPrefs.SetBool(nameof(FDataFillerTool), nameof(OverrideExistingValues), value);
        }

        [PropertyOrder(-855)]
        [PropertySpace(SpaceBefore = 6)]
        [ShowIf(nameof(SelectedFieldIsDict))]
        [LabelText("Asset Default (Optional)")]
        [AssetSelector(Paths = "Assets")]
        [ShowInInspector]
        public Object AssetDefault;

        private bool SelectedFieldIsDict
        {
            get
            {
                FillTargetInfo info = ClassifySelectedSilent();
                return info != null && info.IsDict;
            }
        }

        [PropertyOrder(-850)]
        [OnInspectorGUI]
        private void DrawGuide()
        {
            GUILayout.Space(2);
            FStylesUtils.DrawInfoBox(
                "Target            SO / GameObject (chọn Component) / Component chứa field cần fill\n" +
                "Target Assets     kéo asset nguồn vào đây (Texture2D tự tách sub-sprites khi field chứa Sprite;\n" +
                "                  field chứa Component thì kéo prefab vào — tool tự lấy component ở prefab root)\n" +
                "Field             Dictionary<Enum,T> / Dictionary<string,T> / List<T> / T[] / HashSet<T>\n" +
                "Match Threshold   ngưỡng khớp mờ cho Dictionary (0–1), thường 0.8–0.9\n" +
                "Override          Dictionary: ghi đè value đã có; List/Array/Set: replace thay vì append\n" +
                "Asset Default     asset dự phòng cho key không khớp (Dictionary)\n" +
                "Preview Match     xem bảng key → asset kèm % khớp trước khi ghi\n" +
                "Fill Field        ghi kết quả vào field đã chọn"
            );
            GUILayout.Space(4);
        }

        // ── Actions ──

        [PropertyOrder(50)]
        [PropertySpace(SpaceBefore = 10)]
        [ButtonGroup("FillActions")]
        [Button("Preview Match", ButtonSizes.Medium)]
        private void PreviewMatch()
        {
            if (!TryResolveField(out Object host, out FillTargetInfo info))
                return;

            List<Object> assets = CollectAssetsOfType(info.ElementType);
            List<KeyMatchResult> matches = ComputeMatchesFor(host, info, assets);

            _previewRows = matches.Select(m => new MatchPreviewRow
            {
                Key   = m.KeyName,
                Score = FormatMatchScore(m, info),
                Asset = m.AssignedAsset,
            }).ToList();
        }

        [PropertyOrder(50)]
        [ButtonGroup("FillActions")]
        [Button("Fill Field", ButtonSizes.Medium)]
        [GUIColor(0.3f, 0.8f, 1f)]
        public void FillField()
        {
            if (!TryResolveField(out Object host, out FillTargetInfo info))
                return;

            List<Object> assets = CollectAssetsOfType(info.ElementType);

            Undo.RegisterCompleteObjectUndo(host, "Fill Field");
            List<KeyMatchResult> matches = ComputeMatchesFor(host, info, assets);

            if (info.IsDict)
                FillDictionaryField(host, info, matches);
            else
                FillCollectionField(host, info, matches);

            WarnIfLikelyNotSerialized(host, info);
            SaveHost(host);
            Debug.Log($"[Feeder] Fill '{info.Field.Name}' xong ({matches.Count(m => m.Status != MatchStatus.Skipped)} entries).");
        }

        [PropertyOrder(100)]
        [PropertySpace(SpaceBefore = 10)]
        [ShowIf(nameof(HasPreviewRows))]
        [TableList(ShowIndexLabels = true, IsReadOnly = false, NumberOfItemsPerPage = 15, AlwaysExpanded = true, ShowPaging = true)]
        [LabelText("Key → Asset preview")]
        [SerializeField]
        private List<MatchPreviewRow> _previewRows = new List<MatchPreviewRow>();

        private bool HasPreviewRows => _previewRows?.Count > 0;

        // ── Field classification ──

        private static bool TryClassifyField(FieldInfo field, out FillTargetInfo info)
        {
            info = null;
            Type ft = field.FieldType;

            if (ft.IsArray && ft.GetArrayRank() == 1)
            {
                Type elem = ft.GetElementType();
                if (!typeof(Object).IsAssignableFrom(elem)) return false;
                info = new FillTargetInfo { Field = field, Kind = FieldKind.Array, ElementType = elem };
                return true;
            }

            if (!ft.IsGenericType) return false;
            Type def = ft.GetGenericTypeDefinition();
            Type[] args = ft.GetGenericArguments();

            if (def == typeof(List<>) && typeof(Object).IsAssignableFrom(args[0]))
            {
                info = new FillTargetInfo { Field = field, Kind = FieldKind.ListLike, ElementType = args[0] };
                return true;
            }
            if (def == typeof(HashSet<>) && typeof(Object).IsAssignableFrom(args[0]))
            {
                info = new FillTargetInfo { Field = field, Kind = FieldKind.HashSet, ElementType = args[0] };
                return true;
            }
            if (def == typeof(Dictionary<,>) && typeof(Object).IsAssignableFrom(args[1]))
            {
                if (args[0].IsEnum)
                    info = new FillTargetInfo { Field = field, Kind = FieldKind.EnumDict, KeyType = args[0], ElementType = args[1] };
                else if (args[0] == typeof(string))
                    info = new FillTargetInfo { Field = field, Kind = FieldKind.StringDict, KeyType = args[0], ElementType = args[1] };
                return info != null;
            }
            return false;
        }

        // Walks the inheritance chain so private fields declared on base classes are included.
        private static IEnumerable<FieldInfo> EnumerateFillableFields(Type hostType)
        {
            var seen = new HashSet<string>();
            for (Type t = hostType; t != null && t != typeof(object); t = t.BaseType)
                foreach (FieldInfo f in t.GetFields(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.DeclaredOnly))
                    if (seen.Add(f.Name) && TryClassifyField(f, out _))
                        yield return f;
        }

        private static FieldInfo FindField(Type hostType, string name)
        {
            for (Type t = hostType; t != null && t != typeof(object); t = t.BaseType)
            {
                FieldInfo f = t.GetField(name, BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.DeclaredOnly);
                if (f != null) return f;
            }
            return null;
        }

        private IEnumerable<ValueDropdownItem<string>> GetFillableFieldOptions()
        {
            Object host = ResolveHost();
            if (host == null)
                yield break;
            foreach (FieldInfo f in EnumerateFillableFields(host.GetType()))
                yield return new ValueDropdownItem<string>($"{f.Name}  ({FriendlyTypeName(f.FieldType)})", f.Name);
        }

        private static string FriendlyTypeName(Type t)
        {
            if (t.IsArray) return FriendlyTypeName(t.GetElementType()) + "[]";
            if (!t.IsGenericType) return t.Name;
            string name = t.Name.Substring(0, t.Name.IndexOf('`'));
            return $"{name}<{string.Join(", ", t.GetGenericArguments().Select(FriendlyTypeName))}>";
        }

        /// <summary>No error logs — safe to call from GUI predicates every repaint.</summary>
        private FillTargetInfo ClassifySelectedSilent()
        {
            Object host = ResolveHost();
            if (host == null || string.IsNullOrEmpty(SelectedFieldName)) return null;
            FieldInfo field = FindField(host.GetType(), SelectedFieldName);
            if (field == null) return null;
            return TryClassifyField(field, out FillTargetInfo info) ? info : null;
        }

        private bool TryResolveField(out Object host, out FillTargetInfo info)
        {
            info = null;
            host = ResolveHost();
            if (host == null)
            {
                Debug.LogError("[Feeder] Chưa chọn Target (GameObject thì phải chọn thêm Component).");
                return false;
            }
            if (string.IsNullOrEmpty(SelectedFieldName))
            {
                Debug.LogError("[Feeder] Chưa chọn Field.");
                return false;
            }
            FieldInfo field = FindField(host.GetType(), SelectedFieldName);
            if (field == null)
            {
                Debug.LogError($"[Feeder] Không tìm thấy field '{SelectedFieldName}' trên {host.GetType().Name}.");
                return false;
            }
            if (!TryClassifyField(field, out info))
            {
                Debug.LogError($"[Feeder] Field '{SelectedFieldName}' không phải kiểu hỗ trợ " +
                               "(Dictionary<Enum,T> / Dictionary<string,T> / List<T> / T[] / HashSet<T> với T là UnityEngine.Object).");
                return false;
            }
            return true;
        }

        // ── Asset collection ──

        // Accepts any asset assignable to elementType; Texture2D expands to Sprite sub-assets when filling Sprites,
        // and a dragged prefab (always a GameObject) resolves to its root component when filling a Component type.
        private List<Object> CollectAssetsOfType(Type elementType)
        {
            var result = new List<Object>();
            var seen = new HashSet<Object>();
            int prefabCount = 0;
            foreach (Object asset in TargetAssets)
            {
                if (asset == null) continue;

                if (elementType.IsInstanceOfType(asset))
                {
                    if (seen.Add(asset)) result.Add(asset);
                    continue;
                }

                if (elementType == typeof(Sprite) && asset is Texture2D)
                {
                    string path = AssetDatabase.GetAssetPath(asset);
                    foreach (Object sub in AssetDatabase.LoadAllAssetsAtPath(path))
                        if (sub is Sprite sprite && seen.Add(sprite))
                            result.Add(sprite);
                    continue;
                }

                // Project window can only drag prefab roots (GameObject), never a component — so a Component-typed
                // field is unfillable unless we resolve it here. Root only: matching compares component.name, which
                // is the owning GameObject's name, so a component from a child would silently match the child's name.
                if (typeof(Component).IsAssignableFrom(elementType) && asset is GameObject go)
                {
                    prefabCount++;
                    Component comp = go.GetComponent(elementType);
                    if (comp != null && seen.Add(comp)) result.Add(comp);
                    continue;
                }
            }

            if (result.Count == 0)
            {
                if (prefabCount > 0)
                    Debug.LogWarning($"[Feeder] Đã kéo {prefabCount} prefab vào Target Assets nhưng không prefab nào " +
                                     $"có component {elementType.Name} ở root (component ở child không được lấy).");
                else
                    Debug.LogWarning($"[Feeder] Không tìm thấy asset kiểu {elementType.Name} nào trong Target Assets.");
            }
            return result;
        }

        // ── Matching ──

        private List<KeyMatchResult> ComputeMatchesFor(Object host, FillTargetInfo info, List<Object> assets)
        {
            object current = info.Field.GetValue(host);
            Object fallback = ResolveFallback(info);

            switch (info.Kind)
            {
                case FieldKind.EnumDict:
                {
                    Array enumValues = Enum.GetValues(info.KeyType);
                    var keys = new object[enumValues.Length];
                    var keyNames = new string[enumValues.Length];
                    for (int i = 0; i < enumValues.Length; i++)
                    {
                        keys[i] = enumValues.GetValue(i);
                        keyNames[i] = keys[i].ToString();
                    }
                    return ComputeDictMatches(keys, keyNames, assets, current as IDictionary, fallback);
                }
                case FieldKind.StringDict:
                {
                    var dict = current as IDictionary;
                    if (dict == null || dict.Count == 0)
                    {
                        // No keys to match against — add every collected asset keyed by its name.
                        assets.Sort((a, b) => EditorUtility.NaturalCompare(a.name, b.name));
                        return assets.Select(a => new KeyMatchResult
                        {
                            Key = a.name, KeyName = a.name, AssignedAsset = a, BestScore = 1f, Status = MatchStatus.Matched,
                        }).ToList();
                    }
                    object[] keys = dict.Keys.Cast<object>().ToArray();
                    string[] keyNames = keys.Select(k => (string)k).ToArray();
                    return ComputeDictMatches(keys, keyNames, assets, dict, fallback);
                }
                default:
                    return ComputeCollectionMatches(current, assets);
            }
        }

        private Object ResolveFallback(FillTargetInfo info)
        {
            if (!info.IsDict || AssetDefault == null) return null;
            if (info.ElementType.IsInstanceOfType(AssetDefault)) return AssetDefault;
            Debug.LogWarning($"[Feeder] Asset Default ({AssetDefault.name}) không phải kiểu {info.ElementType.Name} — bỏ qua.");
            return null;
        }

        // Globally-optimal greedy assignment:
        // 1. Score all (key, asset) pairs
        // 2. Sort descending by score
        // 3. Assign from highest score — skip if key or asset already taken
        // This ensures the best match globally wins regardless of key order.
        private List<KeyMatchResult> ComputeDictMatches(object[] keys, string[] keyNames, List<Object> assets, IDictionary dictionary, Object fallback)
        {
            int keyCount = keys.Length;
            int assetCount = assets.Count;

            var normalizedKeys = new string[keyCount];
            for (int i = 0; i < keyCount; i++)
                normalizedKeys[i] = FuzzyMatchUtils.Normalize(keyNames[i]);

            var normalizedAssets = new string[assetCount];
            for (int i = 0; i < assetCount; i++)
                normalizedAssets[i] = assets[i] != null ? FuzzyMatchUtils.Normalize(assets[i].name) : null;

            // Determine which keys to skip (override disabled + already has a non-null value)
            var skipAssets = new Object[keyCount];
            if (!OverrideExistingValues && dictionary != null)
            {
                for (int i = 0; i < keyCount; i++)
                {
                    if (!dictionary.Contains(keys[i])) continue;
                    if (dictionary[keys[i]] is Object existing && existing != null)
                        skipAssets[i] = existing;
                }
            }

            // Build all (keyIdx, assetIdx, score) pairs for non-skipped keys
            var candidates = new List<(int keyIdx, int assetIdx, float score)>();
            for (int k = 0; k < keyCount; k++)
            {
                if (skipAssets[k] != null) continue;
                for (int a = 0; a < assetCount; a++)
                {
                    if (normalizedAssets[a] == null) continue;
                    float score = FuzzyMatchUtils.Similarity(normalizedKeys[k], normalizedAssets[a]);
                    candidates.Add((k, a, score));
                }
            }

            // Track best raw score per key for display on fallback rows
            var bestRawScores = new float[keyCount];
            foreach ((int k, int _, float score) in candidates)
                if (score > bestRawScores[k])
                    bestRawScores[k] = score;

            // Sort descending — highest-confidence pairs assigned first
            candidates.Sort((x, y) => y.score.CompareTo(x.score));

            var assignedKeys = new HashSet<int>();
            var assignedAssets = new HashSet<int>();
            var assignments = new Dictionary<int, (int assetIdx, float score)>();

            foreach ((int k, int a, float score) in candidates)
            {
                if (score < _matchThreshold) break;
                if (assignedKeys.Contains(k) || assignedAssets.Contains(a)) continue;

                assignments[k] = (a, score);
                assignedKeys.Add(k);
                assignedAssets.Add(a);
            }

            // Build result in key order
            var results = new List<KeyMatchResult>(keyCount);
            for (int i = 0; i < keyCount; i++)
            {
                if (skipAssets[i] != null)
                {
                    results.Add(new KeyMatchResult { Key = keys[i], KeyName = keyNames[i], AssignedAsset = skipAssets[i], Status = MatchStatus.Skipped });
                    continue;
                }

                if (assignments.TryGetValue(i, out (int assetIdx, float score) match))
                {
                    results.Add(new KeyMatchResult { Key = keys[i], KeyName = keyNames[i], AssignedAsset = assets[match.assetIdx], BestScore = match.score, Status = MatchStatus.Matched });
                    continue;
                }

                results.Add(new KeyMatchResult { Key = keys[i], KeyName = keyNames[i], AssignedAsset = fallback, BestScore = bestRawScores[i], Status = MatchStatus.FallbackDefault });
            }

            return results;
        }

        // Collections have no keys: result = kept existing items (override off) + all type-compatible assets sorted by name.
        private List<KeyMatchResult> ComputeCollectionMatches(object current, List<Object> assets)
        {
            var results = new List<KeyMatchResult>();
            var existing = new List<Object>();
            if (!OverrideExistingValues && current != null)
                foreach (object item in (IEnumerable)current)
                    if (item is Object obj && obj != null)
                        existing.Add(obj);

            var existingSet = new HashSet<Object>(existing);
            assets.Sort((a, b) => EditorUtility.NaturalCompare(a.name, b.name));

            foreach (Object obj in existing)
                results.Add(new KeyMatchResult { Key = obj, KeyName = obj.name, AssignedAsset = obj, Status = MatchStatus.Skipped });
            foreach (Object asset in assets)
                if (!existingSet.Contains(asset))
                    results.Add(new KeyMatchResult { Key = asset, KeyName = asset.name, AssignedAsset = asset, BestScore = 1f, Status = MatchStatus.Matched });
            return results;
        }

        // ── Writing ──

        private void FillDictionaryField(Object host, FillTargetInfo info, List<KeyMatchResult> matches)
        {
            object dictionary = info.Field.GetValue(host);
            if (dictionary == null)
            {
                dictionary = Activator.CreateInstance(info.Field.FieldType);
                info.Field.SetValue(host, dictionary);
            }

            var dict = (IDictionary)dictionary;
            foreach (KeyMatchResult match in matches)
            {
                if (match.Status == MatchStatus.Skipped)
                {
                    Debug.Log($"<color=yellow>Skip '{match.KeyName}' — đã có giá trị (override off)</color>");
                    continue;
                }
                dict[match.Key] = match.AssignedAsset;
                LogMatchResult(match);
            }
        }

        // Matches already contain kept-existing + new entries, so a clear + rebuild is uniform for all kinds.
        private void FillCollectionField(Object host, FillTargetInfo info, List<KeyMatchResult> matches)
        {
            List<Object> finalAssets = matches.Select(m => m.AssignedAsset).Where(a => a != null).ToList();
            object current = info.Field.GetValue(host);

            switch (info.Kind)
            {
                case FieldKind.ListLike:
                {
                    var list = (IList)(current ?? Activator.CreateInstance(info.Field.FieldType));
                    list.Clear();
                    foreach (Object a in finalAssets) list.Add(a);
                    info.Field.SetValue(host, list);
                    break;
                }
                case FieldKind.Array:
                {
                    Array arr = Array.CreateInstance(info.ElementType, finalAssets.Count);
                    for (int i = 0; i < finalAssets.Count; i++) arr.SetValue(finalAssets[i], i);
                    info.Field.SetValue(host, arr);
                    break;
                }
                case FieldKind.HashSet:
                {
                    object set = current ?? Activator.CreateInstance(info.Field.FieldType);
                    Type setType = info.Field.FieldType;
                    setType.GetMethod("Clear").Invoke(set, null);
                    MethodInfo add = setType.GetMethod("Add");
                    foreach (Object a in finalAssets) add.Invoke(set, new object[] { a });
                    info.Field.SetValue(host, set);
                    break;
                }
            }
        }

        private static void SaveHost(Object host)
        {
            EditorUtility.SetDirty(host);
            if (host is Component c && !EditorUtility.IsPersistent(c))
            {
                if (PrefabUtility.IsPartOfPrefabInstance(c))
                    PrefabUtility.RecordPrefabInstancePropertyModifications(c);
                EditorSceneManager.MarkSceneDirty(c.gameObject.scene);
            }
            else
            {
                AssetDatabase.SaveAssets();
            }
        }

        // Unity's serializer can't store Dictionary/HashSet — only Odin-serialized hosts persist them.
        private static void WarnIfLikelyNotSerialized(Object host, FillTargetInfo info)
        {
            if (info.Kind == FieldKind.ListLike || info.Kind == FieldKind.Array) return;
            if (host is SerializedScriptableObject || host is SerializedMonoBehaviour) return;
            Debug.LogWarning($"[Feeder] {host.GetType().Name} không dùng Odin serialization — " +
                             $"field {FriendlyTypeName(info.Field.FieldType)} có thể không được lưu lại.");
        }

        // ── Display ──

        private string FormatMatchScore(KeyMatchResult match, FillTargetInfo info)
        {
            if (!info.IsDict)
                return match.Status == MatchStatus.Skipped ? "keep" : "add";

            switch (match.Status)
            {
                case MatchStatus.Skipped:         return "skip";
                case MatchStatus.Matched:          return $"{match.BestScore * 100f:F1}%";
                case MatchStatus.FallbackDefault:
                    if (match.AssignedAsset != null) return "default";
                    return match.BestScore > 0f ? $"{match.BestScore * 100f:F1}%" : "—";
                default:                           return "—";
            }
        }

        private void LogMatchResult(KeyMatchResult match)
        {
            switch (match.Status)
            {
                case MatchStatus.Matched when match.AssignedAsset != null:
                    Debug.Log($"<color=cyan>Match: '{match.KeyName}' → '{match.AssignedAsset.name}' ({match.BestScore * 100f:F1}%)</color>");
                    break;
                case MatchStatus.FallbackDefault when match.AssignedAsset != null:
                    Debug.Log($"<color=orange>Default: '{match.KeyName}' → '{match.AssignedAsset.name}' (best: {match.BestScore * 100f:F1}%)</color>");
                    break;
                default:
                    Debug.Log($"<color=red>No match for '{match.KeyName}' (best: {match.BestScore * 100f:F1}%)</color>");
                    break;
            }
        }
    }
}
