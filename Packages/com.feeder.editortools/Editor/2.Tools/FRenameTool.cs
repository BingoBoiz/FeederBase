using Sirenix.OdinInspector;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using UnityEditor;
using UnityEngine;

namespace Feeder
{
    public sealed class FRenameTool : FTargetAssetsToolBase
    {
        protected override string GetDescription()
        {
            return "Đổi tên asset hoặc scene object theo pattern hoặc tìm & thay thế. Kéo asset vào TargetAssets trước.";
        }

        [System.Serializable]
        private class AssetRenameEntry
        {
            public string AssetPath;
            public string OldName;
            public string NewName;
        }

        private class SceneObjectRenameEntry
        {
            public GameObject GameObject;
            public string OldName;
            public string NewName;
        }

        /// <summary>A sprite sub-asset inside a Multiple/Polygon-mode texture (renamed via the sprite data provider).</summary>
        private class SpriteRenameEntry
        {
            public string TexturePath;
            public string OldName;
            public string NewName;
        }

        [System.Serializable]
        private sealed class PreviewRow
        {
            [TableColumnWidth(160)] public string OldName;
            [TableColumnWidth(160)] public string NewName;
            public string Status;
        }

        /// <summary>Result of a dry-run: the rename entries plus a per-target preview row list.</summary>
        private sealed class RenamePlan
        {
            public readonly Dictionary<string, AssetRenameEntry> AssetEntries = new Dictionary<string, AssetRenameEntry>();
            public readonly Dictionary<int, SceneObjectRenameEntry> SceneEntries = new Dictionary<int, SceneObjectRenameEntry>();
            public readonly Dictionary<string, SpriteRenameEntry> SpriteEntries = new Dictionary<string, SpriteRenameEntry>();
            public readonly List<PreviewRow> Rows = new List<PreviewRow>();
            public int ErrorCount;
            public int ChangeCount => AssetEntries.Count + SceneEntries.Count + SpriteEntries.Count;

            public void AddRow(string oldName, string newName)
                => Rows.Add(new PreviewRow { OldName = oldName, NewName = newName, Status = newName == oldName ? "unchanged" : "OK" });

            public void AddErrorRow(string oldName, string message)
            {
                Rows.Add(new PreviewRow { OldName = oldName, Status = message });
                ErrorCount++;
            }
        }

        [PropertySpace(SpaceBefore = 10)]
        [TabGroup("RenameMode", "Change Pattern")]
        [LabelText("Input Pattern"), Tooltip("use {number}, {variant}, {EnumType} (numeric), {(s)EnumType} (string)")]
        [ShowInInspector, ReadOnly, EnableGUI] private string inputPattern = "";

        
        [PropertySpace(SpaceBefore = 6, SpaceAfter = 2)]
        [TabGroup("RenameMode", "Change Pattern")]
        [LabelText("Output Pattern"), Tooltip("use {number}, {variant}, {start:step}, {EnumType} (numeric), {(s)EnumType} (string)")]
        [ShowInInspector, OnValueChanged(nameof(SchedulePreviewRebuild))]
        public string outputPattern
        {
            get => FToolPrefs.GetString(nameof(FRenameTool), nameof(outputPattern), "");
            set => FToolPrefs.SetString(nameof(FRenameTool), nameof(outputPattern), value);
        }

        // Last value this tool wrote into outputPattern automatically. While outputPattern is empty
        // or still equals this value the field is "auto-owned" and follows the analyzed pattern;
        // once the user edits it, auto-seeding stops touching it. NonSerialized on purpose: after a
        // domain reload any non-empty outputPattern is conservatively treated as user-owned.
        [System.NonSerialized] private string _lastAutoSeededOutput;

        [System.NonSerialized] private FAssetRevertService.RevertOperation _lastOp;
        [System.NonSerialized] private bool _previewScheduled;

        [TabGroup("RenameMode", "Change Pattern")]
        [OnInspectorGUI, PropertyOrder(1)]
        private void DrawGuide()
        {
            GUILayout.Space(2);
            FStylesUtils.DrawInfoBox(
                "{number}         số được tách ra từ input pattern\n" +
                "{variant}        đoạn văn bản được tách ra từ input\n" +
                "{start:step}     đếm tự động theo asset  (vd: {0:1} → 0, 1, 2…)\n" +
                "{EnumType}       giá trị số của enum theo slot index\n" +
                "{(s)EnumType}    tên chuỗi của enum theo slot index"
            );
            GUILayout.Space(4);
        }
        
        [TabGroup("RenameMode", "Change Pattern")]
        [Button("Analyze Pattern", ButtonSizes.Medium), PropertyOrder(2)]
        private void AnalyzePattern()
        {
            RefreshInputPattern();
        }

        [TabGroup("RenameMode", "Change Pattern"), PropertyOrder(3)]
        [ShowInInspector, TableList(IsReadOnly = true, NumberOfItemsPerPage = 10)]
        [LabelText("Preview")]
        private List<PreviewRow> patternPreview = new List<PreviewRow>();

        private bool CanApplyRename => (TargetAssets?.Count ?? 0) > 0
            && !string.IsNullOrEmpty(inputPattern) && !string.IsNullOrEmpty(outputPattern);

        private bool CanApplyFindReplace => (TargetAssets?.Count ?? 0) > 0 && !string.IsNullOrEmpty(findString);

        private bool HasTargets => (TargetAssets?.Count ?? 0) > 0;

        private string ApplyBlockedReason =>
            !HasTargets ? "TargetAssets trống — kéo asset vào hoặc bấm Add Selection."
            : string.IsNullOrEmpty(inputPattern) ? "Input Pattern trống — bấm Analyze Pattern."
            : "Output Pattern trống.";

        [TabGroup("RenameMode", "Change Pattern"), PropertyOrder(4)]
        [InfoBox("$ApplyBlockedReason", InfoMessageType.Info, VisibleIf = "@!CanApplyRename")]
        [Button("Apply Rename", ButtonSizes.Large), EnableIf(nameof(CanApplyRename))]
        private void ApplyRename()
        {
            if (!CanApplyRename)
                return;

            var plan = BuildRenamePlan(TargetAssets, inputPattern, outputPattern);
            ApplyPlan(plan, "Rename");
        }

        [TabGroup("RenameMode", "Change Pattern"), PropertyOrder(5)]
        [Button("Revert Last Rename", ButtonSizes.Medium), EnableIf(nameof(HasRevertableRename))]
        private void RevertLastRename()
        {
            if (FAssetRevertService.Revert(_lastOp, out var report))
                _lastOp = null;
            Debug.Log(report);
            RefreshInputPattern();
        }

        private bool HasRevertableRename => _lastOp != null && !_lastOp.IsEmpty;

        /// <summary>Applies the non-error entries of a plan, records revert info and pings the results.</summary>
        private void ApplyPlan(RenamePlan plan, string label)
        {
            if (plan.ChangeCount == 0)
            {
                Debug.Log($"[FRenameTool] {label}: không có gì để đổi tên"
                          + (plan.ErrorCount > 0 ? $" ({plan.ErrorCount} lỗi — xem Preview)." : "."));
                return;
            }

            var op = FAssetRevertService.Begin(label,
                "Scene-object renames dùng Ctrl+Z; nút này revert asset-file và sprite renames.");
            ApplyAssetRenames(plan.AssetEntries, op);
            ApplySpriteRenames(plan.SpriteEntries, op);
            ApplySceneObjectRenames(plan.SceneEntries);
            _lastOp = op.IsEmpty ? null : op;

            Debug.Log($"[FRenameTool] {label}: đổi tên {plan.ChangeCount}, bỏ qua {plan.ErrorCount} lỗi.");
            RefreshInputPattern();
            FSelectionUtils.SelectAndPing(TargetAssets.Where(a => a != null).ToList());
        }

        [PropertySpace(SpaceBefore = 10)]
        [TabGroup("RenameMode", "Find & Replace")]
        [LabelText("Find")]
        [ShowInInspector, OnValueChanged(nameof(SchedulePreviewRebuild))]
        public string findString
        {
            get => FToolPrefs.GetString(nameof(FRenameTool), nameof(findString), "");
            set => FToolPrefs.SetString(nameof(FRenameTool), nameof(findString), value);
        }

        [PropertySpace(SpaceBefore = 6, SpaceAfter = 6)]
        [TabGroup("RenameMode", "Find & Replace")]
        [LabelText("Replace With")]
        [ShowInInspector, OnValueChanged(nameof(SchedulePreviewRebuild))]
        public string replaceString
        {
            get => FToolPrefs.GetString(nameof(FRenameTool), nameof(replaceString), "");
            set => FToolPrefs.SetString(nameof(FRenameTool), nameof(replaceString), value);
        }

        [TabGroup("RenameMode", "Find & Replace"), PropertyOrder(3)]
        [ShowInInspector, TableList(IsReadOnly = true, NumberOfItemsPerPage = 10)]
        [LabelText("Preview")]
        private List<PreviewRow> findReplacePreview = new List<PreviewRow>();

        private string FindReplaceBlockedReason =>
            !HasTargets ? "TargetAssets trống — kéo asset vào hoặc bấm Add Selection." : "Find trống.";

        [TabGroup("RenameMode", "Find & Replace"), PropertyOrder(4)]
        [InfoBox("$FindReplaceBlockedReason", InfoMessageType.Info, VisibleIf = "@!CanApplyFindReplace")]
        [Button("Apply Find & Replace", ButtonSizes.Large), EnableIf(nameof(CanApplyFindReplace))]
        private void ApplyFindAndReplace()
        {
            if (!CanApplyFindReplace)
                return;

            var plan = BuildFindReplacePlan(TargetAssets, findString, replaceString ?? "");
            ApplyPlan(plan, "Find & Replace");
        }

        [TabGroup("RenameMode", "Find & Replace"), PropertyOrder(5)]
        [Button("Revert Last Rename", ButtonSizes.Medium), EnableIf(nameof(HasRevertableRename))]
        private void RevertLastRenameFromFindReplaceTab() => RevertLastRename();

        [PropertySpace(SpaceBefore = 10)]
        [TabGroup("RenameMode", "Sprite → Texture Name")]
        [OnInspectorGUI]
        private void DrawSpriteSyncGuide()
        {
            GUILayout.Space(2);
            FStylesUtils.DrawInfoBox(
                "Đổi tên sprite bên trong texture Multiple-mode cho khớp tên file.\n" +
                "Kéo folder hoặc texture vào TargetAssets. Chỉ xử lý texture có đúng 1 sprite.\n" +
                "fileID của sprite được giữ nguyên nên mọi reference trong prefab/SO không bị đứt."
            );
            GUILayout.Space(4);
        }

        [TabGroup("RenameMode", "Sprite → Texture Name")]
        [InfoBox("TargetAssets trống — kéo asset vào hoặc bấm Add Selection.", InfoMessageType.Info, VisibleIf = "@!HasTargets")]
        [Button("Sync Sprite Name To Texture Name", ButtonSizes.Large), EnableIf(nameof(HasTargets))]
        private void SyncSpriteNamesToTextureNames()
        {
            if (!HasTargets)
                return;

            var texturePaths = CollectMultipleModeTexturePaths(TargetAssets);
            if (texturePaths.Count == 0)
            {
                Debug.LogWarning("[FRenameTool] No Multiple-mode textures found in TargetAssets.");
                return;
            }

            int renamed = FSpriteRenameUtils.SyncSpriteNamesToFileName(texturePaths);
            AssetDatabase.Refresh();
            Debug.Log($"[FRenameTool] Synced {renamed} sprite name(s) to texture name across {texturePaths.Count} texture(s).");
        }

        /// <summary>Expands TargetAssets (folders, textures, sprites) into distinct Multiple-mode texture paths.</summary>
        private static List<string> CollectMultipleModeTexturePaths(List<Object> assets)
        {
            var result = new List<string>();
            var seen = new HashSet<string>();
            for (int i = 0; i < assets.Count; i++)
            {
                var asset = assets[i];
                if (asset == null) continue;
                var path = AssetDatabase.GetAssetPath(asset);
                if (string.IsNullOrEmpty(path)) continue;

                if (AssetDatabase.IsValidFolder(path))
                {
                    foreach (var guid in AssetDatabase.FindAssets("t:Texture2D", new[] { path }))
                    {
                        var texPath = AssetDatabase.GUIDToAssetPath(guid);
                        if (seen.Add(texPath) && FSpriteRenameUtils.IsMultipleModeTexture(texPath))
                            result.Add(texPath);
                    }
                }
                else if (seen.Add(path) && FSpriteRenameUtils.IsMultipleModeTexture(path))
                {
                    result.Add(path);
                }
            }
            return result;
        }

        protected override void OnTargetAssetsChanged()
        {
            RefreshInputPattern();
        }

        private void RefreshInputPattern()
        {
            if (TargetAssets?.Count > 0)
                inputPattern = BuildPatternFromAssets(TargetAssets);
            else
                inputPattern = "";
            AutoSeedOutputPattern();
            SchedulePreviewRebuild();
        }

        private void AutoSeedOutputPattern()
        {
            string current = outputPattern;
            bool autoOwned = string.IsNullOrEmpty(current) || current == _lastAutoSeededOutput;
            if (!autoOwned)
                return;
            outputPattern = inputPattern;
            _lastAutoSeededOutput = inputPattern;
        }

        private void SchedulePreviewRebuild()
        {
            if (_previewScheduled) return;
            _previewScheduled = true;
            EditorApplication.delayCall += () =>
            {
                _previewScheduled = false;
                RebuildPreviews();
            };
        }

        private const int PreviewRowCap = 200;

        private void RebuildPreviews()
        {
            patternPreview.Clear();
            if (CanApplyRename)
                CopyRowsCapped(BuildRenamePlan(TargetAssets, inputPattern, outputPattern).Rows, patternPreview);

            findReplacePreview.Clear();
            if (CanApplyFindReplace)
                CopyRowsCapped(BuildFindReplacePlan(TargetAssets, findString, replaceString ?? "").Rows, findReplacePreview);
        }

        private static void CopyRowsCapped(List<PreviewRow> source, List<PreviewRow> destination)
        {
            for (int i = 0; i < source.Count && i < PreviewRowCap; i++)
                destination.Add(source[i]);
            if (source.Count > PreviewRowCap)
                destination.Add(new PreviewRow { OldName = $"… {source.Count - PreviewRowCap} more" });
        }

        private static bool IsSceneObject(Object obj)
        {
            return obj is GameObject go && !EditorUtility.IsPersistent(go);
        }

        /// <summary>
        /// True when <paramref name="obj"/> is a Sprite sub-asset of a Multiple/Polygon-mode texture.
        /// Single-mode sprites are excluded on purpose: their name mirrors the file name, so those
        /// rename through the normal texture-file path instead.
        /// </summary>
        private static bool TryGetSpriteTarget(Object obj, out string texturePath, out string spriteName)
        {
            texturePath = null;
            spriteName = null;
            if (!(obj is Sprite sprite)) return false;

            var path = AssetDatabase.GetAssetPath(sprite);
            if (string.IsNullOrEmpty(path)) return false;
            if (!FSpriteRenameUtils.IsMultipleModeTexture(path)) return false;

            texturePath = path;
            spriteName = sprite.name;
            return true;
        }

        private static void AddSpriteEntry(
            Dictionary<string, SpriteRenameEntry> spriteEntries,
            string texturePath, string oldName, string newName)
        {
            var key = texturePath + "|" + oldName;
            if (!spriteEntries.TryGetValue(key, out var entry))
                spriteEntries.Add(key, new SpriteRenameEntry { TexturePath = texturePath, OldName = oldName, NewName = newName });
            else if (entry.NewName != newName)
                throw new System.InvalidOperationException($"conflicting rename for sprite '{oldName}' in {texturePath}.");
        }

        private static string BuildPatternFromAssets(List<Object> assets)
        {
            if (assets == null || assets.Count == 0)
                return "";

            var names = new List<string>(assets.Count);
            for (int i = 0; i < assets.Count; i++)
            {
                var asset = assets[i];
                if (asset == null) continue;

                string name;
                if (IsSceneObject(asset))
                {
                    name = asset.name;
                }
                else if (TryGetSpriteTarget(asset, out _, out var spriteName))
                {
                    name = spriteName;
                }
                else
                {
                    var path = AssetDatabase.GetAssetPath(asset);
                    if (string.IsNullOrEmpty(path)) continue;
                    name = Path.GetFileNameWithoutExtension(path);
                }

                if (string.IsNullOrEmpty(name)) continue;
                names.Add(name);
            }
            if (names.Count == 0)
                return "";
            return FStringAnalyzeUtils.BuildPatternFromNames(names);
        }

        private static class EnumPatternResolver
        {
            private const string ReservedNumber = "number";
            private const string ReservedVariant = "variant";

            private sealed class EnumCacheEntry
            {
                public System.Type EnumType;
                public object[] Values;
            }

            // matches {EnumType} (numeric) or {(s)EnumType} (string); reserved: number, variant
            private static readonly System.Text.RegularExpressions.Regex EnumPlaceholderRegex =
                new System.Text.RegularExpressions.Regex(@"\{(\(s\))?([^}]+)\}", System.Text.RegularExpressions.RegexOptions.Compiled);

            private static readonly System.Collections.Generic.Dictionary<string, EnumCacheEntry> EnumCache =
                new System.Collections.Generic.Dictionary<string, EnumCacheEntry>();

            private static bool IsReservedPlaceholder(string key)
            {
                string t = key?.Trim();
                return t == ReservedNumber || t == ReservedVariant;
            }

            private static bool ShouldSkipPlaceholder(string key)
            {
                string t = key?.Trim();
                return IsReservedPlaceholder(t) || FSequenceNumberUtils.IsSequencePlaceholder(t);
            }

            /// <summary>True when pattern contains enum placeholder (not number/variant) so slot index drives enum.</summary>
            public static bool PatternUsesEnum(string pattern)
            {
                if (string.IsNullOrEmpty(pattern)) return false;
                foreach (System.Text.RegularExpressions.Match m in EnumPlaceholderRegex.Matches(pattern))
                {
                    string key = m.Groups[2].Value;
                    if (!ShouldSkipPlaceholder(key)) return true;
                }
                return false;
            }

            public static string Resolve(string pattern, int index)
            {
                if (string.IsNullOrEmpty(pattern))
                    throw new System.InvalidOperationException("outputPattern is empty.");

                if (!EnumPlaceholderRegex.IsMatch(pattern))
                    return pattern;

                return EnumPlaceholderRegex.Replace(pattern, match =>
                {
                    var isStringEnum = match.Groups[1].Success;
                    var enumTypeName = match.Groups[2].Value?.Trim();
                    if (string.IsNullOrEmpty(enumTypeName))
                        throw new System.InvalidOperationException("enum placeholder has empty type name.");
                    if (ShouldSkipPlaceholder(enumTypeName))
                        return match.Value;

                    var enumEntry = GetOrCreateEnumEntry(enumTypeName);

                    if (index < 0 || index >= enumEntry.Values.Length)
                        throw new System.InvalidOperationException($"index {index} is out of range for enum {enumEntry.EnumType.FullName}.");

                    var value = enumEntry.Values[index];
                    if (isStringEnum)
                        return value?.ToString() ?? "";
                    var underlyingType = System.Enum.GetUnderlyingType(enumEntry.EnumType);
                    var numeric = System.Convert.ChangeType(value, underlyingType);
                    return System.Convert.ToString(numeric, System.Globalization.CultureInfo.InvariantCulture);
                });
            }

            private static EnumCacheEntry GetOrCreateEnumEntry(string enumTypeName)
            {
                if (string.IsNullOrEmpty(enumTypeName))
                    throw new System.InvalidOperationException("enum type name is empty.");

                enumTypeName = enumTypeName.Trim();

                if (EnumCache.TryGetValue(enumTypeName, out var cachedEntry))
                    return cachedEntry;

                var enumType = FindEnumType(enumTypeName);
                if (enumType == null || !enumType.IsEnum)
                    throw new System.InvalidOperationException($"enum type not found: {enumTypeName}.");

                var valuesArray = System.Enum.GetValues(enumType);
                var filteredValues = new System.Collections.Generic.List<object>(valuesArray.Length);
                for (int i = 0; i < valuesArray.Length; i++)
                {
                    var value = valuesArray.GetValue(i) ?? throw new System.InvalidOperationException($"enum value at index {i} is null.");
                    if (FEnumTypeUtils.ShouldSkipEnumMember(value.ToString())) continue;
                    filteredValues.Add(value);
                }
                var values = filteredValues.ToArray();

                var entry = new EnumCacheEntry
                {
                    EnumType = enumType,
                    Values = values
                };

                EnumCache.Add(enumTypeName, entry);
                return entry;
            }

            private static System.Type FindEnumType(string enumTypeName)
            {
                var type = System.Type.GetType(enumTypeName, false);
                if (type != null && type.IsEnum)
                    return type;

                var assemblies = System.AppDomain.CurrentDomain.GetAssemblies();
                for (int i = 0; i < assemblies.Length; i++)
                {
                    var assembly = assemblies[i] ?? throw new System.InvalidOperationException($"assembly at index {i} is null.");
                    var t = assembly.GetType(enumTypeName, false);
                    if (t != null && t.IsEnum)
                        return t;
                }

                var lastDotIndex = enumTypeName.LastIndexOf('.');
                var shortName = lastDotIndex >= 0 ? enumTypeName.Substring(lastDotIndex + 1) : enumTypeName;

                for (int i = 0; i < assemblies.Length; i++)
                {
                    var assembly = assemblies[i];
                    var types = assembly.GetTypes();
                    for (int j = 0; j < types.Length; j++)
                    {
                        var t = types[j];
                        if (t != null && t.IsEnum && t.Name == shortName)
                            return t;
                    }
                }

                return null;
            }
        }

        private static RenamePlan BuildRenamePlan(List<Object> assets, string input, string output)
        {
            var plan = new RenamePlan();
            bool useEnumSlotIndex = EnumPatternResolver.PatternUsesEnum(output);
            int enumSlotIndex = 0;
            for (int i = 0; i < assets.Count; i++)
            {
                var asset = assets[i];
                if (asset == null)
                {
                    if (useEnumSlotIndex) enumSlotIndex++;
                    continue;
                }

                int idx = useEnumSlotIndex ? enumSlotIndex : i;
                try
                {
                    if (TryGetSpriteTarget(asset, out var spriteTexPath, out var oldSpriteName))
                    {
                        var resolvedSpriteOutput = EnumPatternResolver.Resolve(output, idx);
                        var newSpriteName = FModifyStringUtils.ApplyPattern(oldSpriteName, input, resolvedSpriteOutput, idx);
                        if (string.IsNullOrEmpty(newSpriteName))
                            throw new System.InvalidOperationException("rename result is empty.");
                        if (newSpriteName != oldSpriteName)
                            AddSpriteEntry(plan.SpriteEntries, spriteTexPath, oldSpriteName, newSpriteName);
                        plan.AddRow(oldSpriteName, newSpriteName);
                    }
                    else if (IsSceneObject(asset))
                    {
                        var go = (GameObject)asset;
                        var oldName = go.name;
                        var resolvedOutput = EnumPatternResolver.Resolve(output, idx);
                        var newName = FModifyStringUtils.ApplyPattern(oldName, input, resolvedOutput, idx);
                        if (string.IsNullOrEmpty(newName))
                            throw new System.InvalidOperationException("rename result is empty.");

                        if (newName != oldName)
                        {
                            int instanceId = go.GetInstanceID();
                            if (!plan.SceneEntries.TryGetValue(instanceId, out var entry))
                                plan.SceneEntries.Add(instanceId, new SceneObjectRenameEntry { GameObject = go, OldName = oldName, NewName = newName });
                            else if (entry.NewName != newName)
                                throw new System.InvalidOperationException($"conflicting rename for scene object '{go.name}'.");
                        }
                        plan.AddRow(oldName, newName);
                    }
                    else
                    {
                        var assetPath = AssetDatabase.GetAssetPath(asset);
                        if (string.IsNullOrEmpty(assetPath))
                            throw new System.InvalidOperationException("no asset path.");

                        var oldFileName = Path.GetFileNameWithoutExtension(assetPath);
                        var resolvedAssetOutput = EnumPatternResolver.Resolve(output, idx);
                        var newAssetName = FModifyStringUtils.ApplyPattern(oldFileName, input, resolvedAssetOutput, idx);
                        if (string.IsNullOrEmpty(newAssetName))
                            throw new System.InvalidOperationException("rename result is empty.");

                        if (newAssetName != oldFileName)
                        {
                            if (!plan.AssetEntries.TryGetValue(assetPath, out var entry))
                            {
                                plan.AssetEntries.Add(assetPath, new AssetRenameEntry
                                {
                                    AssetPath = assetPath,
                                    OldName = oldFileName,
                                    NewName = newAssetName
                                });
                            }
                            else if (entry.NewName != newAssetName)
                            {
                                throw new System.InvalidOperationException($"conflicting rename for asset {assetPath}.");
                            }
                        }
                        plan.AddRow(oldFileName, newAssetName);
                    }
                }
                catch (System.Exception ex)
                {
                    plan.AddErrorRow(asset.name, ex.Message);
                }
                if (useEnumSlotIndex) enumSlotIndex++;
            }
            return plan;
        }

        private static RenamePlan BuildFindReplacePlan(List<Object> assets, string find, string replace)
        {
            var plan = new RenamePlan();
            for (int i = 0; i < assets.Count; i++)
            {
                var asset = assets[i];
                if (asset == null)
                    continue;

                try
                {
                    if (TryGetSpriteTarget(asset, out var spriteTexPath, out var oldSpriteName))
                    {
                        var newSpriteName = oldSpriteName.Replace(find, replace);
                        if (newSpriteName != oldSpriteName)
                            AddSpriteEntry(plan.SpriteEntries, spriteTexPath, oldSpriteName, newSpriteName);
                        plan.AddRow(oldSpriteName, newSpriteName);
                    }
                    else if (IsSceneObject(asset))
                    {
                        var go = (GameObject)asset;
                        var oldName = go.name;
                        var newName = oldName.Replace(find, replace);
                        if (newName != oldName)
                        {
                            int instanceId = go.GetInstanceID();
                            if (!plan.SceneEntries.TryGetValue(instanceId, out var entry))
                                plan.SceneEntries.Add(instanceId, new SceneObjectRenameEntry { GameObject = go, OldName = oldName, NewName = newName });
                            else if (entry.NewName != newName)
                                throw new System.InvalidOperationException($"conflicting rename for scene object '{go.name}'.");
                        }
                        plan.AddRow(oldName, newName);
                    }
                    else
                    {
                        var assetPath = AssetDatabase.GetAssetPath(asset);
                        if (string.IsNullOrEmpty(assetPath))
                            throw new System.InvalidOperationException("no asset path.");

                        var oldFileName = Path.GetFileNameWithoutExtension(assetPath);
                        var newFileName = oldFileName.Replace(find, replace);
                        if (newFileName != oldFileName)
                        {
                            if (!plan.AssetEntries.TryGetValue(assetPath, out var assetEntry))
                            {
                                plan.AssetEntries.Add(assetPath, new AssetRenameEntry
                                {
                                    AssetPath = assetPath,
                                    OldName = oldFileName,
                                    NewName = newFileName
                                });
                            }
                            else if (assetEntry.NewName != newFileName)
                            {
                                throw new System.InvalidOperationException($"conflicting rename for asset {assetPath}.");
                            }
                        }
                        plan.AddRow(oldFileName, newFileName);
                    }
                }
                catch (System.Exception ex)
                {
                    plan.AddErrorRow(asset.name, ex.Message);
                }
            }
            return plan;
        }

        private static void ApplyAssetRenames(Dictionary<string, AssetRenameEntry> assetEntries, FAssetRevertService.RevertOperation op)
        {
            if (assetEntries.Count == 0)
                return;

            var renamed = new List<AssetRenameEntry>(assetEntries.Count);
            AssetDatabase.StartAssetEditing();
            try
            {
                foreach (var entry in assetEntries.Values)
                {
                    var renameError = AssetDatabase.RenameAsset(entry.AssetPath, entry.NewName);
                    if (!string.IsNullOrEmpty(renameError))
                    {
                        Debug.LogError($"[FRenameTool] Không đổi tên được '{entry.AssetPath}': {renameError}");
                        continue;
                    }
                    renamed.Add(entry);
                    FAssetRevertService.RecordAssetRename(op, GetPathAfterRename(entry), entry.OldName);
                }
            }
            finally
            {
                AssetDatabase.StopAssetEditing();
            }

            foreach (var entry in renamed)
                TryRenameTextureSprites(entry);
        }

        private static string GetPathAfterRename(AssetRenameEntry entry)
        {
            var dir = Path.GetDirectoryName(entry.AssetPath)?.Replace('\\', '/') ?? "";
            var ext = Path.GetExtension(entry.AssetPath);
            return string.IsNullOrEmpty(dir) ? entry.NewName + ext : dir + "/" + entry.NewName + ext;
        }

        private static void TryRenameTextureSprites(AssetRenameEntry entry)
        {
            var dir = Path.GetDirectoryName(entry.AssetPath)?.Replace('\\', '/') ?? "";
            var ext = Path.GetExtension(entry.AssetPath);
            var newPath = string.IsNullOrEmpty(dir)
                ? entry.NewName + ext
                : dir + "/" + entry.NewName + ext;

            var importer = AssetImporter.GetAtPath(newPath) as TextureImporter;
            if (importer == null) return;

            if (importer.spriteImportMode == SpriteImportMode.Multiple ||
                importer.spriteImportMode == SpriteImportMode.Polygon)
            {
                // Rename via the sprite data provider so name↔fileId stays consistent (keeps references intact).
                var renames = AssetDatabase.LoadAllAssetsAtPath(newPath)
                    .OfType<Sprite>()
                    .Where(s => s.name.Contains(entry.OldName))
                    .Select(s => (s.name, s.name.Replace(entry.OldName, entry.NewName)))
                    .ToList();
                if (renames.Count > 0)
                    FSpriteRenameUtils.RenameSprites(newPath, renames, saveAndReimport: true);
            }
            else if (importer.spriteImportMode == SpriteImportMode.Single)
            {
                // Force reimport so Unity re-derives the sprite sub-asset name from the new file name.
                importer.SaveAndReimport();
            }
        }

        private static void ApplySpriteRenames(Dictionary<string, SpriteRenameEntry> spriteEntries, FAssetRevertService.RevertOperation op)
        {
            if (spriteEntries.Count == 0)
                return;

            AssetDatabase.StartAssetEditing();
            try
            {
                foreach (var group in spriteEntries.Values.GroupBy(e => e.TexturePath))
                {
                    var renames = group.Select(e => (e.OldName, e.NewName)).ToList();
                    if (FSpriteRenameUtils.RenameSprites(group.Key, renames, saveAndReimport: true) > 0)
                        foreach (var e in group)
                            FAssetRevertService.RecordSpriteRename(op, e.TexturePath, e.OldName, e.NewName);
                }
            }
            finally
            {
                AssetDatabase.StopAssetEditing();
            }
        }

        private static void ApplySceneObjectRenames(Dictionary<int, SceneObjectRenameEntry> sceneEntries)
        {
            if (sceneEntries.Count == 0)
                return;

            Undo.SetCurrentGroupName("Rename Scene GameObjects");
            int group = Undo.GetCurrentGroup();
            foreach (var entry in sceneEntries.Values)
            {
                Undo.RecordObject(entry.GameObject, "Rename");
                entry.GameObject.name = entry.NewName;
            }
            Undo.CollapseUndoOperations(group);
        }
    }
}
