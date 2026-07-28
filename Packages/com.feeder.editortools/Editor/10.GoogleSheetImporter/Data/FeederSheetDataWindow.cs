#if ODIN_INSPECTOR
using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using Sirenix.OdinInspector;
using Sirenix.OdinInspector.Editor;
using UnityEditor;
using UnityEngine;
using Object = UnityEngine.Object;

namespace Feeder
{
    /// <summary>
    /// Cửa sổ xem data của tab đang chọn. Chỉ để NHÌN: không sửa được, không nút thừa —
    /// toolbar còn đúng ô tìm kiếm và số dòng.
    /// Không dùng Editor.CreateEditor(asset).OnInspectorGUI(): làm vậy là ăn nguyên bộ chrome của
    /// [TableList] trên script sinh ra (field Script, foldout đóng sẵn, "N items", phân trang
    /// 15 dòng/trang, nút thêm/xóa từng dòng) mà không xóa được cái nào. Thay vào đó đổ rows sang
    /// <see cref="FeederSheetTable{T}"/> — bản attribute do mình kiểm soát — rồi tự vẽ bằng PropertyTree.
    /// </summary>
    public sealed class FeederSheetDataWindow : EditorWindow
    {
        private const float ToolbarHeight = 18f;
        private const float MinWindowWidth = 560f;
        private const float MinWindowHeight = 320f;

        // GUI.enabled = false làm chữ mờ đi ~một nửa, mà cửa sổ này sinh ra để đọc data.
        // Đẩy contentColor sáng lên để bù lại; hạ về 1f nếu thấy chói.
        private const float ReadOnlyTextBoost = 1.6f;

        // Phần cửa sổ KHÔNG phải cột: thanh cuộn dọc, viền bảng, khung cửa sổ. Ước dư còn hơn ước
        // thiếu — thiếu thì Odin bóp cột lại rồi cắt chữ, đúng thứ đang đi sửa.
        private const float TableChromeWidth = 44f;

        // Toolbar cửa sổ + thanh "N items / phân trang" của Odin + hàng tiêu đề cột + viền.
        private const float TableChromeHeight = 90f;
        private const float EstimatedRowHeight = 22f;
        private const float MaxWindowScreenRatio = 0.9f;

        private const string PrefX = "Feeder.SheetDataWindow.X";
        private const string PrefY = "Feeder.SheetDataWindow.Y";

        // Object reference SỐNG SÓT domain reload nên bind lại được sau khi Unity compile.
        [SerializeField] private Object targetAsset;
        [SerializeField] private string tabName;

        [NonSerialized] private FeederSheetTableSource source;
        [NonSerialized] private bool sourceDirty = true;
        [NonSerialized] private bool rowsDirty;
        [NonSerialized] private string search = string.Empty;
        [NonSerialized] private int visibleCount;
        [NonSerialized] private PropertyTree tree;
        [NonSerialized] private TableListAttribute tableAttribute;
        [NonSerialized] private IList hostRows;

        public static FeederSheetDataWindow Open(Object asset, string tab)
        {
            // GetWindow<T> sẽ tạo mới nếu cửa sổ đang ẩn sau tab khác; FindObjectsOfTypeAll tìm được
            // cả instance chưa hiện nên không bao giờ đẻ ra cửa sổ thứ hai.
            FeederSheetDataWindow window = Resources.FindObjectsOfTypeAll<FeederSheetDataWindow>()
                .FirstOrDefault(x => x != null);

            if (window == null)
            {
                window = CreateInstance<FeederSheetDataWindow>();
                window.minSize = new Vector2(MinWindowWidth, MinWindowHeight);
                window.Bind(asset, tab);
                window.Show();
                window.position = ResolveRect(0.9f);
            }
            else
            {
                window.Bind(asset, tab);
            }

            window.Focus();
            return window;
        }

        // Chỉ còn lo VỊ TRÍ — kích thước do FitToContent quyết định lại mỗi lần bind sheet.
        private static Rect ResolveRect(float ratio)
        {
            Rect main = EditorGUIUtility.GetMainWindowPosition();
            float width = main.width * ratio;
            float height = main.height * ratio;
            Rect fallback = new Rect(main.x + (main.width - width) * 0.5f,
                main.y + (main.height - height) * 0.5f, width, height);

            if (!EditorPrefs.HasKey(PrefX))
            {
                return fallback;
            }

            Vector2 saved = new Vector2(EditorPrefs.GetFloat(PrefX), EditorPrefs.GetFloat(PrefY));

            // Người dùng có thể đã đổi cấu hình màn hình từ phiên trước — bỏ vị trí ngoài tầm nhìn.
            bool onScreen = saved.x > main.xMin - 200f && saved.x < main.xMax - 100f &&
                            saved.y > main.yMin - 200f && saved.y < main.yMax - 100f;
            return onScreen ? new Rect(saved.x, saved.y, fallback.width, fallback.height) : fallback;
        }

        private void OnDisable()
        {
            EditorPrefs.SetFloat(PrefX, position.x);
            EditorPrefs.SetFloat(PrefY, position.y);
            DisposeTree();
        }

        private void DisposeTree()
        {
            // Odin không public tài liệu PropertyTree có IDisposable hay không; ép qua object
            // để câu lệnh này biên dịch được ở cả hai trường hợp.
            if ((object)tree is IDisposable disposable)
            {
                disposable.Dispose();
            }

            tree = null;
            tableAttribute = null;
            hostRows = null;
            FeederSheetColumnWidths.Clear();
        }

        public void Bind(Object asset, string tab)
        {
            targetAsset = asset;
            tabName = tab;

            // Không dựng source ở đây: Bind gọi từ ngoài OnGUI, mà PropertyTree cùng đám drawer
            // của Odin chỉ hợp lệ trong ngữ cảnh GUI.
            sourceDirty = true;
            UpdateTitle();
        }

        private void UpdateTitle()
        {
            string label = string.IsNullOrEmpty(tabName) ? "Sheet Data" : $"Sheet Data — {tabName}";
            titleContent = new GUIContent(label, EditorGUIUtility.IconContent("ScriptableObject Icon").image);
        }

        private void RebuildSource()
        {
            sourceDirty = false;
            DisposeTree();

            source = new FeederSheetTableSource(targetAsset);
            if (!source.IsValid)
            {
                return;
            }

            // Type của element chỉ biết lúc chạy (nằm ở Assembly-CSharp) nên phải dựng host bằng
            // MakeGenericType. Code editor-only nên không vướng giới hạn AOT của IL2CPP.
            Type hostType = typeof(FeederSheetTable<>).MakeGenericType(source.ElementType);
            object host = Activator.CreateInstance(hostType);
            hostRows = (IList)hostType.GetField("rows").GetValue(host);

            // Phải nạp bề rộng TRƯỚC khi dựng tree: Odin chỉ chạy attribute processor lúc build.
            // RebuildSource luôn chạy trong EventType.Layout nên CalcWidth ở đây hợp lệ.
            // Co sẵn theo màn hình (không theo cửa sổ) để bề rộng không phụ thuộc kích thước cửa sổ —
            // nếu phụ thuộc thì FitToContent đổi size sẽ lại bắt đo lại, thành vòng lặp.
            Rect screen = EditorGUIUtility.GetMainWindowPosition();
            Dictionary<string, int> widths = source.MeasureColumnWidths(screen.width - TableChromeWidth);
            FeederSheetColumnWidths.Set(source.ElementType, widths);

            EvictOdinInfoCache();
            tree = PropertyTree.Create(host);
            tableAttribute = tree.GetPropertyAtPath("rows")?.GetAttribute<TableListAttribute>();

            RebuildRows();
            FitToContent(widths);
        }

        // FinalizedInspectorInfoCache là internal của Odin. Nó băm hash CHỈ trên tên field, key là
        // (element type, hash) và dùng chung toàn editor — nên bản cache mà Inspector thường tạo ra
        // lúc bạn bấm vào Raw{Tab}.asset sẽ được dùng lại cho cây của mình, kèm theo là attribute
        // bề rộng bị nuốt mất. Dọn một nhát trước khi dựng tree.
        // BẮT BUỘC là ClearAll chứ KHÔNG phải Clear: Clear chỉ dọn Cache mà chừa CanBeCachedCache,
        // khiến lần dựng sau tưởng "cache được" rồi ghi bề rộng của mình vào cache dùng chung —
        // rò ngược sang Inspector thường và đóng băng ở đó. Clear() còn tệ hơn là không làm gì.
        private static readonly MethodInfo OdinInfoCacheClearAll = typeof(PropertyTree).Assembly
            .GetType("Sirenix.OdinInspector.Editor.FinalizedInspectorInfoCache")
            ?.GetMethod("ClearAll", BindingFlags.Static | BindingFlags.NonPublic | BindingFlags.Public);

        private static void EvictOdinInfoCache()
        {
            try
            {
                OdinInfoCacheClearAll?.Invoke(null, null);
            }
            catch
            {
                // Odin đổi API thì cùng lắm mất auto-width, không đáng để làm vỡ cả cửa sổ.
            }
        }

        // Không chia phần dư cho cột nào — co cửa sổ lại cho vừa đúng nội dung.
        private void FitToContent(Dictionary<string, int> widths)
        {
            int columns = 0;
            foreach (int width in widths.Values)
            {
                columns += width;
            }

            Rect main = EditorGUIUtility.GetMainWindowPosition();
            float fitWidth = Mathf.Clamp(columns + TableChromeWidth, MinWindowWidth, main.width);

            // Chỉ cần cao bằng MỘT trang, không phải cả 1448 dòng.
            int rowsOnPage = Mathf.Min(source.RowCount, FeederSheetTableSource.PageSize);
            float fitHeight = Mathf.Clamp(rowsOnPage * EstimatedRowHeight + TableChromeHeight,
                MinWindowHeight, main.height * MaxWindowScreenRatio);

            // Gán position giữa OnGUI dễ làm IMGUI lệch layout group — hoãn tới sau khi vẽ xong.
            EditorApplication.delayCall += () =>
            {
                if (this == null)
                {
                    return;
                }

                position = new Rect(position.x, position.y, fitWidth, fitHeight);
            };
        }

        private void RebuildRows()
        {
            if (hostRows == null || source == null || !source.IsValid)
            {
                visibleCount = 0;
                return;
            }

            hostRows.Clear();
            IList all = source.Rows;
            bool filtering = !string.IsNullOrEmpty(search);
            for (int r = 0; r < all.Count; r++)
            {
                if (!filtering || source.RowMatches(r, search))
                {
                    hostRows.Add(all[r]);
                }
            }

            visibleCount = hostRows.Count;
            tree?.UpdateTree();
        }

        private void OnGUI()
        {
            // Đổi số dòng giữa chừng một event không phải Layout sẽ làm IMGUI lệch layout group
            // ("Mismatched LayoutGroup") vì Odin dựng bảng bằng layout. Dồn hết về đúng Layout pass.
            if (Event.current.type == EventType.Layout)
            {
                if (sourceDirty)
                {
                    RebuildSource();
                }
                else if (rowsDirty)
                {
                    rowsDirty = false;
                    RebuildRows();
                }
            }

            DrawToolbar();

            if (source == null || !source.IsValid)
            {
                EditorGUILayout.HelpBox(
                    source?.Error ?? "Chưa có dữ liệu. Bấm 'Generate Assets' trong Googlesheet Importer trước.",
                    MessageType.Info);
                return;
            }

            DrawTable();
        }

        private void DrawToolbar()
        {
            EditorGUILayout.BeginHorizontal(EditorStyles.toolbar);

            string nextSearch = GUILayout.TextField(search, EditorStyles.toolbarSearchField,
                GUILayout.MinWidth(160f), GUILayout.MaxWidth(420f), GUILayout.ExpandWidth(true));
            if (nextSearch != search)
            {
                search = nextSearch;
                rowsDirty = true;
                Repaint();
            }

            GUILayout.FlexibleSpace();
            if (source != null && source.IsValid)
            {
                string rows = visibleCount == source.RowCount
                    ? $"{source.RowCount} rows"
                    : $"{visibleCount} / {source.RowCount} rows";
                GUILayout.Label($"{rows} · {source.ColumnCount} cols", EditorStyles.miniLabel);
            }

            EditorGUILayout.EndHorizontal();
        }

        private void DrawTable()
        {
            if (tree == null)
            {
                return;
            }

            // TableList tự cuộn và tự cull dòng ngoài tầm nhìn, nhưng mặc định bó trong hộp ~350px —
            // đúng thứ làm phóng to cửa sổ mà chẳng thấy thêm dòng nào. Ép cao bằng phần còn lại.
            if (tableAttribute != null)
            {
                tableAttribute.ScrollViewHeight =
                    Mathf.Max(120, Mathf.FloorToInt(position.height - ToolbarHeight - 4f));
            }

            // TUYỆT ĐỐI không bọc GUI.enabled = false quanh cả cây: DrawToolbar (nút phân trang) và
            // thanh cuộn của TableList nằm cùng trong tree.Draw nên sẽ chết theo — đó đúng là lý do
            // kéo thanh cuộn không ăn. Từng ô đã được khoá riêng bằng ReadOnly bơm qua
            // FeederSheetColumnWidthProcessor, nên chrome vẫn bấm/kéo được bình thường.
            Color previousContent = GUI.contentColor;
            GUI.contentColor = previousContent * ReadOnlyTextBoost;
            try
            {
                tree.Draw(false);
            }
            finally
            {
                GUI.contentColor = previousContent;
            }
        }
    }
}
#endif
