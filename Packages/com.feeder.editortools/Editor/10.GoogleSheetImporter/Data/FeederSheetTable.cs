#if ODIN_INSPECTOR
using System;
using System.Collections;
using System.Collections.Generic;
using System.Reflection;
using Sirenix.OdinInspector;
using Sirenix.OdinInspector.Editor;
using Sirenix.Utilities.Editor;
using UnityEditor;
using UnityEngine;
using Object = UnityEngine.Object;

namespace Feeder
{
    [Serializable]
    public class FeederSheetTable<T>
    {
        [TableList(
             AlwaysExpanded = true,
             IsReadOnly = true,
             DrawScrollView = true,
             ShowPaging = true,
             NumberOfItemsPerPage = FeederSheetTableSource.PageSize),
         HideLabel]
        public List<T> rows = new List<T>();
    }

    public sealed class FeederSheetTableSource
    {
        public const int PageSize = 50;

        private const int MinColumnWidth = 44;
        private const int MaxColumnWidth = 320;
        private const int ObjectColumnMinWidth = 130;
        private const int BoolColumnWidth = 30;
        private const int MeasureSampleRows = 200;
        private const int HeaderPadding = 10;

        private const int CellPadding = 12;

        private const float GrowCapRatio = 1.5f;

        private readonly IList rows;
        private readonly FieldInfo[] fields;

        private readonly string[,] text;

        public string Error { get; private set; }

        public Type ElementType { get; private set; }

        public bool IsValid => rows != null && fields != null && fields.Length > 0;

        public IList Rows => rows;

        public int RowCount => rows?.Count ?? 0;

        public int ColumnCount => fields?.Length ?? 0;

        public FeederSheetTableSource(Object asset)
        {
            if (asset == null)
            {
                Error = "Chưa có asset.";
                return;
            }

            Type assetType = asset.GetType();
            FieldInfo listField = FindListField(assetType);
            if (listField == null)
            {
                Error = $"Không tìm thấy field List<> nào trong {assetType.Name}.";
                return;
            }

            rows = listField.GetValue(asset) as IList;
            if (rows == null)
            {
                Error = $"Field '{listField.Name}' đang null.";
                return;
            }

            ElementType = listField.FieldType.IsGenericType
                ? listField.FieldType.GetGenericArguments()[0]
                : null;
            if (ElementType == null)
            {
                Error = $"Field '{listField.Name}' không phải List<T>.";
                return;
            }

            fields = ElementType.GetFields(BindingFlags.Public | BindingFlags.Instance);
            if (fields.Length == 0)
            {
                Error = $"{ElementType.Name} không có field public nào.";
                return;
            }

            text = BuildTextCache();
        }

        private static FieldInfo FindListField(Type assetType)
        {
            string typeName = assetType.Name;
            if (typeName.EndsWith("Data", StringComparison.Ordinal))
            {
                typeName = typeName.Substring(0, typeName.Length - 4);
            }

            if (typeName.Length > 0)
            {
                string conventional = $"{char.ToLower(typeName[0])}{typeName.Substring(1)}s";
                FieldInfo byConvention = assetType.GetField(conventional);
                if (byConvention != null && typeof(IList).IsAssignableFrom(byConvention.FieldType))
                {
                    return byConvention;
                }
            }

            FieldInfo[] all = assetType.GetFields(BindingFlags.Public | BindingFlags.Instance);
            for (int i = 0; i < all.Length; i++)
            {
                if (all[i].FieldType.IsGenericType &&
                    all[i].FieldType.GetGenericTypeDefinition() == typeof(List<>))
                {
                    return all[i];
                }
            }

            return null;
        }

        private string[,] BuildTextCache()
        {
            string[,] cache = new string[fields.Length, rows.Count];
            for (int r = 0; r < rows.Count; r++)
            {
                object element = rows[r];
                if (element == null)
                {
                    continue;
                }

                for (int c = 0; c < fields.Length; c++)
                {
                    cache[c, r] = ToSearchText(fields[c].GetValue(element));
                }
            }

            return cache;
        }

        private static string ToSearchText(object value)
        {
            if (value == null)
            {
                return string.Empty;
            }

            if (value is Object unityObject)
            {
                return unityObject != null ? unityObject.name : string.Empty;
            }

            return value.ToString();
        }

        public Dictionary<string, int> MeasureColumnWidths(float availableWidth, float growTarget,
            out string absorberMember)
        {
            absorberMember = null;
            Dictionary<string, int> widths = new Dictionary<string, int>();
            if (!IsValid)
            {
                return widths;
            }

            GUIStyle headerStyle = SirenixGUIStyles.Label;

            int[] natural = new int[fields.Length];
            int[] floors = new int[fields.Length];

            int step = Mathf.Max(1, rows.Count / MeasureSampleRows);

            HashSet<string> distinct = new HashSet<string>(StringComparer.Ordinal);

            for (int c = 0; c < fields.Length; c++)
            {
                Type type = fields[c].FieldType;
                GUIStyle cellStyle = MeasureStyleForType(type);

                string header = ObjectNames.NicifyVariableName(fields[c].Name);
                float width = GUIHelper.CalcWidth(headerStyle, header) + HeaderPadding;

                if (type.IsEnum)
                {
                    width = MeasureEnumColumn(c, cellStyle, width, distinct);
                }
                else
                {
                    for (int r = 0; r < rows.Count && width < MaxColumnWidth; r += step)
                    {
                        string cell = text[c, r];
                        if (string.IsNullOrEmpty(cell))
                        {
                            continue;
                        }

                        float cellWidth = GUIHelper.CalcWidth(cellStyle, cell) + CellPadding;
                        if (cellWidth > width)
                        {
                            width = cellWidth;
                        }
                    }
                }

                floors[c] = FloorForType(type);
                natural[c] = Mathf.Clamp(Mathf.CeilToInt(width), floors[c], MaxColumnWidth);
            }

            ShrinkToFit(natural, floors, availableWidth);

            GrowToFit(natural, growTarget);

            absorberMember = PickAbsorber(natural);

            for (int c = 0; c < fields.Length; c++)
            {
                widths[fields[c].Name] = natural[c];
            }

            return widths;
        }

        private float MeasureEnumColumn(int column, GUIStyle cellStyle, float width, HashSet<string> distinct)
        {
            distinct.Clear();
            for (int r = 0; r < rows.Count; r++)
            {
                string cell = text[column, r];
                if (!string.IsNullOrEmpty(cell))
                {
                    distinct.Add(cell);
                }
            }

            foreach (string value in distinct)
            {
                if (width >= MaxColumnWidth)
                {
                    break;
                }

                float cellWidth = GUIHelper.CalcWidth(cellStyle,
                    Sirenix.Utilities.StringExtensions.SplitPascalCase(value)) + CellPadding;
                if (cellWidth > width)
                {
                    width = cellWidth;
                }
            }

            return width;
        }

        private static void ShrinkToFit(int[] widths, int[] floors, float available)
        {
            if (available <= 0f)
            {
                return;
            }

            float sum = 0f;
            for (int c = 0; c < widths.Length; c++)
            {
                sum += widths[c];
            }

            if (sum <= available)
            {
                return;
            }

            float shrinkable = 0f;
            for (int c = 0; c < widths.Length; c++)
            {
                shrinkable += widths[c] - floors[c];
            }

            if (shrinkable <= 0f)
            {
                return;
            }

            float keep = Mathf.Clamp01(1f - (sum - available) / shrinkable);
            for (int c = 0; c < widths.Length; c++)
            {
                widths[c] = Mathf.CeilToInt(floors[c] + (widths[c] - floors[c]) * keep);
            }
        }

        private static void GrowToFit(int[] widths, float target)
        {
            if (target <= 0f)
            {
                return;
            }

            float sum = 0f;
            for (int c = 0; c < widths.Length; c++)
            {
                sum += widths[c];
            }

            if (sum <= 0f || sum >= target)
            {
                return;
            }

            float scale = Mathf.Min(target / sum, GrowCapRatio);

            for (int c = 0; c < widths.Length; c++)
            {
                widths[c] = Mathf.FloorToInt(widths[c] * scale);
            }
        }

        private static GUIStyle MeasureStyleForType(Type type)
        {
            if (type.IsEnum)
            {
                return EditorStyles.popup;
            }

            return typeof(Object).IsAssignableFrom(type) ? EditorStyles.objectField : EditorStyles.label;
        }

        private string PickAbsorber(int[] widths)
        {
            int best = -1;
            int bestWidth = -1;

            for (int c = 0; c < fields.Length; c++)
            {
                if (IsElasticType(fields[c].FieldType) && widths[c] >= bestWidth)
                {
                    bestWidth = widths[c];
                    best = c;
                }
            }

            if (best < 0)
            {
                for (int c = 0; c < fields.Length; c++)
                {
                    if (widths[c] >= bestWidth)
                    {
                        bestWidth = widths[c];
                        best = c;
                    }
                }
            }

            return fields[best].Name;
        }

        private static bool IsElasticType(Type type)
        {
            return type == typeof(string) || typeof(Object).IsAssignableFrom(type);
        }

        private static int FloorForType(Type type)
        {
            if (typeof(Object).IsAssignableFrom(type))
            {
                return ObjectColumnMinWidth;
            }

            return type == typeof(bool) ? BoolColumnWidth : MinColumnWidth;
        }

        public bool RowMatches(int row, string needle)
        {
            if (text == null || row < 0 || row >= text.GetLength(1))
            {
                return false;
            }

            for (int c = 0; c < text.GetLength(0); c++)
            {
                string cell = text[c, row];
                if (!string.IsNullOrEmpty(cell) &&
                    cell.IndexOf(needle, StringComparison.OrdinalIgnoreCase) >= 0)
                {
                    return true;
                }
            }

            return false;
        }
    }

    internal static class FeederSheetColumnWidths
    {
        private static readonly Dictionary<string, int> Widths = new Dictionary<string, int>();
        private static readonly Type TableDefinition = typeof(FeederSheetTable<>);
        private static Type activeElementType;
        private static string absorberMember;

        public static void Set(Type elementType, Dictionary<string, int> widths, string absorber)
        {
            Widths.Clear();
            absorberMember = null;
            activeElementType = elementType;
            if (widths == null)
            {
                return;
            }

            foreach (KeyValuePair<string, int> pair in widths)
            {
                Widths[pair.Key] = pair.Value;
            }

            if (absorber != null && Widths.ContainsKey(absorber))
            {
                absorberMember = absorber;
                return;
            }

            foreach (KeyValuePair<string, int> pair in widths)
            {
                absorberMember = pair.Key;
                break;
            }
        }

        public static void Clear()
        {
            activeElementType = null;
            absorberMember = null;
            Widths.Clear();
        }

        public static bool IsTrackedElementType(InspectorProperty parentProperty)
        {
            if (activeElementType == null || parentProperty == null)
            {
                return false;
            }

            IPropertyValueEntry entry = parentProperty.ValueEntry;
            return entry != null && entry.TypeOfValue == activeElementType;
        }

        public static bool IsOurTree(InspectorProperty parentProperty)
        {
            Type target = parentProperty?.Tree?.TargetType;
            return target != null && target.IsGenericType &&
                   target.GetGenericTypeDefinition() == TableDefinition;
        }

        public static bool TryGet(string memberName, out int width, out bool isAbsorber)
        {
            isAbsorber = memberName == absorberMember;
            return Widths.TryGetValue(memberName, out width);
        }
    }

    public sealed class FeederSheetColumnWidthProcessor : OdinAttributeProcessor
    {
        public override bool CanProcessSelfAttributes(InspectorProperty property)
        {
            return false;
        }

        public override bool CanProcessChildMemberAttributes(InspectorProperty parentProperty, MemberInfo member)
        {
            return FeederSheetColumnWidths.IsTrackedElementType(parentProperty);
        }

        public override void ProcessChildMemberAttributes(InspectorProperty parentProperty, MemberInfo member,
            List<Attribute> attributes)
        {
            if (!FeederSheetColumnWidths.IsOurTree(parentProperty))
            {
                return;
            }

            if (!FeederSheetColumnWidths.TryGet(member.Name, out int width, out bool isAbsorber))
            {
                return;
            }

            attributes.RemoveAll(a => a is TableColumnWidthAttribute || a is ReadOnlyAttribute);

            attributes.Add(new TableColumnWidthAttribute(width, isAbsorber));

            attributes.Add(new ReadOnlyAttribute());
        }
    }
}
#endif
