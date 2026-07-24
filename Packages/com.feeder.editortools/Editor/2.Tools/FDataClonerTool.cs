using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using Sirenix.OdinInspector;
using UnityEditor;
using UnityEngine;

namespace Feeder
{
    public sealed class FDataClonerTool : FBaseTool
    {
        protected override string GetDescription() =>
            "Clone collection field (Dictionary, List, HashSet) từ một ScriptableObject sang cái khác. " +
            "Dictionary<Enum>: ghép theo tên enum (cross-enum). List/HashSet: copy toàn bộ phần tử.";

        [BoxGroup("Source"), AssetSelector(Paths = "Assets")]
        [OnValueChanged(nameof(ClearSourceField)), LabelText("Source ScriptableObject")]
        [ShowInInspector]
        public ScriptableObject SourceSO
        {
            get => FDataPersistenceService.GetOrCreateDataContainer().DataClonerSourceSO;
            set
            {
                var c = FDataPersistenceService.GetOrCreateDataContainer();
                c.DataClonerSourceSO = value;
                FDataPersistenceService.SaveData(c);
            }
        }

        [BoxGroup("Source"), ValueDropdown(nameof(GetSourceFieldOptions))]
        [ShowIf("@SourceSO != null"), LabelText("Source Field")]
        [ShowInInspector]
        public string SourceFieldName
        {
            get => FToolPrefs.GetString(nameof(FDataClonerTool), nameof(SourceFieldName), null);
            set => FToolPrefs.SetString(nameof(FDataClonerTool), nameof(SourceFieldName), value);
        }

        [BoxGroup("Destination"), AssetSelector(Paths = "Assets")]
        [OnValueChanged(nameof(ClearDestinationField)), LabelText("Destination ScriptableObject")]
        [ShowInInspector]
        public ScriptableObject DestinationSO
        {
            get => FDataPersistenceService.GetOrCreateDataContainer().DataClonerDestinationSO;
            set
            {
                var c = FDataPersistenceService.GetOrCreateDataContainer();
                c.DataClonerDestinationSO = value;
                FDataPersistenceService.SaveData(c);
            }
        }

        [BoxGroup("Destination"), ValueDropdown(nameof(GetDestinationFieldOptions))]
        [ShowIf("@DestinationSO != null"), LabelText("Destination Field")]
        [ShowInInspector]
        public string DestinationFieldName
        {
            get => FToolPrefs.GetString(nameof(FDataClonerTool), nameof(DestinationFieldName), null);
            set => FToolPrefs.SetString(nameof(FDataClonerTool), nameof(DestinationFieldName), value);
        }

        [BoxGroup("Settings"), LabelText("Override Existing Values")]
        [Tooltip("Dictionary: bỏ qua key đã có value non-null. List/HashSet: clear trước khi copy.")]
        [ShowInInspector]
        public bool OverrideExistingValues
        {
            get => FToolPrefs.GetBool(nameof(FDataClonerTool), nameof(OverrideExistingValues), true);
            set => FToolPrefs.SetBool(nameof(FDataClonerTool), nameof(OverrideExistingValues), value);
        }

        private void ClearSourceField() => SourceFieldName = null;
        private void ClearDestinationField() => DestinationFieldName = null;

        [Button(ButtonSizes.Large)]
        [GUIColor(0.3f, 0.8f, 1f)]
        [EnableIf(nameof(CanClone))]
        public void CloneData()
        {
            if (!TryGetFields(out var srcField, out var dstField)) return;

            var srcValue = srcField.GetValue(SourceSO);
            if (srcValue == null)
            {
                Debug.LogError("Source field value is null!");
                return;
            }

            Undo.RegisterCompleteObjectUndo(DestinationSO, "Clone Collection Data");

            var srcType = srcField.FieldType;
            var dstType = dstField.FieldType;
            bool success;

            if (IsDictionaryType(srcType) && IsDictionaryType(dstType))
                success = CloneDictionary(srcField, dstField, srcValue);
            else if (IsListType(srcType) && IsListType(dstType))
                success = CloneSimpleCollection(dstField, srcValue, "List");
            else if (IsHashSetType(srcType) && IsHashSetType(dstType))
                success = CloneSimpleCollection(dstField, srcValue, "HashSet");
            else
            {
                Debug.LogError($"Incompatible types: {PrettyType(srcType)} → {PrettyType(dstType)}");
                return;
            }

            if (success)
            {
                EditorUtility.SetDirty(DestinationSO);
                AssetDatabase.SaveAssets();
            }
        }

        private bool CloneDictionary(FieldInfo srcField, FieldInfo dstField, object srcValue)
        {
            var srcType = srcField.FieldType;
            var dstType = dstField.FieldType;
            var srcKeyType = srcType.GetGenericArguments()[0];
            var srcValType = srcType.GetGenericArguments()[1];
            var dstKeyType = dstType.GetGenericArguments()[0];
            var dstValType = dstType.GetGenericArguments()[1];

            bool sameKey = srcKeyType == dstKeyType;
            bool crossEnum = srcKeyType.IsEnum && dstKeyType.IsEnum && !sameKey;

            if (!sameKey && !crossEnum)
            {
                Debug.LogError($"Incompatible key types: {srcKeyType.Name} → {dstKeyType.Name}");
                return false;
            }

            if (!dstValType.IsAssignableFrom(srcValType))
            {
                Debug.LogError($"Incompatible value types: {srcValType.Name} → {dstValType.Name}");
                return false;
            }

            var dstValue = EnsureInstance(dstField, dstType);
            var dstSetItem = dstType.GetMethod("set_Item");
            var dstContainsKey = dstType.GetMethod("ContainsKey");
            var dstGetItem = dstType.GetMethod("get_Item");
            var srcDict = (IDictionary)srcValue;

            int cloned = 0, skipped = 0;

            if (sameKey)
            {
                foreach (DictionaryEntry entry in srcDict)
                {
                    if (ShouldSkip(dstContainsKey, dstGetItem, dstValue, entry.Key)) { skipped++; continue; }
                    dstSetItem.Invoke(dstValue, new[] { entry.Key, entry.Value });
                    cloned++;
                }
            }
            else
            {
                var dstEnumsByName = Enum.GetValues(dstKeyType).Cast<Enum>().ToDictionary(e => e.ToString());
                foreach (DictionaryEntry entry in srcDict)
                {
                    if (entry.Value == null) continue;
                    if (!dstEnumsByName.TryGetValue(entry.Key.ToString(), out var dstEnum))
                    {
                        Debug.LogWarning($"<color=yellow>No match for '{entry.Key}' in {dstKeyType.Name}</color>");
                        continue;
                    }
                    if (ShouldSkip(dstContainsKey, dstGetItem, dstValue, dstEnum)) { skipped++; continue; }
                    dstSetItem.Invoke(dstValue, new[] { (object)dstEnum, entry.Value });
                    Debug.Log($"<color=cyan>'{entry.Key}' → '{dstEnum}'</color>");
                    cloned++;
                }
            }

            Debug.Log($"<color=green>Dictionary clone done! Cloned: {cloned}, Skipped: {skipped}</color>");
            return true;
        }

        private bool CloneSimpleCollection(FieldInfo dstField, object srcValue, string label)
        {
            var dstType = dstField.FieldType;
            var dstValue = EnsureInstance(dstField, dstType);

            if (OverrideExistingValues)
                dstType.GetMethod("Clear").Invoke(dstValue, null);

            var addMethod = dstType.GetMethod("Add");
            int count = 0;
            foreach (var item in (IEnumerable)srcValue)
            {
                addMethod.Invoke(dstValue, new[] { item });
                count++;
            }

            Debug.Log($"<color=green>{label} clone done! Copied: {count}</color>");
            return true;
        }

        private object EnsureInstance(FieldInfo field, Type type)
        {
            var value = field.GetValue(DestinationSO);
            if (value != null) return value;
            value = Activator.CreateInstance(type);
            field.SetValue(DestinationSO, value);
            return value;
        }

        private bool ShouldSkip(MethodInfo containsKey, MethodInfo getItem, object dict, object key)
        {
            if (OverrideExistingValues) return false;
            var has = (bool)containsKey.Invoke(dict, new[] { key });
            if (!has) return false;
            return getItem.Invoke(dict, new[] { key }) != null;
        }

        private bool CanClone =>
            SourceSO != null && DestinationSO != null &&
            !string.IsNullOrEmpty(SourceFieldName) && !string.IsNullOrEmpty(DestinationFieldName);

        private bool TryGetFields(out FieldInfo srcField, out FieldInfo dstField)
        {
            srcField = GetFieldInfo(SourceSO, SourceFieldName);
            dstField = GetFieldInfo(DestinationSO, DestinationFieldName);
            if (srcField != null && dstField != null) return true;
            Debug.LogError("Could not find one or both fields!");
            return false;
        }

        private static FieldInfo GetFieldInfo(ScriptableObject so, string fieldName) =>
            so?.GetType().GetField(fieldName, BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);

        private IEnumerable<ValueDropdownItem<string>> GetSourceFieldOptions() =>
            GetFieldOptions(SourceSO, null);

        private IEnumerable<ValueDropdownItem<string>> GetDestinationFieldOptions()
        {
            Type srcType = null;
            if (SourceSO != null && !string.IsNullOrEmpty(SourceFieldName))
                srcType = GetFieldInfo(SourceSO, SourceFieldName)?.FieldType;
            return GetFieldOptions(DestinationSO, srcType);
        }

        private static IEnumerable<ValueDropdownItem<string>> GetFieldOptions(ScriptableObject so, Type filterCompatibleWith)
        {
            if (so == null) yield break;
            foreach (var f in so.GetType().GetFields(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance))
            {
                if (!IsCollectionType(f.FieldType)) continue;
                if (filterCompatibleWith != null && !AreCompatible(filterCompatibleWith, f.FieldType)) continue;
                yield return new ValueDropdownItem<string>($"{f.Name}  [{PrettyType(f.FieldType)}]", f.Name);
            }
        }

        private static bool AreCompatible(Type src, Type dst)
        {
            if (IsDictionaryType(src) && IsDictionaryType(dst))
            {
                var srcKey = src.GetGenericArguments()[0];
                var dstKey = dst.GetGenericArguments()[0];
                var srcVal = src.GetGenericArguments()[1];
                var dstVal = dst.GetGenericArguments()[1];
                bool compatKey = srcKey == dstKey || (srcKey.IsEnum && dstKey.IsEnum);
                return compatKey && dstVal.IsAssignableFrom(srcVal);
            }
            if (IsListType(src) && IsListType(dst))
                return dst.GetGenericArguments()[0].IsAssignableFrom(src.GetGenericArguments()[0]);
            if (IsHashSetType(src) && IsHashSetType(dst))
                return dst.GetGenericArguments()[0].IsAssignableFrom(src.GetGenericArguments()[0]);
            return false;
        }

        private static bool IsDictionaryType(Type t) =>
            t.IsGenericType && t.GetGenericTypeDefinition() == typeof(Dictionary<,>);

        private static bool IsListType(Type t) =>
            t.IsGenericType && t.GetGenericTypeDefinition() == typeof(List<>);

        private static bool IsHashSetType(Type t) =>
            t.IsGenericType && t.GetGenericTypeDefinition() == typeof(HashSet<>);

        private static bool IsCollectionType(Type t) =>
            IsDictionaryType(t) || IsListType(t) || IsHashSetType(t);

        private static string PrettyType(Type t)
        {
            if (!t.IsGenericType) return t.Name;
            var name = t.Name.Split('`')[0];
            var args = string.Join(", ", t.GetGenericArguments().Select(a => a.Name));
            return $"{name}<{args}>";
        }
    }
}
