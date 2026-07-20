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
            Type elementType = Assembly.GetExecutingAssembly().GetType(className);
            if (elementType != null)
            {
                EditorUtility.DisplayDialog("Generate Script", $"{elementType} is already existed", "close");
                return;
            }

            StringBuilder fieldBuilder = new StringBuilder();
            for (int i = 0; i < rawFields.Count; i++)
            {
                string rawField = rawFields[i];
                if (rawField.IsNullOrWhitespace())
                {
                    EditorUtility.DisplayDialog("Generate Script", $"Column {i + 1} is empty field", "close");
                    break;
                }

                if (!rawField.Contains("_"))
                {
                    EditorUtility.DisplayDialog("Generate Script", $"Column {i + 1} : {rawField} is invalid", "close");
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
                                EditorUtility.DisplayDialog("Generate Script", $"Column {i + 1} : enum {typeToken} not found", "close");
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
                                EditorUtility.DisplayDialog("Generate Script", $"Column {i + 1} : component {typeToken} not found", "close");
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

                fieldBuilder.AppendLine(string.Format(FieldTemplate, fieldType, fieldName));
            }

            string listField = $"{className[0].ToString().ToLower()}{className.Substring(1)}s";
            string classData = ScriptTemplate.Replace("{0}", className)
                .Replace("{1}", fieldBuilder.ToString())
                .Replace("{2}", listField);
            string filePath = scriptPath + $"/{className}Data.cs";
            StreamWriter writer = File.CreateText(filePath);
            writer.WriteLine(classData);
            writer.Close();
            AssetDatabase.SaveAssets();
            AssetDatabase.Refresh();
            Debug.Log($"Success !! {className} scripts is generated !!");
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
