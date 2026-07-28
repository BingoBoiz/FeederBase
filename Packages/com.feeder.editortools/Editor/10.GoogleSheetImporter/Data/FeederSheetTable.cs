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
    /// <summary>
    /// Vỏ bọc để mượn Odin TableList vẽ bảng theo ý mình.
    /// Attribute là hằng compile-time nên không sửa được cờ trên script sinh ra — nó đang là
    /// [TableList(DrawScrollView = true, ShowPaging = true)]: foldout đóng sẵn, 15 dòng/trang,
    /// kèm nút thêm/xóa. Đổ rows sang type này thì bộ cờ dưới đây có hiệu lực mà không phải
    /// regenerate lại script data nào.
    /// </summary>
    [Serializable]
    public class FeederSheetTable<T>
    {
        [TableList(
             AlwaysExpanded = true,  // bỏ foldout — mở cửa sổ là thấy bảng luôn
             IsReadOnly = true,      // bỏ nút + và cột nút x xóa dòng
             DrawScrollView = true,  // cuộn được trong trang khi trang cao hơn cửa sổ
             ShowPaging = true,      // chặn lag: mỗi frame chỉ dựng đúng một trang thay vì cả nghìn dòng
             NumberOfItemsPerPage = FeederSheetTableSource.PageSize),
         HideLabel]
        public List<T> rows = new List<T>();
    }

    /// <summary>
    /// Đọc ScriptableObject Raw{Tab}.asset bằng reflection.
    /// Bắt buộc reflection: class sinh ra nằm ở Assembly-CSharp, assembly này không reference tới đó được.
    /// </summary>
    public sealed class FeederSheetTableSource
    {
        // Số dòng mỗi trang. Mặc định của Odin là 15 (1448 dòng => 97 trang, bấm mỏi tay);
        // 50 vẫn nhẹ mà chỉ còn ~29 trang. Đổi đúng một chỗ này nếu muốn trang to/nhỏ hơn.
        public const int PageSize = 50;

        // Hằng số lấy lại từ grid tự vẽ đời trước — đã dùng thực tế trên chính mấy sheet này.
        private const int MinColumnWidth = 44;
        private const int MaxColumnWidth = 320;
        private const int ObjectColumnMinWidth = 130;
        private const int BoolColumnWidth = 30;
        private const int MeasureSampleRows = 200;
        // Tiêu đề được canh giữa và Odin không thêm khung gì quanh nó nên không cần đệm rộng —
        // đệm dư ở đây là phình đúng mấy cột số vốn lấy bề rộng từ tiêu đề.
        private const int HeaderPadding = 10;

        // Ô dữ liệu vẽ bằng property.Draw(null), tức string ra text field chứ không phải label:
        // đệm này gánh CellPadding 2px mỗi bên của TableList + viền text field + chút dư.
        private const int CellPadding = 12;

        private readonly IList rows;
        private readonly FieldInfo[] fields;

        // [cột, dòng] — dựng 1 lần lúc mở. Search gõ từng phím mà reflection lại thì mỗi keystroke
        // tốn RowCount × ColumnCount lần GetValue (sheet 1448 dòng × 14 cột ≈ 20k lần).
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

        // Ưu tiên đúng quy ước của FeederDataAssetGenerator: "{camelCaseTypeName}s".
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

            // Object.ToString() của asset trả về "name (Type)" — chỉ cần name là đủ để tìm.
            if (value is Object unityObject)
            {
                return unityObject != null ? unityObject.name : string.Empty;
            }

            return value.ToString();
        }

        /// <summary>
        /// Đo bề rộng "vừa đủ" cho từng cột, trả về map tên field -> pixel.
        /// BẮT BUỘC gọi trong ngữ cảnh GUI: CalcWidth cần GUI skin, gọi ngoài OnGUI sẽ ném.
        /// </summary>
        public Dictionary<string, int> MeasureColumnWidths(float availableWidth)
        {
            Dictionary<string, int> widths = new Dictionary<string, int>();
            if (!IsValid)
            {
                return widths;
            }

            // Odin đo và vẽ tiêu đề cột bằng SirenixGUIStyles.Label / LabelCentered — KHÔNG in đậm.
            // Đo bằng boldLabel sẽ dư ~5-8%, mà với cột số thì tiêu đề mới là thứ quyết định bề rộng
            // nên cột số phình lên đúng chỗ đang muốn thu hẹp.
            GUIStyle headerStyle = SirenixGUIStyles.Label;
            GUIStyle cellStyle = EditorStyles.label;

            int[] natural = new int[fields.Length];
            int[] floors = new int[fields.Length];

            // Sheet thường ghi nối đuôi nên giá trị dài nằm ở cuối — lấy mẫu rải đều thay vì 200 dòng
            // đầu, cùng chi phí mà không bỏ sót phần đuôi.
            int step = Mathf.Max(1, rows.Count / MeasureSampleRows);

            for (int c = 0; c < fields.Length; c++)
            {
                // Odin đặt tiêu đề cột bằng NiceName = ObjectNames.NicifyVariableName(tên field).
                // Với cột số thì tiêu đề dài hơn giá trị: "ID_Effect 1" rộng hơn hẳn số "13".
                string header = ObjectNames.NicifyVariableName(fields[c].Name);
                float width = GUIHelper.CalcWidth(headerStyle, header) + HeaderPadding;

                // Trần MaxColumnWidth kiêm luôn vai trò chặn giá trị cá biệt quá dài, nên lấy max
                // là đủ — không cần percentile (sẽ phải sort 200 float × mỗi cột mà chẳng lợi gì).
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

                floors[c] = FloorForType(fields[c].FieldType);
                natural[c] = Mathf.Clamp(Mathf.CeilToInt(width), floors[c], MaxColumnWidth);
            }

            ShrinkToFit(natural, floors, availableWidth);

            for (int c = 0; c < fields.Length; c++)
            {
                widths[fields[c].Name] = natural[c];
            }

            return widths;
        }

        /// <summary>
        /// TableList KHÔNG có thanh cuộn ngang, và vì Column.MinWidth == ColWidth == bề rộng mình bơm
        /// vào nên Odin cũng không co cột được. Tổng vượt quá chỗ hiển thị là mấy cột bên phải bị cắt
        /// mất hẳn, không cuộn tới được — nên phải tự co từ trước.
        /// </summary>
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
                return; // đã chạm sàn hết, co nữa là không đọc được — đành để tràn
            }

            float keep = Mathf.Clamp01(1f - (sum - available) / shrinkable);
            for (int c = 0; c < widths.Length; c++)
            {
                widths[c] = Mathf.CeilToInt(floors[c] + (widths[c] - floors[c]) * keep);
            }
        }

        private static int FloorForType(Type type)
        {
            // Object reference vẽ kèm nút chọn + icon bút chì; hẹp quá là nuốt mất phần tên asset.
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

    /// <summary>
    /// Kênh truyền bề rộng cột sang <see cref="FeederSheetColumnWidthProcessor"/>.
    /// Phải là static vì processor do Odin khởi tạo, mình không cầm được instance của nó.
    /// </summary>
    internal static class FeederSheetColumnWidths
    {
        private static readonly Dictionary<string, int> Widths = new Dictionary<string, int>();
        private static readonly Type TableDefinition = typeof(FeederSheetTable<>);
        private static Type activeElementType;
        private static string absorberMember;

        public static void Set(Type elementType, Dictionary<string, int> widths)
        {
            Widths.Clear();
            absorberMember = null;
            activeElementType = elementType;
            if (widths == null)
            {
                return;
            }

            // Cột rộng nhất (thường là cột chữ) làm cột co giãn — xem ghi chú ở IsAbsorber.
            int widest = -1;
            foreach (KeyValuePair<string, int> pair in widths)
            {
                Widths[pair.Key] = pair.Value;
                if (pair.Value > widest)
                {
                    widest = pair.Value;
                    absorberMember = pair.Key;
                }
            }
        }

        public static void Clear()
        {
            activeElementType = null;
            absorberMember = null;
            Widths.Clear();
        }

        /// <summary>
        /// "Có phải element type mình đang theo dõi không" — quyết định CACHE, không quyết định bơm.
        /// Odin quét toàn assembly nên hàm này bị gọi cho MỌI property ở MỌI Inspector: phải rẻ và
        /// tuyệt đối không được ném.
        /// </summary>
        public static bool IsTrackedElementType(InspectorProperty parentProperty)
        {
            if (activeElementType == null || parentProperty == null)
            {
                return false;
            }

            IPropertyValueEntry entry = parentProperty.ValueEntry;
            return entry != null && entry.TypeOfValue == activeElementType;
        }

        /// <summary>"Có phải cây của cửa sổ mình không" — quyết định BƠM attribute.</summary>
        public static bool IsOurTree(InspectorProperty parentProperty)
        {
            // TargetType là type của cả cây (root), không phải của property — đúng ở mọi độ sâu.
            Type target = parentProperty?.Tree?.TargetType;
            return target != null && target.IsGenericType &&
                   target.GetGenericTypeDefinition() == TableDefinition;
        }

        /// <summary>
        /// Đúng MỘT cột được để resizable, và đó là cột nuốt phần chênh lệch.
        /// Lý do nằm trong IL của Odin: TableListAttributeDrawer đặt Column.Preserve = !attr.Resizable,
        /// còn GUITableUtilities.DistributeDelta chỉ cộng (rect.width - tổng bề rộng)/số-cột-không-preserve
        /// vào các cột KHÔNG preserve. Để resizable hết thì mọi cột nhận cùng một lượng cộng thêm như nhau,
        /// nên chênh lệch bề rộng bị san phẳng — đúng bug "1 frame sau giãn đều hết".
        /// Nhưng preserve HẾT thì số-cột-không-preserve = 0 và phép chia thành chia cho 0, nên example
        /// của chính Odin luôn chừa lại một cột không gắn attribute. Đây là cột chừa lại đó.
        /// </summary>
        public static bool TryGet(string memberName, out int width, out bool isAbsorber)
        {
            isAbsorber = memberName == absorberMember;
            return Widths.TryGetValue(memberName, out width);
        }
    }

    /// <summary>
    /// Gắn [TableColumnWidth] lên field của type sinh ra mà không phải sửa generator.
    /// KHÔNG đánh dấu [OdinCacheableProcessor]: bề rộng phụ thuộc dữ liệu từng sheet chứ không
    /// cố định theo property, cache lại là sai.
    /// </summary>
    public sealed class FeederSheetColumnWidthProcessor : OdinAttributeProcessor
    {
        public override bool CanProcessSelfAttributes(InspectorProperty property)
        {
            return false;
        }

        // Cố tình trả true cho MỌI cây có element type này, kể cả Inspector thường — không chỉ cây
        // của mình. Đây là thứ ép Odin coi type đó là "không cache được": FinalizedInspectorInfoCache
        // băm hash CHỈ trên tên field chứ không tính attribute, nên bản cache do Inspector thường tạo
        // ra (không có TableColumnWidth) sẽ được dùng lại cho cây của mình và nuốt mất attribute vừa
        // bơm vào — bề rộng im lặng mất tác dụng, không lỗi lầm gì để lần ra.
        public override bool CanProcessChildMemberAttributes(InspectorProperty parentProperty, MemberInfo member)
        {
            return FeederSheetColumnWidths.IsTrackedElementType(parentProperty);
        }

        public override void ProcessChildMemberAttributes(InspectorProperty parentProperty, MemberInfo member,
            List<Attribute> attributes)
        {
            // Chỉ BƠM vào cây của cửa sổ mình; Inspector thường vẫn giữ nguyên như cũ.
            if (!FeederSheetColumnWidths.IsOurTree(parentProperty))
            {
                return;
            }

            if (!FeederSheetColumnWidths.TryGet(member.Name, out int width, out bool isAbsorber))
            {
                return;
            }

            // AttributeUsage(AllowMultiple = false) — Odin lấy cái ĐẦU TIÊN, dọn trước cho chắc.
            attributes.RemoveAll(a => a is TableColumnWidthAttribute || a is ReadOnlyAttribute);

            // Resizable = false => Preserve = true => Odin giữ nguyên bề rộng mình tính.
            attributes.Add(new TableColumnWidthAttribute(width, isAbsorber));

            // Khoá từng Ô thay vì bọc GUI.enabled = false quanh cả bảng. Bọc cả bảng thì nút phân
            // trang và thanh cuộn chết theo vì DrawToolbar nằm cùng trong tree.Draw. Chính doc của
            // TableList.IsReadOnly cũng mô tả ReadOnly là cái "disabling GUI for each element drawn".
            attributes.Add(new ReadOnlyAttribute());
        }
    }
}
#endif
