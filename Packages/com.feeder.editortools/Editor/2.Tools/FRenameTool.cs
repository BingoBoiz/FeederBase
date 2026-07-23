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
            var spriteEntries = new Dictionary<string, SpriteRenameEntry>(TargetAssets.Count);
            BuildRenamePlan(TargetAssets, inputPattern, outputPattern, assetEntries, sceneEntries, spriteEntries);

            if (assetEntries.Count == 0 && sceneEntries.Count == 0 && spriteEntries.Count == 0)
                return;

            ApplyAssetRenames(assetEntries);
            ApplySpriteRenames(spriteEntries);
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
            var spriteEntries = new Dictionary<string, SpriteRenameEntry>(TargetAssets.Count);
            BuildFindReplacePlan(TargetAssets, findString, replaceString ?? "", assetEntries, sceneEntries, spriteEntries);

            if (assetEntries.Count == 0 && sceneEntries.Count == 0 && spriteEntries.Count == 0)
                return;

            ApplyAssetRenames(assetEntries);
            ApplySpriteRenames(spriteEntries);
            ApplySceneObjectRenames(sceneEntries);
            RefreshInputPattern();
        }

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
        [Button("Sync Sprite Name To Texture Name", ButtonSizes.Large)]
        private void SyncSpriteNamesToTextureNames()
        {
            if ((TargetAssets?.Count ?? 0) == 0)
                throw new System.InvalidOperationException("TargetAssets is empty.");

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

        private static void BuildRenamePlan(
            List<Object> assets,
            string input,
            string output,
            Dictionary<string, AssetRenameEntry> assetEntries,
            Dictionary<int, SceneObjectRenameEntry> sceneEntries,
            Dictionary<string, SpriteRenameEntry> spriteEntries)
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

                if (TryGetSpriteTarget(asset, out var spriteTexPath, out var oldSpriteName))
                {
                    int idx = useEnumSlotIndex ? enumSlotIndex : i;
                    var resolvedSpriteOutput = EnumPatternResolver.Resolve(output, idx);
                    var newSpriteName = FModifyStringUtils.ApplyPattern(oldSpriteName, input, resolvedSpriteOutput, idx);
                    if (string.IsNullOrEmpty(newSpriteName))
                        throw new System.InvalidOperationException($"rename result is empty at index {i}.");
                    if (newSpriteName != oldSpriteName)
                        AddSpriteEntry(spriteEntries, spriteTexPath, oldSpriteName, newSpriteName);
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
            Dictionary<int, SceneObjectRenameEntry> sceneEntries,
            Dictionary<string, SpriteRenameEntry> spriteEntries)
        {
            for (int i = 0; i < assets.Count; i++)
            {
                var asset = assets[i];
                if (asset == null)
                {
                    Debug.LogWarning($"[FRenameTool] Skipping null at TargetAssets[{i}].");
                    continue;
                }

                if (TryGetSpriteTarget(asset, out var spriteTexPath, out var oldSpriteName))
                {
                    var newSpriteName = oldSpriteName.Replace(find, replace);
                    if (newSpriteName != oldSpriteName)
                        AddSpriteEntry(spriteEntries, spriteTexPath, oldSpriteName, newSpriteName);
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

        private static void ApplySpriteRenames(Dictionary<string, SpriteRenameEntry> spriteEntries)
        {
            if (spriteEntries.Count == 0)
                return;

            AssetDatabase.StartAssetEditing();
            try
            {
                foreach (var group in spriteEntries.Values.GroupBy(e => e.TexturePath))
                {
                    var renames = group.Select(e => (e.OldName, e.NewName)).ToList();
                    FSpriteRenameUtils.RenameSprites(group.Key, renames, saveAndReimport: true);
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
