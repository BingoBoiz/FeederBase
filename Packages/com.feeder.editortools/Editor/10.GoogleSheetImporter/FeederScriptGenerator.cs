using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
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

        public static void GenerateClass(string className, List<string> rawFields, string scriptPath)
        {
            string classData = BuildScriptText(className, rawFields, out List<string> skippedColumns);
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

        /// <summary>
        /// Sinh nội dung file .cs mà KHÔNG chạm đĩa và KHÔNG hiện dialog — cảnh báo trả qua
        /// <paramref name="skippedColumns"/> để cửa sổ xem trước hiện inline thay vì bắn dialog giữa vòng lặp.
        /// </summary>
        public static string BuildScriptText(string className, List<string> rawFields, out List<string> skippedColumns)
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
                    // Chỉ bỏ đúng cột này — trước đây 'break' làm mất luôn mọi field phía sau.
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
                            if (FeederEnumUtils.GetEnumTypeByName(typeToken) == null)
                            {
                                skippedColumns.Add($"  • Cột {i + 1} '{rawField}': không tìm thấy enum {typeToken} → tạm sinh ra string");
                            }
                            else
                            {
                                resolvedTypeName = true;
                                fieldType = typeToken;
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
                                fieldType = componentType.FullName;
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
                    // Prefix lạ -> GetTypeName trả rỗng -> trước đây sinh ra "public  Name;" làm script không compile được.
                    skippedColumns.Add($"  • Cột {i + 1} '{rawField}': prefix không có trong bảng kiểu");
                    continue;
                }

                fieldBuilder.AppendLine(string.Format(FieldTemplate, fieldType, fieldName.Trim()));
            }

            string listField = $"{className[0].ToString().ToLower()}{className.Substring(1)}s";

            // Nối newline cuối để khớp hành vi StreamWriter.WriteLine trước đây; thiếu nó thì
            // diff của mọi file đã generate sẽ báo đổi dòng cuối một cách vô cớ.
            return ScriptTemplate.Replace("{0}", className)
                .Replace("{1}", fieldBuilder.ToString())
                .Replace("{2}", listField) + Environment.NewLine;
        }

        public static void WriteScript(string filePath, string classData)
        {
            File.WriteAllText(filePath, classData, new UTF8Encoding(false));
        }

        // Tách header 'prefix_FieldName' hoặc 'prefix_FieldName:TypeToken' ra đúng tên field C# đã sinh.
        // FeederDataAssetGenerator dùng lại hàm này để map cột theo TÊN thay vì theo chỉ số cột.
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
