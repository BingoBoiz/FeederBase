using System.Collections.Generic;
using System.Text;
using Sirenix.OdinInspector;
using UnityEditor;
using UnityEngine;

namespace Feeder
{
    public sealed class FAssetGroupTool : FTargetAssetsToolBase
    {
        private const string Tab = "GroupMode";
        private const string TabAutoDiscover = "Auto Discover";
        private const int MaxNgramLen = 6;
        private const float KeywordPreviewRowHeight = 20f;
        private const float KeywordPreviewMaxHeight = 400f;
        private const string GroupOriginal = "Original";
        private const string GroupUncategorized = "Uncategorized";

        [System.Serializable]
        private sealed class AssetGroup
        {
            public string GroupName;
            public List<Object> Assets = new List<Object>();
        }

        private sealed class PatternInfo
        {
            public string Key;         // lowercase, underscore-joined for prefix matching
            public string DisplayName; // original-case display
            public int TokenCount;
            public List<Object> Assets = new List<Object>();
        }

        protected override string GetDescription()
        {
            return "Gom TargetAssets thành các nhóm theo pattern tên hoặc từ khóa. " +
                   "Apply ghi subset vào TargetAssets cho Rename / Organizer xử lý tiếp.";
        }

        [PropertyOrder(-10)]
        [OnInspectorGUI]
        private void DrawGuide()
        {
            GUILayout.Space(2);
            FStylesUtils.DrawInfoBox(
                "Auto Discover   phát hiện nhóm từ token chung trong tên (_/-/. delimiter)\n" +
                "Min Asset Count ngưỡng tối thiểu asset khớp pattern (thường 2–3)\n" +
                "Discover Groups xóa nhóm cũ, tạo lại + Uncategorized + Original\n" +
                "Keyword         nhập từ khóa → Find Groups xem preview → Add Group\n" +
                "Merge           bấm Merge ở nhóm nguồn, rồi → Here ở nhóm đích\n" +
                "Apply           ghi asset của nhóm đó vào TargetAssets\n" +
                "Restore Original / Clear Groups   khôi phục hoặc reset về nhóm Original"
            );
            GUILayout.Space(4);
        }

        [SerializeField, HideInInspector]
        private List<AssetGroup> _groups = new List<AssetGroup>();

        // ── pattern settings ──
        [System.NonSerialized] private int _patternMinCount = 2;

        // ── keyword input ──
        [System.NonSerialized] private string _kwKeyword = "";
        [System.NonSerialized] private List<Object> _kwPreviewAssets = new List<Object>();
        [System.NonSerialized] private bool _kwPreviewReady;
        [System.NonSerialized] private Vector2 _kwPreviewScrollPos;

        // ── ui state ──
        [System.NonSerialized] private Dictionary<int, bool> _foldouts = new Dictionary<int, bool>();
        [System.NonSerialized] private int _mergeSourceIndex = -1;

        private static readonly StringBuilder s_Builder = new StringBuilder(64);

        private void OnEnable()
        {
            if (_foldouts == null) _foldouts = new Dictionary<int, bool>();
            if (_kwPreviewAssets == null) _kwPreviewAssets = new List<Object>();
            _mergeSourceIndex = -1;
        }

        protected override void OnTargetAssetsChanged()
        {
            _foldouts.Clear();
            _mergeSourceIndex = -1;
            _kwPreviewAssets.Clear();
            _kwPreviewReady = false;
            RebuildToOriginal();
        }

        private void RebuildToOriginal()
        {
            _groups.Clear();
            List<Object> assets = TargetAssets;
            if (assets.Count > 0)
                _groups.Add(new AssetGroup { GroupName = GroupOriginal, Assets = new List<Object>(assets) });
        }

        // ───────────────────────────── Auto Discover Tab ─────────────────────────────

        [PropertySpace(SpaceBefore = 10)]
        [TabGroup(Tab, TabAutoDiscover, false, -1f)]
        [ShowInInspector]
        [LabelText("Min Asset Count")]
        [PropertyRange(2, 20)]
        [PropertyOrder(0)]
        private int PatternMinCount
        {
            get => _patternMinCount;
            set => _patternMinCount = value;
        }

        [TabGroup(Tab, TabAutoDiscover, false, -1f)]
        [Button("Discover Groups", ButtonSizes.Medium)]
        [GUIColor(0.4f, 0.8f, 1f)]
        [PropertyOrder(1)]
        private void DiscoverGroups()
        {
            List<PatternInfo> patterns = BuildCandidatePatterns();
            if (patterns.Count == 0)
            {
                Debug.LogWarning($"[Asset Group] No patterns found with min count = {_patternMinCount}.");
                return;
            }

            _mergeSourceIndex = -1;
            _foldouts.Clear();
            _groups.Clear();

            for (int i = 0; i < patterns.Count; i++)
            {
                PatternInfo p = patterns[i];
                _groups.Add(new AssetGroup { GroupName = p.DisplayName, Assets = new List<Object>(p.Assets) });
            }

            HashSet<Object> categorized = new HashSet<Object>();
            for (int i = 0; i < _groups.Count; i++)
            {
                for (int j = 0; j < _groups[i].Assets.Count; j++)
                    categorized.Add(_groups[i].Assets[j]);
            }

            List<Object> uncategorized = new List<Object>();
            List<Object> original = TargetAssets;
            for (int i = 0; i < original.Count; i++)
            {
                if (original[i] != null && !categorized.Contains(original[i]))
                    uncategorized.Add(original[i]);
            }

            if (uncategorized.Count > 0)
                _groups.Add(new AssetGroup { GroupName = GroupUncategorized, Assets = uncategorized });

            if (original.Count > 0)
                _groups.Add(new AssetGroup { GroupName = GroupOriginal, Assets = new List<Object>(original) });

            SortGroups();
            Debug.Log($"<color=cyan>[Asset Group] Discovered {patterns.Count} groups. Uncategorized: {uncategorized.Count} assets.</color>");
        }

        // Sort pattern groups by asset count desc; Original stays last, Uncategorized stays above it.
        private void SortGroups()
        {
            AssetGroup original = FindGroup(GroupOriginal);
            AssetGroup uncategorized = FindGroup(GroupUncategorized);

            _groups.RemoveAll(g => g.GroupName == GroupOriginal || g.GroupName == GroupUncategorized);
            _groups.Sort((a, b) => b.Assets.Count.CompareTo(a.Assets.Count));

            if (uncategorized != null) _groups.Add(uncategorized);
            if (original != null) _groups.Add(original);

            _foldouts.Clear();
        }

        private AssetGroup FindGroup(string name)
        {
            for (int i = 0; i < _groups.Count; i++)
                if (string.Equals(_groups[i].GroupName, name, System.StringComparison.OrdinalIgnoreCase))
                    return _groups[i];
            return null;
        }

        private int FindGroupIndex(string name)
        {
            for (int i = 0; i < _groups.Count; i++)
                if (string.Equals(_groups[i].GroupName, name, System.StringComparison.OrdinalIgnoreCase))
                    return i;
            return -1;
        }

        // ── N-gram based pattern discovery ──────────────────────────────────────────

        private List<PatternInfo> BuildCandidatePatterns()
        {
            List<Object> assets = TargetAssets;
            if (assets.Count == 0)
            {
                Debug.LogWarning("[Asset Group] No target assets to analyze.");
                return new List<PatternInfo>();
            }

            // tokenize all assets upfront
            List<string[]> allTokens = new List<string[]>(assets.Count);
            for (int i = 0; i < assets.Count; i++)
                allTokens.Add(assets[i] != null ? TokenizeName(assets[i].name).ToArray() : new string[0]);

            // ngramKey (lowercase, underscore-joined) → set of asset indices
            Dictionary<string, HashSet<int>> ngramAssets = new Dictionary<string, HashSet<int>>(System.StringComparer.OrdinalIgnoreCase);
            Dictionary<string, string> ngramDisplay = new Dictionary<string, string>(System.StringComparer.OrdinalIgnoreCase);
            Dictionary<string, int> ngramLen = new Dictionary<string, int>(System.StringComparer.OrdinalIgnoreCase);

            for (int i = 0; i < assets.Count; i++)
            {
                if (assets[i] == null) continue;
                string[] tokens = allTokens[i];
                HashSet<string> seenInAsset = new HashSet<string>(System.StringComparer.OrdinalIgnoreCase);

                for (int start = 0; start < tokens.Length; start++)
                {
                    string key = "";
                    string display = "";

                    for (int len = 1; len <= System.Math.Min(MaxNgramLen, tokens.Length - start); len++)
                    {
                        string t = tokens[start + len - 1];
                        // stop extending n-gram at short or purely numeric tokens
                        if (t.Length < 2 || IsNumericOnly(t)) break;

                        key = len == 1 ? t.ToLowerInvariant() : key + "_" + t.ToLowerInvariant();
                        display = len == 1 ? t : display + "_" + t;

                        if (seenInAsset.Contains(key)) continue;
                        seenInAsset.Add(key);

                        if (!ngramAssets.TryGetValue(key, out HashSet<int> set))
                        {
                            set = new HashSet<int>();
                            ngramAssets[key] = set;
                            ngramDisplay[key] = display;
                            ngramLen[key] = len;
                        }
                        set.Add(i);
                    }
                }
            }

            // build candidates filtered by minCount
            List<PatternInfo> candidates = new List<PatternInfo>();
            foreach (KeyValuePair<string, HashSet<int>> kvp in ngramAssets)
            {
                if (kvp.Value.Count < _patternMinCount) continue;
                PatternInfo info = new PatternInfo
                {
                    Key = kvp.Key,
                    DisplayName = ngramDisplay[kvp.Key],
                    TokenCount = ngramLen[kvp.Key]
                };
                foreach (int idx in kvp.Value)
                    info.Assets.Add(assets[idx]);
                candidates.Add(info);
            }

            // sort by score: count × tokenCount²
            candidates.Sort((a, b) =>
            {
                int sa = a.Assets.Count * a.TokenCount * a.TokenCount;
                int sb = b.Assets.Count * b.TokenCount * b.TokenCount;
                return sb.CompareTo(sa);
            });

            return PruneSubsumed(candidates);
        }

        // Pattern A is subsumed only if a longer pattern B exists where:
        //   1. B.Key starts with A.Key+"_"  (B is a direct token extension of A)
        //   2. B.Assets ⊆ A.Assets          (B covers the same or fewer assets)
        // This prevents unrelated patterns (e.g. "VIP1_Everlasting") from
        // incorrectly subsuming independent patterns like "clothes2".
        private static List<PatternInfo> PruneSubsumed(List<PatternInfo> sorted)
        {
            List<PatternInfo> result = new List<PatternInfo>();
            for (int i = 0; i < sorted.Count; i++)
            {
                PatternInfo p = sorted[i];
                HashSet<Object> pSet = new HashSet<Object>(p.Assets);
                string pKeyPrefix = p.Key + "_";
                bool subsumed = false;

                for (int j = 0; j < sorted.Count; j++)
                {
                    if (j == i) continue;
                    PatternInfo q = sorted[j];
                    if (q.TokenCount <= p.TokenCount) continue;

                    // B must be a direct extension of A (shares A as token prefix)
                    if (!q.Key.StartsWith(pKeyPrefix, System.StringComparison.OrdinalIgnoreCase)) continue;

                    // all of B's assets must be within A's asset set
                    bool allQInP = true;
                    for (int k = 0; k < q.Assets.Count; k++)
                    {
                        if (!pSet.Contains(q.Assets[k]))
                        {
                            allQInP = false;
                            break;
                        }
                    }

                    if (allQInP)
                    {
                        subsumed = true;
                        break;
                    }
                }

                if (!subsumed)
                    result.Add(p);
            }
            return result;
        }

        // ───────────────────────────── Keyword Tab ──────────────────────────────────

        [PropertySpace(SpaceBefore = 10)]
        [TabGroup(Tab, "Keyword", false, 1f)]
        [LabelText("Keyword")]
        [ShowInInspector]
        [PropertyOrder(50)]
        private string KwKeyword
        {
            get => _kwKeyword;
            set
            {
                if (_kwKeyword == value) return;
                _kwKeyword = value;
                _kwPreviewReady = false;
                _kwPreviewAssets.Clear();
            }
        }

        [PropertySpace(SpaceBefore = 4)]
        [TabGroup(Tab, "Keyword", false, 1f)]
        [Button("Find Groups", ButtonSizes.Medium)]
        [GUIColor(0.9f, 0.75f, 0.35f)]
        [PropertyOrder(51)]
        private void FindKeywordGroups()
        {
            _kwPreviewAssets.Clear();
            _kwPreviewReady = false;

            if (string.IsNullOrEmpty(_kwKeyword))
            {
                Debug.LogWarning("[Asset Group] Keyword must not be empty.");
                return;
            }

            string lower = _kwKeyword.ToLowerInvariant();
            List<Object> assets = TargetAssets;
            for (int i = 0; i < assets.Count; i++)
            {
                if (assets[i] == null) continue;
                if (assets[i].name.ToLowerInvariant().Contains(lower))
                    _kwPreviewAssets.Add(assets[i]);
            }

            _kwPreviewReady = true;
            Debug.Log($"<color=cyan>[Asset Group] Preview '{_kwKeyword}': {_kwPreviewAssets.Count} assets matched</color>");
        }

        [PropertySpace(SpaceBefore = 4)]
        [TabGroup(Tab, "Keyword", false, 1f)]
        [Button("Add Group", ButtonSizes.Medium)]
        [GUIColor(0.3f, 0.8f, 0.3f)]
        [PropertyOrder(52)]
        private void AddKeywordGroup()
        {
            if (string.IsNullOrEmpty(_kwKeyword))
            {
                Debug.LogWarning("[Asset Group] Keyword must not be empty.");
                return;
            }

            List<Object> matched = _kwPreviewReady
                ? new List<Object>(_kwPreviewAssets)
                : CollectByKeyword(_kwKeyword);

            if (matched.Count == 0)
            {
                Debug.LogWarning($"[Asset Group] No assets matched '{_kwKeyword}'.");
                return;
            }

            _groups.Add(new AssetGroup { GroupName = _kwKeyword, Assets = matched });
            SortGroups();
            _kwPreviewReady = false;
            _kwPreviewAssets.Clear();
            Debug.Log($"<color=cyan>[Asset Group] Added '{_kwKeyword}': {matched.Count} assets</color>");
        }

        [TabGroup(Tab, "Keyword", false, 1f)]
        [OnInspectorGUI]
        [PropertyOrder(60)]
        private void DrawKeywordPreview()
        {
            if (!_kwPreviewReady) return;

            GUILayout.Space(8);
            string header = _kwPreviewAssets.Count == 1
                ? $"Preview — 1 asset matched \"{_kwKeyword}\""
                : $"Preview — {_kwPreviewAssets.Count} assets matched \"{_kwKeyword}\"";
            EditorGUILayout.LabelField(header, EditorStyles.boldLabel);

            if (_kwPreviewAssets.Count == 0)
            {
                EditorGUILayout.HelpBox("No assets matched the keyword.", MessageType.Info);
                return;
            }

            float contentHeight = _kwPreviewAssets.Count * KeywordPreviewRowHeight;
            float viewHeight = Mathf.Min(contentHeight, KeywordPreviewMaxHeight);
            bool needsScroll = contentHeight > KeywordPreviewMaxHeight;

            if (needsScroll)
                _kwPreviewScrollPos = EditorGUILayout.BeginScrollView(_kwPreviewScrollPos, GUILayout.Height(viewHeight), GUILayout.ExpandWidth(true));
            else
                EditorGUILayout.BeginVertical(GUILayout.Height(viewHeight), GUILayout.ExpandWidth(true));

            for (int i = 0; i < _kwPreviewAssets.Count; i++)
                EditorGUILayout.ObjectField(_kwPreviewAssets[i], typeof(Object), false);

            if (needsScroll)
                EditorGUILayout.EndScrollView();
            else
                EditorGUILayout.EndVertical();
        }

        private List<Object> CollectByKeyword(string keyword)
        {
            List<Object> result = new List<Object>();
            string lower = keyword.ToLowerInvariant();
            List<Object> assets = TargetAssets;
            for (int i = 0; i < assets.Count; i++)
            {
                if (assets[i] == null) continue;
                if (assets[i].name.ToLowerInvariant().Contains(lower))
                    result.Add(assets[i]);
            }
            return result;
        }

        // ───────────────────────────── Groups Display ───────────────────────────────

        [PropertyOrder(100)]
        [OnInspectorGUI]
        private void DrawGroupsSection()
        {
            if (_groups.Count == 0) return;

            GUILayout.Space(12);
            EditorGUILayout.LabelField($"Groups ({_groups.Count})", EditorStyles.boldLabel);

            GUILayout.Space(4);
            EditorGUILayout.BeginHorizontal();

            Color originalBg = GUI.backgroundColor;

            GUI.backgroundColor = new Color(0.4f, 0.8f, 1f);
            if (GUILayout.Button("Restore Original", GUILayout.Height(26)))
            {
                AssetGroup orig = FindGroup(GroupOriginal);
                if (orig != null)
                    ApplyGroupToTargetAssets(orig);
                else
                    Debug.LogWarning("[Asset Group] No Original group found.");
            }

            GUI.backgroundColor = new Color(1f, 0.55f, 0.55f);
            if (GUILayout.Button("Clear Groups", GUILayout.Height(26)))
            {
                _mergeSourceIndex = -1;
                _foldouts.Clear();
                RebuildToOriginal();
            }

            GUI.backgroundColor = originalBg;
            EditorGUILayout.EndHorizontal();

            if (_mergeSourceIndex >= 0 && _mergeSourceIndex < _groups.Count)
            {
                EditorGUILayout.HelpBox(
                    $"Merge mode: click → Here on the target to merge \"{_groups[_mergeSourceIndex].GroupName}\" into it. Click Merge again to cancel.",
                    MessageType.Info);
            }

            GUILayout.Space(4);

            for (int i = 0; i < _groups.Count; i++)
                DrawSingleGroup(_groups[i], i);
        }

        private void DrawSingleGroup(AssetGroup group, int index)
        {
            bool expanded;
            _foldouts.TryGetValue(index, out expanded);

            bool isMergeSource = _mergeSourceIndex == index;
            bool mergeActive = _mergeSourceIndex >= 0;

            EditorGUILayout.BeginHorizontal(EditorStyles.helpBox);

            string label = group.Assets.Count == 1
                ? $"{group.GroupName} (1 asset)"
                : $"{group.GroupName} ({group.Assets.Count} assets)";

            expanded = EditorGUILayout.Foldout(expanded, label, true);
            _foldouts[index] = expanded;

            Color originalBg = GUI.backgroundColor;

            // Merge button (left of Apply)
            if (isMergeSource)
                GUI.backgroundColor = new Color(1f, 0.7f, 0.2f);     // orange = selected source
            else if (mergeActive)
                GUI.backgroundColor = new Color(0.5f, 0.85f, 1f);    // blue = available target
            else
                GUI.backgroundColor = new Color(0.9f, 0.75f, 0.35f); // idle

            string mergeLabel = isMergeSource ? "Cancel" : (mergeActive ? "→ Here" : "Merge");
            if (GUILayout.Button(mergeLabel, GUILayout.Width(54), GUILayout.Height(18)))
                HandleMergeClick(index);

            // Apply button
            GUI.backgroundColor = new Color(0.4f, 1f, 0.5f);
            if (GUILayout.Button("Apply", GUILayout.Width(52), GUILayout.Height(18)))
                ApplyGroupToTargetAssets(group);

            GUI.backgroundColor = originalBg;
            EditorGUILayout.EndHorizontal();

            if (!expanded) return;

            EditorGUI.indentLevel++;
            for (int j = 0; j < group.Assets.Count; j++)
                EditorGUILayout.ObjectField(group.Assets[j], typeof(Object), true);
            EditorGUI.indentLevel--;
            GUILayout.Space(2);
        }

        private void HandleMergeClick(int clickedIndex)
        {
            if (_mergeSourceIndex == -1)
            {
                _mergeSourceIndex = clickedIndex;
                return;
            }

            if (_mergeSourceIndex == clickedIndex)
            {
                _mergeSourceIndex = -1;
                return;
            }

            AssetGroup source = _groups[_mergeSourceIndex];
            AssetGroup target = _groups[clickedIndex];
            string mergedGroupName = target.GroupName;

            HashSet<Object> targetSet = new HashSet<Object>(target.Assets);
            for (int i = 0; i < source.Assets.Count; i++)
            {
                if (source.Assets[i] != null && targetSet.Add(source.Assets[i]))
                    target.Assets.Add(source.Assets[i]);
            }

            Debug.Log($"<color=cyan>[Asset Group] Merged '{source.GroupName}' → '{target.GroupName}' ({target.Assets.Count} assets)</color>");
            _groups.RemoveAt(_mergeSourceIndex);
            _mergeSourceIndex = -1;
            SortGroups();

            int mergedIndex = FindGroupIndex(mergedGroupName);
            if (mergedIndex >= 0)
                _foldouts[mergedIndex] = true;
        }

        private void ApplyGroupToTargetAssets(AssetGroup group)
        {
            FDataContainer data = GetDataContainer();
            data.TargetAssets.Clear();
            data.TargetAssets.AddRange(group.Assets);
            data.SyncAllFromAssets();
            FDataPersistenceService.SaveData(data);
            Debug.Log($"<color=cyan>[Asset Group] Applied '{group.GroupName}' → {group.Assets.Count} assets to TargetAssets</color>");
        }

        // ───────────────────────────── Tokenization ─────────────────────────────────

        private static List<string> TokenizeName(string name)
        {
            List<string> tokens = new List<string>();
            if (string.IsNullOrEmpty(name)) return tokens;

            s_Builder.Length = 0;
            for (int i = 0; i < name.Length; i++)
            {
                char c = name[i];
                if (c == '_' || c == '-' || c == '.' || c == ' ')
                {
                    FlushToken(tokens);
                    continue;
                }
                s_Builder.Append(c);
            }
            FlushToken(tokens);
            return tokens;
        }

        private static void FlushToken(List<string> tokens)
        {
            if (s_Builder.Length == 0) return;
            tokens.Add(s_Builder.ToString());
            s_Builder.Length = 0;
        }

        private static bool IsNumericOnly(string token)
        {
            for (int i = 0; i < token.Length; i++)
            {
                if (!char.IsDigit(token[i])) return false;
            }
            return token.Length > 0;
        }
    }
}
