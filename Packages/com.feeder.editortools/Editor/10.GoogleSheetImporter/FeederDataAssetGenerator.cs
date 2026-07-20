using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Reflection;
using NabaGame.Core.Runtime.Extensions;
using UnityEditor;
using UnityEngine;

namespace Feeder
{
    public static class FeederDataAssetGenerator
    {
        public static void GenerateClass(string sheetName, string[,] cells, List<string> rawFields,
            string assetFolderPath, string spriteAssetFolderPath, string prefabFolderPath)
        {
            sheetName = sheetName.Replace(" ", "");
            string typeName = cells[0, 0];
            string fileName = "Raw" + sheetName;
            string dataClassName = typeName + "Data";
            Type assetType = GetTypeByName(dataClassName);
            string path = $"Assets/{assetFolderPath}";

            if (assetType == null)
            {
                EditorUtility.DisplayDialog("Error",
                    "Cannot find the script " + dataClassName + ", please generate the script first.", "close");
                return;
            }

            UnityEngine.Object dataHolder = AssetDatabase.LoadAssetAtPath(path + $"/{fileName}.asset", assetType);
            if (dataHolder == null)
            {
                dataHolder = ScriptableObject.CreateInstance(assetType);
                AssetDatabase.CreateAsset(dataHolder, path + $"/{fileName}.asset");
            }

            string listFieldName = $"{typeName[0].ToString().ToLower()}{typeName.Substring(1)}s";
            FieldInfo dataListField = assetType.GetField(listFieldName);
            object dataList = dataListField.GetValue(dataHolder);
            dataList.GetType().GetMethod("Clear").Invoke(dataList, null);

            Type dataType = GetTypeByName(typeName);
            FieldInfo[] fields = dataType.GetFields();
            if (fields == null || fields.Length <= 0)
            {
                EditorUtility.DisplayDialog("Generate Asset", $"Failed ! {typeName} have no field", "close");
                return;
            }

            if (fields.Length > rawFields.Count)
            {
                EditorUtility.DisplayDialog("Generate Asset",
                    $"Failed ! scripts {typeName} fields count does not match with data", "close");
                return;
            }

            int totalRow = cells.GetLength(1);
            if (totalRow < 3)
            {
                EditorUtility.DisplayDialog("Generate Asset", "Failed ! data not found", "close");
                return;
            }

            string[] defaultVals = new string[fields.Length];
            MethodInfo addMethod = dataList.GetType().GetMethod("Add");
            Dictionary<string, UnityEngine.Object> assetCache = new Dictionary<string, UnityEngine.Object>();
            int totalRows = cells.GetLength(1);
            try
            {
                for (int row = 2; row < totalRows; row++)
                {
                    EditorUtility.DisplayProgressBar("Generating Assets",
                        $"Row {row - 1}/{totalRows - 2}", (float)(row - 2) / (totalRows - 2));
                    object data = Activator.CreateInstance(dataType);
                    for (int col = 0; col < fields.Length; col++)
                    {
                        if (col > fields.Length)
                        {
                            continue;
                        }

                        string cell = cells[col, row];
                        if (cell.IsNullOrWhitespace())
                        {
                            if (defaultVals[col].IsNullOrWhitespace())
                            {
                                EditorUtility.DisplayDialog("Generate Asset", $"Failed ! cell[{row},{col}] cannot be null",
                                    "close");
                                return;
                            }

                            cell = defaultVals[col];
                        }
                        else
                        {
                            defaultVals[col] = cell;
                        }

                        FieldInfo fieldInfo = fields[col];
                        object value = GetFieldValue(fieldInfo, cell, spriteAssetFolderPath, prefabFolderPath, assetCache);
                        fieldInfo.SetValue(data, value);
                    }

                    addMethod.Invoke(dataList, new object[] { data });
                }
            }
            finally
            {
                EditorUtility.ClearProgressBar();
            }

            EditorUtility.SetDirty(dataHolder);
            AssetDatabase.SaveAssets();
            AssetDatabase.Refresh();
            Debug.Log($"Success !! {typeName} data is generated !!");
        }

        private static object GetFieldValue(FieldInfo info, string rawValue, string spriteAssetFolderPath, string prefabFolderPath, Dictionary<string, UnityEngine.Object> assetCache)
        {
            object value = null;
            Type fieldType = info.FieldType;
            if (fieldType == typeof(byte))
            {
                if (byte.TryParse(rawValue, out byte rs))
                {
                    value = rs;
                }
            }
            else if (fieldType == typeof(short))
            {
                if (short.TryParse(rawValue, out short rs))
                {
                    value = rs;
                }
            }
            else if (fieldType == typeof(ushort))
            {
                if (ushort.TryParse(rawValue, out ushort rs))
                {
                    value = rs;
                }
            }
            else if (fieldType == typeof(int))
            {
                if (TryParseSheetInt(rawValue, out int rs))
                    value = rs;
            }
            else if (fieldType == typeof(uint))
            {
                if (uint.TryParse(rawValue, out uint rs))
                {
                    value = rs;
                }
            }
            else if (fieldType == typeof(long))
            {
                if (long.TryParse(rawValue, out long rs))
                {
                    value = rs;
                }
            }
            else if (fieldType == typeof(ulong))
            {
                if (ulong.TryParse(rawValue, out ulong rs))
                {
                    value = rs;
                }
            }
            else if (fieldType == typeof(double))
            {
                if (double.TryParse(rawValue, NumberStyles.Float, CultureInfo.InvariantCulture, out double rs))
                {
                    value = rs;
                }
            }
            else if (fieldType == typeof(float))
            {
                if (float.TryParse(rawValue, NumberStyles.Float, CultureInfo.InvariantCulture, out float rs))
                {
                    value = rs;
                }
            }
            else if (fieldType == typeof(bool))
            {
                if (Boolean.TryParse(rawValue.ToUpper(), out bool rs))
                {
                    value = rs;
                }
            }
            else if (fieldType == typeof(string))
            {
                value = rawValue;
            }
            else if (fieldType == typeof(List<byte>))
            {
                string[] elementList = rawValue.Split(',');
                List<byte> values = new List<byte>(elementList.Length);
                for (int index = 0; index < elementList.Length; index++)
                {
                    values.Add(byte.Parse(elementList[index]));
                }

                value = values;
            }
            else if (fieldType == typeof(List<short>))
            {
                string[] elementList = rawValue.Split(',');
                List<short> values = new List<short>(elementList.Length);
                for (int index = 0; index < elementList.Length; index++)
                {
                    values.Add(short.Parse(elementList[index]));
                }

                value = values;
            }
            else if (fieldType == typeof(List<ushort>))
            {
                string[] elementList = rawValue.Split(',');
                List<ushort> values = new List<ushort>(elementList.Length);
                for (int index = 0; index < elementList.Length; index++)
                {
                    values.Add(ushort.Parse(elementList[index]));
                }

                value = values;
            }
            else if (fieldType == typeof(List<int>))
            {
                string[] elementList = rawValue.Split(',');
                List<int> values = new List<int>(elementList.Length);
                for (int index = 0; index < elementList.Length; index++)
                {
                    values.Add(int.Parse(elementList[index]));
                }

                value = values;
            }
            else if (fieldType == typeof(List<uint>))
            {
                string[] elementList = rawValue.Split(',');
                List<uint> values = new List<uint>(elementList.Length);
                for (int index = 0; index < elementList.Length; index++)
                {
                    values.Add(uint.Parse(elementList[index]));
                }

                value = values;
            }
            else if (fieldType == typeof(List<long>))
            {
                string[] elementList = rawValue.Split(',');
                List<long> values = new List<long>(elementList.Length);
                for (int index = 0; index < elementList.Length; index++)
                {
                    values.Add(long.Parse(elementList[index]));
                }

                value = values;
            }
            else if (fieldType == typeof(List<ulong>))
            {
                string[] elementList = rawValue.Split(',');
                List<ulong> values = new List<ulong>(elementList.Length);
                for (int index = 0; index < elementList.Length; index++)
                {
                    values.Add(ulong.Parse(elementList[index]));
                }

                value = values;
            }
            else if (fieldType == typeof(List<float>))
            {
                string[] elementList = rawValue.Split(',');
                List<float> values = new List<float>(elementList.Length);
                for (int index = 0; index < elementList.Length; index++)
                {
                    values.Add(float.Parse(elementList[index], NumberStyles.Float, CultureInfo.InvariantCulture));
                }

                value = values;
            }
            else if (fieldType == typeof(List<double>))
            {
                string[] elementList = rawValue.Split(',');
                List<double> values = new List<double>(elementList.Length);
                for (int index = 0; index < elementList.Length; index++)
                {
                    values.Add(double.Parse(elementList[index], NumberStyles.Float, CultureInfo.InvariantCulture));
                }

                value = values;
            }
            else if (fieldType == typeof(Sprite))
            {
                value = LoadAssetByNameInFolder<Sprite>(rawValue, spriteAssetFolderPath, "Sprite", assetCache);
            }
            else if (fieldType == typeof(GameObject))
            {
                value = LoadPrefabByNameInFolder(rawValue, prefabFolderPath, assetCache);
            }
            else if (typeof(Component).IsAssignableFrom(fieldType))
            {
                GameObject prefab = LoadPrefabByNameInFolder(rawValue, prefabFolderPath, assetCache);
                value = prefab != null ? prefab.GetComponent(fieldType) : null;
                if (prefab != null && value == null)
                    Debug.LogError($"Prefab '{rawValue}' has no component {fieldType.Name}.");
            }
            else if (fieldType.IsEnum)
            {
                try
                {
                    value = Enum.Parse(fieldType, rawValue);
                }
                catch (Exception)
                {
                    value = null;
                    Debug.LogError($"Convert Fail: {rawValue} to Enum Type {fieldType}");
                }
            }

            return value;
        }

        private static T LoadAssetByNameInFolder<T>(string rawValue, string folderPath, string assetTypeName, Dictionary<string, UnityEngine.Object> assetCache) where T : UnityEngine.Object
        {
            string trimmedValue = rawValue.Trim();
            string cacheKey = $"{typeof(T).Name}|{folderPath}|{trimmedValue}";
            if (assetCache.TryGetValue(cacheKey, out UnityEngine.Object cached))
                return cached as T;

            if (trimmedValue.StartsWith("Assets/", StringComparison.OrdinalIgnoreCase))
            {
                T absolutePathAsset = AssetDatabase.LoadAssetAtPath<T>(trimmedValue);
                if (absolutePathAsset != null)
                {
                    assetCache[cacheKey] = absolutePathAsset;
                    return absolutePathAsset;
                }
            }

            string normalizedFolderPath = NormalizeAssetFolderPath(folderPath);
            string searchFolder = $"Assets/{normalizedFolderPath}";
            string searchName = Path.GetFileNameWithoutExtension(trimmedValue);
            string[] foundAssetGuids = AssetDatabase.FindAssets($"{searchName} t:{assetTypeName}", new[] { searchFolder });
            for (int index = 0; index < foundAssetGuids.Length; index++)
            {
                string currentGuid = foundAssetGuids[index];
                string currentPath = AssetDatabase.GUIDToAssetPath(currentGuid);
                string currentName = Path.GetFileNameWithoutExtension(currentPath);
                if (String.Equals(currentName, searchName, StringComparison.OrdinalIgnoreCase))
                {
                    T foundAsset = AssetDatabase.LoadAssetAtPath<T>(currentPath);
                    if (foundAsset != null)
                    {
                        assetCache[cacheKey] = foundAsset;
                        return foundAsset;
                    }
                }
            }

            Debug.LogError($"Cannot find {assetTypeName} '{trimmedValue}' in folder '{searchFolder}'.");
            assetCache[cacheKey] = null;
            return null;
        }

        private static GameObject LoadPrefabByNameInFolder(string rawValue, string folderPath, Dictionary<string, UnityEngine.Object> assetCache)
        {
            string trimmedValue = rawValue.Trim();
            string cacheKey = $"GameObject|{folderPath}|{trimmedValue}";
            if (assetCache.TryGetValue(cacheKey, out UnityEngine.Object cached))
                return cached as GameObject;

            if (trimmedValue.StartsWith("Assets/", StringComparison.OrdinalIgnoreCase))
            {
                GameObject absolutePathPrefab = AssetDatabase.LoadAssetAtPath<GameObject>(trimmedValue);
                if (absolutePathPrefab != null)
                {
                    assetCache[cacheKey] = absolutePathPrefab;
                    return absolutePathPrefab;
                }
            }

            string normalizedFolderPath = NormalizeAssetFolderPath(folderPath);
            string searchFolder = $"Assets/{normalizedFolderPath}";
            string searchName = Path.GetFileNameWithoutExtension(trimmedValue);
            string[] foundAssetGuids = AssetDatabase.FindAssets($"{searchName} t:GameObject", new[] { searchFolder });
            for (int index = 0; index < foundAssetGuids.Length; index++)
            {
                string currentGuid = foundAssetGuids[index];
                string currentPath = AssetDatabase.GUIDToAssetPath(currentGuid);
                bool isPrefab = currentPath.EndsWith(".prefab", StringComparison.OrdinalIgnoreCase);
                if (!isPrefab)
                {
                    continue;
                }

                string currentName = Path.GetFileNameWithoutExtension(currentPath);
                if (String.Equals(currentName, searchName, StringComparison.OrdinalIgnoreCase))
                {
                    GameObject foundPrefab = AssetDatabase.LoadAssetAtPath<GameObject>(currentPath);
                    if (foundPrefab != null)
                    {
                        assetCache[cacheKey] = foundPrefab;
                        return foundPrefab;
                    }
                }
            }

            Debug.LogError($"Cannot find Prefab '{trimmedValue}' in folder '{searchFolder}'.");
            assetCache[cacheKey] = null;
            return null;
        }

        private static string NormalizeAssetFolderPath(string folderPath)
        {
            string normalizedFolderPath = folderPath.Replace("\\", "/").Trim('/');
            if (normalizedFolderPath.StartsWith("Assets/", StringComparison.OrdinalIgnoreCase))
            {
                normalizedFolderPath = normalizedFolderPath.Substring("Assets/".Length);
            }

            return normalizedFolderPath;
        }

        public static Type GetTypeByName(string name)
        {
            foreach (Assembly assembly in AppDomain.CurrentDomain.GetAssemblies())
            {
                foreach (Type type in assembly.GetTypes())
                {
                    if (type.Name == name)
                    {
                        return type;
                    }
                }
            }

            return null;
        }

        static bool TryParseSheetInt(string rawValue, out int result)
        {
            if (int.TryParse(rawValue, NumberStyles.Integer, CultureInfo.InvariantCulture, out result))
                return true;

            string normalized = rawValue.Replace(',', '.');
            if (float.TryParse(normalized, NumberStyles.Float, CultureInfo.InvariantCulture, out float decimalValue))
            {
                result = Mathf.RoundToInt(decimalValue);
                return true;
            }

            result = 0;
            return false;
        }
    }
}
