using Sirenix.OdinInspector;
using System.Collections.Generic;
using System.IO;
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

        [PropertySpace(SpaceBefore = 10)]
        [TabGroup("RenameMode", "Change Pattern")]
        [LabelText("Input Pattern"), Tooltip("use {number}, {variant}, {EnumType} (numeric), {(s)EnumType} (string)")]
        [ShowInInspector, ReadOnly, EnableGUI] private string inputPattern = "";

        
        [PropertySpace(SpaceBefore = 6, SpaceAfter = 2)]
        [TabGroup("RenameMode", "Change Pattern")]
        [LabelText("Output Pattern"), Tooltip("use {number}, {variant}, {start:step}, {EnumType} (numeric), {(s)EnumType} (string)")]
        public string outputPattern = "";

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
        [Button("Analyze Pattern", ButtonSizes.Medium)]
        private void AnalyzePattern()
        {
            inputPattern = BuildPatternFromAssets(TargetAssets);
        }

        [TabGroup("RenameMode", "Change Pattern")]
        [Button("Apply Rename", ButtonSizes.Large)]
        private void ApplyRename()
        {
            if ((TargetAssets?.Count ?? 0) == 0)
                throw new System.InvalidOperationException("TargetAssets is empty.");
            if (string.IsNullOrEmpty(inputPattern))
                throw new System.InvalidOperationException("inputPattern is empty.");
            if (string.IsNullOrEmpty(outputPattern))
                throw new System.InvalidOperationException("outputPattern is empty.");

            var assetEntries = new Dictionary<string, AssetRenameEntry>(TargetAssets.Count);
            var sceneEntries = new Dictionary<int, SceneObjectRenameEntry>(TargetAssets.Count);
            BuildRenamePlan(TargetAssets, inputPattern, outputPattern, assetEntries, sceneEntries);

            if (assetEntries.Count == 0 && sceneEntries.Count == 0)
                return;

            ApplyAssetRenames(assetEntries);
            ApplySceneObjectRenames(sceneEntries);
            RefreshInputPattern();
        }

        [PropertySpace(SpaceBefore = 10)]
        [TabGroup("RenameMode", "Find & Replace")]
        [LabelText("Find")]
        public string findString = "";

        [PropertySpace(SpaceBefore = 6, SpaceAfter = 6)]
        [TabGroup("RenameMode", "Find & Replace")]
        [LabelText("Replace With")]
        public string replaceString = "";

        [TabGroup("RenameMode", "Find & Replace")]
        [Button("Apply Find & Replace", ButtonSizes.Large)]
        private void ApplyFindAndReplace()
        {
            if ((TargetAssets?.Count ?? 0) == 0)
                throw new System.InvalidOperationException("TargetAssets is empty.");
            if (string.IsNullOrEmpty(findString))
                throw new System.InvalidOperationException("findString is empty.");

            var assetEntries = new Dictionary<string, AssetRenameEntry>(TargetAssets.Count);
            var sceneEntries = new Dictionary<int, SceneObjectRenameEntry>(TargetAssets.Count);
            BuildFindReplacePlan(TargetAssets, findString, replaceString ?? "", assetEntries, sceneEntries);

            if (assetEntries.Count == 0 && sceneEntries.Count == 0)
                return;

            ApplyAssetRenames(assetEntries);
            ApplySceneObjectRenames(sceneEntries);
            RefreshInputPattern();
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
        }

        private static bool IsSceneObject(Object obj)
        {
            return obj is GameObject go && !EditorUtility.IsPersistent(go);
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

        private static void BuildRenamePlan(
            List<Object> assets,
            string input,
            string output,
            Dictionary<string, AssetRenameEntry> assetEntries,
            Dictionary<int, SceneObjectRenameEntry> sceneEntries)
        {
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

                if (IsSceneObject(asset))
                {
                    var go = (GameObject)asset;
                    var oldName = go.name;
                    int indexForPattern = useEnumSlotIndex ? enumSlotIndex : i;
                    var resolvedOutput = EnumPatternResolver.Resolve(output, indexForPattern);
                    var newName = FModifyStringUtils.ApplyPattern(oldName, input, resolvedOutput, indexForPattern);
                    if (string.IsNullOrEmpty(newName))
                        throw new System.InvalidOperationException($"rename result is empty at index {i}.");

                    if (newName != oldName)
                    {
                        int instanceId = go.GetInstanceID();
                        if (!sceneEntries.TryGetValue(instanceId, out var entry))
                            sceneEntries.Add(instanceId, new SceneObjectRenameEntry { GameObject = go, OldName = oldName, NewName = newName });
                        else if (entry.NewName != newName)
                            throw new System.InvalidOperationException($"conflicting rename for scene object '{go.name}'.");
                    }
                    if (useEnumSlotIndex) enumSlotIndex++;
                    continue;
                }

                var assetPath = AssetDatabase.GetAssetPath(asset);
                if (string.IsNullOrEmpty(assetPath))
                {
                    if (useEnumSlotIndex) enumSlotIndex++;
                    Debug.LogWarning($"[FRenameTool] Skipping TargetAssets[{i}] (no asset path).");
                    continue;
                }

                var oldFileName = Path.GetFileNameWithoutExtension(assetPath);
                int indexForAsset = useEnumSlotIndex ? enumSlotIndex : i;
                var resolvedAssetOutput = EnumPatternResolver.Resolve(output, indexForAsset);
                var newAssetName = FModifyStringUtils.ApplyPattern(oldFileName, input, resolvedAssetOutput, indexForAsset);
                if (string.IsNullOrEmpty(newAssetName))
                    throw new System.InvalidOperationException($"rename result is empty at index {i}.");

                if (newAssetName != oldFileName)
                {
                    if (!assetEntries.TryGetValue(assetPath, out var entry))
                    {
                        assetEntries.Add(assetPath, new AssetRenameEntry
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
                if (useEnumSlotIndex) enumSlotIndex++;
            }
        }

        private static void BuildFindReplacePlan(
            List<Object> assets,
            string find,
            string replace,
            Dictionary<string, AssetRenameEntry> assetEntries,
            Dictionary<int, SceneObjectRenameEntry> sceneEntries)
        {
            for (int i = 0; i < assets.Count; i++)
            {
                var asset = assets[i];
                if (asset == null)
                {
                    Debug.LogWarning($"[FRenameTool] Skipping null at TargetAssets[{i}].");
                    continue;
                }

                if (IsSceneObject(asset))
                {
                    var go = (GameObject)asset;
                    var oldName = go.name;
                    var newName = oldName.Replace(find, replace);
                    if (newName != oldName)
                    {
                        int instanceId = go.GetInstanceID();
                        if (!sceneEntries.TryGetValue(instanceId, out var entry))
                            sceneEntries.Add(instanceId, new SceneObjectRenameEntry { GameObject = go, OldName = oldName, NewName = newName });
                        else if (entry.NewName != newName)
                            throw new System.InvalidOperationException($"conflicting rename for scene object '{go.name}'.");
                    }
                    continue;
                }

                var assetPath = AssetDatabase.GetAssetPath(asset);
                if (string.IsNullOrEmpty(assetPath))
                {
                    Debug.LogWarning($"[FRenameTool] Skipping TargetAssets[{i}] (no asset path).");
                    continue;
                }

                var oldFileName = Path.GetFileNameWithoutExtension(assetPath);
                var newFileName = oldFileName.Replace(find, replace);

                if (newFileName == oldFileName)
                    continue;

                if (!assetEntries.TryGetValue(assetPath, out var assetEntry))
                {
                    assetEntries.Add(assetPath, new AssetRenameEntry
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
        }

        private static void ApplyAssetRenames(Dictionary<string, AssetRenameEntry> assetEntries)
        {
            if (assetEntries.Count == 0)
                return;

            AssetDatabase.StartAssetEditing();
            try
            {
                foreach (var entry in assetEntries.Values)
                {
                    var renameError = AssetDatabase.RenameAsset(entry.AssetPath, entry.NewName);
                    if (!string.IsNullOrEmpty(renameError))
                        throw new System.Exception(renameError);
                }
            }
            finally
            {
                AssetDatabase.StopAssetEditing();
            }

            foreach (var entry in assetEntries.Values)
                TryRenameTextureSprites(entry);
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

            if (importer.spriteImportMode == SpriteImportMode.Multiple)
            {
                var spritesheet = importer.spritesheet;
                bool changed = false;
                for (int i = 0; i < spritesheet.Length; i++)
                {
                    if (!spritesheet[i].name.Contains(entry.OldName)) continue;
                    var data = spritesheet[i];
                    data.name = data.name.Replace(entry.OldName, entry.NewName);
                    spritesheet[i] = data;
                    changed = true;
                }
                if (changed)
                {
                    importer.spritesheet = spritesheet;
                    importer.SaveAndReimport();
                }
            }
            else if (importer.spriteImportMode == SpriteImportMode.Single)
            {
                // Force reimport so Unity re-derives the sprite sub-asset name from the new file name.
                importer.SaveAndReimport();
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
