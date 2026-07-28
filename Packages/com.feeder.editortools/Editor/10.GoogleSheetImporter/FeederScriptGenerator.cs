using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Text.RegularExpressions;
using NabaGame.Core.Runtime.Extensions;
using UnityEditor;
using UnityEngine;

namespace Feeder
{
    public static class FeederScriptGenerator
    {
        private static readonly string ScriptTemplate = @"using UnityEngine;
using System.Collections.Generic;
using Sirenix.OdinInspector;

[System.Serializable]
public class {0}
{
{1}}

public class {0}Data : ScriptableObject
{
    [TableList(DrawScrollView = true, ShowPaging = true)]
    public List<{0}> {2} = new List<{0}>();
}";

        private static readonly string FieldTemplate = "\tpublic {0} {1};";

        public static void GenerateClass(string className, List<string> rawFields, string scriptPath,
            string sheetNamespace)
        {
            string classData = BuildScriptText(className, rawFields, sheetNamespace, out List<string> skippedColumns);
            if (classData == null)
            {
                EditorUtility.DisplayDialog("Generate Script",
                    skippedColumns.Count > 0
                        ? string.Join("\n", skippedColumns.ToArray())
                        : "Không sinh được script.", "close");
                return;
            }

            if (skippedColumns.Count > 0)
            {
                string report = "Các cột sau bị bỏ qua / cần xem lại:\n" + string.Join("\n", skippedColumns.ToArray());
                Debug.LogWarning($"[Feeder] {report}");
                if (!EditorUtility.DisplayDialog("Generate Script", report, "Vẫn tạo script", "Huỷ"))
                {
                    return;
                }
            }

            WriteScript($"{scriptPath}/{className}Data.cs", classData);
            AssetDatabase.SaveAssets();
            AssetDatabase.Refresh();
            Debug.Log($"Success !! {className} scripts is generated !!");
        }

        public static string BuildScriptText(string className, List<string> rawFields, string sheetNamespace,
            out List<string> skippedColumns)
        {
            skippedColumns = new List<string>();
            if (className.IsNullOrWhitespace())
            {
                skippedColumns.Add("  • Tên class (ô đầu tiên của hàng 1 trong sheet) đang trống");
                return null;
            }

            if (rawFields == null || rawFields.Count == 0)
            {
                skippedColumns.Add("  • Không có cột nào để sinh field");
                return null;
            }

            StringBuilder fieldBuilder = new StringBuilder();
            for (int i = 0; i < rawFields.Count; i++)
            {
                string rawField = rawFields[i];
                if (rawField.IsNullOrWhitespace())
                {
                    skippedColumns.Add($"  • Cột {i + 1}: header rỗng");
                    continue;
                }

                if (!rawField.Contains("_"))
                {
                    skippedColumns.Add($"  • Cột {i + 1} '{rawField}': thiếu '_' (phải là prefix_FieldName)");
                    continue;
                }

                string fieldType = rawField.Substring(0, rawField.IndexOf('_'));
                string fieldName = rawField.Substring(rawField.IndexOf('_') + 1);
                bool resolvedTypeName = false;
                if (fieldName.Contains(":"))
                {
                    string[] typedParts = fieldName.Split(':');
                    if (typedParts.Length > 1)
                    {
                        string typeToken = typedParts[1];
                        fieldName = typedParts[0];
                        string typeTag = fieldType.Trim().ToLower();
                        if (typeTag == "s")
                        {
                            FeederEnumResolveStatus status = FeederEnumUtils.TryResolveEnumType(
                                typeToken, sheetNamespace, out Type enumType, out List<Type> ambiguous);

                            if (status == FeederEnumResolveStatus.Resolved)
                            {
                                resolvedTypeName = true;
                                fieldType = ToCSharpTypeName(enumType);
                            }
                            else
                            {
                                skippedColumns.Add(DescribeUnresolvedEnum(i, rawField, typeToken, sheetNamespace,
                                    status, ambiguous));
                            }
                        }
                        else if (typeTag == "pref")
                        {
                            // store the prefab's component directly so runtime skips GetComponent
                            Type componentType = FeederDataAssetGenerator.GetTypeByName(typeToken);
                            if (componentType == null || !typeof(Component).IsAssignableFrom(componentType))
                            {
                                skippedColumns.Add($"  • Cột {i + 1} '{rawField}': không tìm thấy component {typeToken} → tạm sinh ra GameObject");
                            }
                            else
                            {
                                resolvedTypeName = true;
                                fieldType = ToCSharpTypeName(componentType);
                            }
                        }
                    }
                }

                if (!resolvedTypeName)
                {
                    fieldType = GetTypeName(fieldType.Trim());
                }

                if (fieldType.IsNullOrWhitespace())
                {
                    skippedColumns.Add($"  • Cột {i + 1} '{rawField}': prefix không có trong bảng kiểu");
                    continue;
                }

                fieldBuilder.AppendLine(string.Format(FieldTemplate, fieldType, fieldName.Trim()));
            }

            string listField = $"{className[0].ToString().ToLower()}{className.Substring(1)}s";

            string generated = ScriptTemplate.Replace("{0}", className)
                .Replace("{1}", fieldBuilder.ToString())
                .Replace("{2}", listField) + Environment.NewLine;

            return sheetNamespace.IsNullOrWhitespace()
                ? generated
                : WrapInNamespace(generated, sheetNamespace.Trim());
        }

        private static string ToCSharpTypeName(Type type)
        {
            return type.FullName?.Replace('+', '.') ?? type.Name;
        }

        private static string DescribeUnresolvedEnum(int columnIndex, string rawField, string typeToken,
            string sheetNamespace, FeederEnumResolveStatus status, List<Type> ambiguous)
        {
            string head = $"  • Cột {columnIndex + 1} '{rawField}': ";
            if (status == FeederEnumResolveStatus.Ambiguous)
            {
                List<string> names = ambiguous.ConvertAll(x => x.FullName?.Replace('+', '.'));
                return head + $"'{typeToken}' trùng tên ở nhiều nơi ({string.Join(", ", names)}) → tạm sinh ra string. " +
                       "Ghi tên đầy đủ trong header sheet.";
            }

            List<Type> elsewhere = FeederEnumUtils.FindEnumsOutsideScope(typeToken, sheetNamespace);
            if (elsewhere.Count > 0)
            {
                List<string> names = elsewhere.ConvertAll(x => x.FullName?.Replace('+', '.'));
                return head + $"không tìm thấy enum '{typeToken}' ở {FeederEnumUtils.DescribeScope(sheetNamespace)} " +
                       $"→ tạm sinh ra string. Nó đang nằm ở: {string.Join(", ", names)} — đặt Namespace của sheet " +
                       $"cho khớp, hoặc ghi tên đầy đủ trong header (ví dụ {rawField.Replace(typeToken, names[0])}).";
            }

            return head + $"không tìm thấy enum '{typeToken}' ở {FeederEnumUtils.DescribeScope(sheetNamespace)} " +
                   "→ tạm sinh ra string. Bấm Update Enum trước để tạo nó.";
        }

        private static string WrapInNamespace(string generated, string ns)
        {
            string newline = FeederEnumSourceEditor.DetectNewline(generated);

            int split = 0;
            while (split < generated.Length)
            {
                int lineEnd = generated.IndexOf('\n', split);
                int nextStart = lineEnd < 0 ? generated.Length : lineEnd + 1;
                string line = generated.Substring(split, (lineEnd < 0 ? generated.Length : lineEnd) - split);
                if (!line.TrimStart().StartsWith("using ", StringComparison.Ordinal))
                {
                    break;
                }

                split = nextStart;
            }

            string usingBlock = generated.Substring(0, split);
            string body = generated.Substring(split).TrimStart('\r', '\n').TrimEnd();

            string indented = Regex.Replace(body, "^(?=[^\r\n])", "    ", RegexOptions.Multiline);

            return usingBlock + newline + "namespace " + ns + newline + "{" + newline +
                   indented + newline + "}" + newline;
        }

        public static void WriteScript(string filePath, string classData)
        {
            File.WriteAllText(filePath, classData, new UTF8Encoding(false));
        }

        public static string GetFieldNameFromHeader(string rawField)
        {
            if (rawField.IsNullOrWhitespace())
            {
                return string.Empty;
            }

            int separatorIndex = rawField.IndexOf('_');
            if (separatorIndex < 0)
            {
                return string.Empty;
            }

            string fieldName = rawField.Substring(separatorIndex + 1);
            int typeTokenIndex = fieldName.IndexOf(':');
            if (typeTokenIndex >= 0)
            {
                fieldName = fieldName.Substring(0, typeTokenIndex);
            }

            return fieldName.Trim();
        }

        private static string GetTypeName(string data)
        {
            string typeName;
            List<string> annotationList = new List<string>(data.Split('.'));
            if (annotationList.Count == 1)
            {
                typeName = GetPrimitiveTypeName(annotationList[0]);
            }
            else
            {
                typeName = GetComplicatedTypeName(annotationList);
            }

            return typeName;
        }

        private static string GetPrimitiveTypeName(string data)
        {
            string typeName = string.Empty;
            if (data == "n")
            {
                typeName = "int";
            }
            else if (data == "by")
            {
                typeName = "byte";
            }
            else if (data == "sh")
            {
                typeName = "short";
            }
            else if (data == "us")
            {
                typeName = "ushort";
            }
            else if (data == "ui")
            {
                typeName = "uint";
            }
            else if (data == "l")
            {
                typeName = "long";
            }
            else if (data == "ul")
            {
                typeName = "ulong";
            }
            else if (data == "d")
            {
                typeName = "double";
            }
            else if (data == "b")
            {
                typeName = "bool";
            }
            else if (data == "s")
            {
                typeName = "string";
            }
            else if (data == "f")
            {
                typeName = "float";
            }
            else if (data == "sp")
            {
                typeName = "Sprite";
            }
            else if (data == "spine")
            {
                typeName = "SkeletonAnimation";
            }
            else if (data == "skeDat")
            {
                typeName = "SkeletonDataAsset";
            }
            else if (data == "pref")
            {
                typeName = "GameObject";
            }
            else if (data.StartsWith("p"))
            {
                int first = data.IndexOf('<');
                int last = data.IndexOf('>');
                string insideString = data.Substring(first + 1, last - first - 1);
                string[] elementList = insideString.Split(',');
                string keyType = GetPrimitiveTypeName(elementList[0]).Capitalize();
                string valueType = GetPrimitiveTypeName(elementList[1]).Capitalize();
                typeName = "Pair" + keyType + valueType;
            }

            return typeName;
        }

        private static string GetComplicatedTypeName(List<string> annotationList)
        {
            string typeName = string.Empty;
            if (annotationList == null || annotationList.Count == 0)
            {
                return string.Empty;
            }

            if (annotationList.Count == 1)
            {
                return GetPrimitiveTypeName(annotationList[0]);
            }

            List<string> subAnnotationList = new List<string>(annotationList);
            subAnnotationList.RemoveAt(0);

            if (annotationList[0] == "li")
            {
                string elementType = GetComplicatedTypeName(subAnnotationList);
                typeName = "List<" + elementType + ">";
            }
            else if (annotationList[0].StartsWith("p"))
            {
                int first = annotationList[1].IndexOf('<');
                int last = annotationList[1].IndexOf('>');
                string insideString = annotationList[1].Substring(first + 1, last - first - 1);
                string[] elementList = insideString.Split(',');
                string keyType = GetPrimitiveTypeName(elementList[0]).Capitalize();
                string valueType = GetPrimitiveTypeName(elementList[1]).Capitalize();
                typeName = "Pair" + keyType + valueType;
            }

            return typeName;
        }
    }
}
