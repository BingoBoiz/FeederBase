using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Reflection;
using System.Text;
using NabaGame.Core.Runtime.Extensions;
using UnityEditor;
using UnityEngine;

namespace Feeder
{
    public static class FeederDataAssetGenerator
    {
        public static void GenerateClass(string sheetName, string[,] cells, List<string> rawFields,
            string assetFolderPath, string spriteAssetFolderPath, string prefabFolderPath, string sheetNamespace)
        {
            sheetName = sheetName.Replace(" ", "");
            string typeName = cells[0, 0];
            string fileName = "Raw" + sheetName;
            string dataClassName = typeName + "Data";
            Type assetType = GetTypeByName(dataClassName, sheetNamespace);
            string path = $"Assets/{assetFolderPath}";

            if (assetType == null)
            {
                EditorUtility.DisplayDialog("Error",
                    $"Cannot find the script {dataClassName}, please generate the script first.\n" +
                    "Nếu vừa đổi Namespace của sheet thì phải bấm Generate Script rồi đợi compile xong.",
                    "close");
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

            Type dataType = GetTypeByName(typeName, sheetNamespace);
            FieldInfo[] fields = dataType.GetFields();
            if (fields == null || fields.Length <= 0)
            {
                EditorUtility.DisplayDialog("Generate Asset", $"Failed ! {typeName} have no field", "close");
                return;
            }

            int totalRow = cells.GetLength(1);
            if (totalRow < 3)
            {
                EditorUtility.DisplayDialog("Generate Asset", "Failed ! data not found", "close");
                return;
            }

            FieldInfo[] columnFields = MapColumnsToFields(typeName, cells, rawFields, fields, out bool canProceed);
            if (!canProceed)
            {
                return;
            }

            dataList.GetType().GetMethod("Clear").Invoke(dataList, null);

            int columnCount = columnFields.Length;
            string[] defaultVals = new string[columnCount];
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
                    for (int col = 0; col < columnCount; col++)
                    {
                        FieldInfo fieldInfo = columnFields[col];
                        if (fieldInfo == null)
                        {
                            continue;
                        }

                        string cell = cells[col, row];
                        if (cell.IsNullOrWhitespace())
                        {
                            if (defaultVals[col].IsNullOrWhitespace())
                            {
                                EditorUtility.DisplayDialog("Generate Asset",
                                    $"Failed ! Ô trống ở dòng {row + 1}, cột '{rawFields[col]}' và không có giá trị phía trên để kế thừa.",
                                    "close");
                                return;
                            }

                            cell = defaultVals[col];
                        }
                        else
                        {
                            defaultVals[col] = cell;
                        }

                        object value = GetFieldValue(fieldInfo, cell, spriteAssetFolderPath, prefabFolderPath, assetCache);
                        if (value == null && fieldInfo.FieldType.IsValueType && !fieldInfo.FieldType.IsEnum)
                        {
                            Debug.LogError(
                                $"[Feeder] {typeName} dòng {row + 1}, cột '{rawFields[col]}' ({fieldInfo.FieldType.Name} {fieldInfo.Name}): " +
                                $"không đọc được giá trị \"{cell}\" → ghi giá trị mặc định.");
                        }

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

        private static FieldInfo[] MapColumnsToFields(string typeName, string[,] cells, List<string> rawFields,
            FieldInfo[] fields, out bool canProceed)
        {
            canProceed = true;
            int columnCount = Mathf.Min(cells.GetLength(0), rawFields.Count);
            FieldInfo[] columnFields = new FieldInfo[columnCount];
            Dictionary<string, int> matchedFields = new Dictionary<string, int>();
            List<string> columnIssues = new List<string>();

            for (int col = 0; col < columnCount; col++)
            {
                string header = rawFields[col];
                string fieldName = FeederScriptGenerator.GetFieldNameFromHeader(header);
                if (fieldName.IsNullOrWhitespace())
                {
                    columnIssues.Add($"  • Cột {col + 1} '{header}': header không đúng dạng prefix_FieldName");
                    continue;
                }

                FieldInfo match = null;
                for (int i = 0; i < fields.Length; i++)
                {
                    if (string.Equals(fields[i].Name, fieldName, StringComparison.Ordinal))
                    {
                        match = fields[i];
                        break;
                    }
                }

                if (match == null)
                {
                    columnIssues.Add($"  • Cột {col + 1} '{header}': class {typeName} không có field '{fieldName}'");
                    continue;
                }

                if (matchedFields.TryGetValue(match.Name, out int previousColumn))
                {
                    columnIssues.Add($"  • Cột {col + 1} '{header}': trùng field '{match.Name}' với cột {previousColumn + 1}");
                    continue;
                }

                columnFields[col] = match;
                matchedFields.Add(match.Name, col);
            }

            List<string> starvedFields = new List<string>();
            for (int i = 0; i < fields.Length; i++)
            {
                if (!matchedFields.ContainsKey(fields[i].Name))
                {
                    starvedFields.Add($"  • {fields[i].FieldType.Name} {fields[i].Name}");
                }
            }

            if (columnIssues.Count == 0 && starvedFields.Count == 0)
            {
                return columnFields;
            }

            StringBuilder message = new StringBuilder();
            message.AppendLine($"Sheet và class {typeName} không khớp nhau:");
            if (columnIssues.Count > 0)
            {
                message.AppendLine();
                message.AppendLine("Cột trong sheet không map được field nào (sẽ bị bỏ qua):");
                message.AppendLine(string.Join("\n", columnIssues.ToArray()));
            }

            if (starvedFields.Count > 0)
            {
                message.AppendLine();
                message.AppendLine("Field trong class không có cột nào cấp dữ liệu (sẽ giữ giá trị mặc định):");
                message.AppendLine(string.Join("\n", starvedFields.ToArray()));
            }

            message.AppendLine();
            message.AppendLine("Thường là do đã sửa header trong sheet mà chưa bấm Generate Script lại.");

            string text = message.ToString();
            Debug.LogWarning($"[Feeder] {text}");
            canProceed = EditorUtility.DisplayDialog("Generate Asset", text, "Vẫn generate", "Huỷ");
            return columnFields;
        }

        private static object GetFieldValue(FieldInfo info, string rawValue, string spriteAssetFolderPath, string prefabFolderPath, Dictionary<string, UnityEngine.Object> assetCache)
        {
            object value = null;
            Type fieldType = info.FieldType;
            if (fieldType == typeof(byte))
            {
                if (TryParseSheetInteger(rawValue, byte.MinValue, byte.MaxValue, out decimal rs))
                {
                    value = (byte)rs;
                }
            }
            else if (fieldType == typeof(short))
            {
                if (TryParseSheetInteger(rawValue, short.MinValue, short.MaxValue, out decimal rs))
                {
                    value = (short)rs;
                }
            }
            else if (fieldType == typeof(ushort))
            {
                if (TryParseSheetInteger(rawValue, ushort.MinValue, ushort.MaxValue, out decimal rs))
                {
                    value = (ushort)rs;
                }
            }
            else if (fieldType == typeof(int))
            {
                if (TryParseSheetInteger(rawValue, int.MinValue, int.MaxValue, out decimal rs))
                {
                    value = (int)rs;
                }
            }
            else if (fieldType == typeof(uint))
            {
                if (TryParseSheetInteger(rawValue, uint.MinValue, uint.MaxValue, out decimal rs))
                {
                    value = (uint)rs;
                }
            }
            else if (fieldType == typeof(long))
            {
                if (TryParseSheetInteger(rawValue, long.MinValue, long.MaxValue, out decimal rs))
                {
                    value = (long)rs;
                }
            }
            else if (fieldType == typeof(ulong))
            {
                if (TryParseSheetInteger(rawValue, ulong.MinValue, ulong.MaxValue, out decimal rs))
                {
                    value = (ulong)rs;
                }
            }
            else if (fieldType == typeof(double))
            {
                if (TryParseSheetDouble(rawValue, out double rs))
                {
                    value = rs;
                }
            }
            else if (fieldType == typeof(float))
            {
                if (TryParseSheetDouble(rawValue, out double rs))
                {
                    value = (float)rs;
                }
            }
            else if (fieldType == typeof(bool))
            {
                string normalized = rawValue.Trim();
                if (bool.TryParse(normalized, out bool rs))
                {
                    value = rs;
                }
                else if (normalized == "1")
                {
                    value = true;
                }
                else if (normalized == "0")
                {
                    value = false;
                }
            }
            else if (fieldType == typeof(string))
            {
                value = rawValue;
            }
            else if (fieldType == typeof(List<byte>))
            {
                string[] elementList = SplitListValue(rawValue);
                List<byte> values = new List<byte>(elementList.Length);
                for (int index = 0; index < elementList.Length; index++)
                {
                    if (TryParseSheetListInteger(info, rawValue, elementList[index], byte.MinValue, byte.MaxValue, out decimal rs))
                    {
                        values.Add((byte)rs);
                    }
                }

                value = values;
            }
            else if (fieldType == typeof(List<short>))
            {
                string[] elementList = SplitListValue(rawValue);
                List<short> values = new List<short>(elementList.Length);
                for (int index = 0; index < elementList.Length; index++)
                {
                    if (TryParseSheetListInteger(info, rawValue, elementList[index], short.MinValue, short.MaxValue, out decimal rs))
                    {
                        values.Add((short)rs);
                    }
                }

                value = values;
            }
            else if (fieldType == typeof(List<ushort>))
            {
                string[] elementList = SplitListValue(rawValue);
                List<ushort> values = new List<ushort>(elementList.Length);
                for (int index = 0; index < elementList.Length; index++)
                {
                    if (TryParseSheetListInteger(info, rawValue, elementList[index], ushort.MinValue, ushort.MaxValue, out decimal rs))
                    {
                        values.Add((ushort)rs);
                    }
                }

                value = values;
            }
            else if (fieldType == typeof(List<int>))
            {
                string[] elementList = SplitListValue(rawValue);
                List<int> values = new List<int>(elementList.Length);
                for (int index = 0; index < elementList.Length; index++)
                {
                    if (TryParseSheetListInteger(info, rawValue, elementList[index], int.MinValue, int.MaxValue, out decimal rs))
                    {
                        values.Add((int)rs);
                    }
                }

                value = values;
            }
            else if (fieldType == typeof(List<uint>))
            {
                string[] elementList = SplitListValue(rawValue);
                List<uint> values = new List<uint>(elementList.Length);
                for (int index = 0; index < elementList.Length; index++)
                {
                    if (TryParseSheetListInteger(info, rawValue, elementList[index], uint.MinValue, uint.MaxValue, out decimal rs))
                    {
                        values.Add((uint)rs);
                    }
                }

                value = values;
            }
            else if (fieldType == typeof(List<long>))
            {
                string[] elementList = SplitListValue(rawValue);
                List<long> values = new List<long>(elementList.Length);
                for (int index = 0; index < elementList.Length; index++)
                {
                    if (TryParseSheetListInteger(info, rawValue, elementList[index], long.MinValue, long.MaxValue, out decimal rs))
                    {
                        values.Add((long)rs);
                    }
                }

                value = values;
            }
            else if (fieldType == typeof(List<ulong>))
            {
                string[] elementList = SplitListValue(rawValue);
                List<ulong> values = new List<ulong>(elementList.Length);
                for (int index = 0; index < elementList.Length; index++)
                {
                    if (TryParseSheetListInteger(info, rawValue, elementList[index], ulong.MinValue, ulong.MaxValue, out decimal rs))
                    {
                        values.Add((ulong)rs);
                    }
                }

                value = values;
            }
            else if (fieldType == typeof(List<float>))
            {
                string[] elementList = SplitListValue(rawValue);
                List<float> values = new List<float>(elementList.Length);
                for (int index = 0; index < elementList.Length; index++)
                {
                    if (TryParseSheetDouble(elementList[index], out double rs))
                    {
                        values.Add((float)rs);
                    }
                    else
                    {
                        LogListElementFailure(info, rawValue, elementList[index]);
                    }
                }

                value = values;
            }
            else if (fieldType == typeof(List<double>))
            {
                string[] elementList = SplitListValue(rawValue);
                List<double> values = new List<double>(elementList.Length);
                for (int index = 0; index < elementList.Length; index++)
                {
                    if (TryParseSheetDouble(elementList[index], out double rs))
                    {
                        values.Add(rs);
                    }
                    else
                    {
                        LogListElementFailure(info, rawValue, elementList[index]);
                    }
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
                    value = Enum.Parse(fieldType, rawValue.Trim());
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

        public static Type GetTypeByName(string name, string sheetNamespace)
        {
            string wanted = string.IsNullOrEmpty(sheetNamespace) || sheetNamespace.Trim().Length == 0
                ? name
                : $"{sheetNamespace.Trim()}.{name}";

            Type loose = null;
            foreach (Assembly assembly in AppDomain.CurrentDomain.GetAssemblies())
            {
                foreach (Type type in GetAssemblyTypes(assembly))
                {
                    if (type == null)
                    {
                        continue;
                    }

                    if (type.FullName == wanted)
                    {
                        return type;
                    }

                    if (loose == null && type.Name == name)
                    {
                        loose = type;
                    }
                }
            }

            if (loose != null && loose.FullName != wanted)
            {
                Debug.LogWarning(
                    $"[Feeder] Không có '{wanted}', dùng tạm '{loose.FullName}' vì trùng tên ngắn. " +
                    "Đặt Namespace của sheet cho khớp rồi Generate Script lại nếu đây không phải class mong muốn.");
            }

            return loose;
        }

        public static Type GetTypeByName(string name)
        {
            return GetTypeByName(name, null);
        }

        private static IEnumerable<Type> GetAssemblyTypes(Assembly assembly)
        {
            try
            {
                return assembly.GetTypes();
            }
            catch (ReflectionTypeLoadException e)
            {
                return e.Types ?? Array.Empty<Type>();
            }
            catch (Exception)
            {
                return Array.Empty<Type>();
            }
        }

        private static bool TryParseSheetDouble(string rawValue, out double result)
        {
            string normalized = rawValue == null ? string.Empty : rawValue.Trim();
            if (double.TryParse(normalized, NumberStyles.Float, CultureInfo.InvariantCulture, out result))
            {
                return true;
            }

            if (double.TryParse(normalized.Replace(',', '.'), NumberStyles.Float, CultureInfo.InvariantCulture, out result))
            {
                return true;
            }

            result = 0d;
            return false;
        }

        private static bool TryParseSheetDecimal(string rawValue, out decimal result)
        {
            string normalized = rawValue == null ? string.Empty : rawValue.Trim();
            if (decimal.TryParse(normalized, NumberStyles.Float, CultureInfo.InvariantCulture, out result))
            {
                return true;
            }

            if (decimal.TryParse(normalized.Replace(',', '.'), NumberStyles.Float, CultureInfo.InvariantCulture, out result))
            {
                return true;
            }

            result = 0m;
            return false;
        }

        private static bool TryParseSheetInteger(string rawValue, decimal min, decimal max, out decimal result)
        {
            result = 0m;
            if (!TryParseSheetDecimal(rawValue, out decimal parsed))
            {
                return false;
            }

            decimal rounded = Math.Round(parsed, MidpointRounding.AwayFromZero);
            if (rounded < min || rounded > max)
            {
                return false;
            }

            result = rounded;
            return true;
        }

        private static bool TryParseSheetListInteger(FieldInfo info, string rawValue, string element, decimal min, decimal max, out decimal result)
        {
            if (TryParseSheetInteger(element, min, max, out result))
            {
                return true;
            }

            LogListElementFailure(info, rawValue, element);
            return false;
        }

        private static void LogListElementFailure(FieldInfo info, string rawValue, string element)
        {
            Debug.LogError($"[Feeder] {info.FieldType.Name} {info.Name}: phần tử \"{element}\" trong \"{rawValue}\" không đọc được → bỏ qua.");
        }

        private static string[] SplitListValue(string rawValue)
        {
            string[] parts = (rawValue ?? string.Empty).Split(',');
            for (int index = 0; index < parts.Length; index++)
            {
                parts[index] = parts[index].Trim();
            }

            return parts;
        }
    }
}
