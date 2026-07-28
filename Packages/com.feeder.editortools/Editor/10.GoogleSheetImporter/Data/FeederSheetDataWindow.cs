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
    public sealed class FeederSheetDataWindow : EditorWindow
    {
        private const float ToolbarHeight = 18f;

        private const float MinWindowWidth = 340f;
        private const float MinWindowHeight = 320f;

        private const float ReadOnlyTextBoost = 1.6f;

        private const float TableChromeWidth = 44f;

        private const float TableChromeHeight = 90f;
        private const float EstimatedRowHeight = 22f;
        private const float MaxWindowScreenRatio = 0.9f;

        private const string PrefX = "Feeder.SheetDataWindow.X";
        private const string PrefY = "Feeder.SheetDataWindow.Y";

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
            FeederSheetDataWindow window = Resources.FindObjectsOfTypeAll<FeederSheetDataWindow>()
                .FirstOrDefault(x => x != null);

            if (window == null)
            {
                window = CreateInstance<FeederSheetDataWindow>();
                window.Bind(asset, tab);
                window.Show();
                window.position = ResolveRect(0.9f);
            }
            else
            {
                window.Bind(asset, tab);
            }

            window.minSize = new Vector2(MinWindowWidth, MinWindowHeight);

            window.Focus();
            return window;
        }

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

            Type hostType = typeof(FeederSheetTable<>).MakeGenericType(source.ElementType);
            object host = Activator.CreateInstance(hostType);
            hostRows = (IList)hostType.GetField("rows").GetValue(host);

            Rect screen = EditorGUIUtility.GetMainWindowPosition();
            Dictionary<string, int> widths = source.MeasureColumnWidths(
                screen.width - TableChromeWidth,
                MinWindowWidth - TableChromeWidth,
                out string absorber);
            FeederSheetColumnWidths.Set(source.ElementType, widths, absorber);

            EvictOdinInfoCache();
            tree = PropertyTree.Create(host);
            tableAttribute = tree.GetPropertyAtPath("rows")?.GetAttribute<TableListAttribute>();

            RebuildRows();
            FitToContent(widths);
        }

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
            }
        }

        private void FitToContent(Dictionary<string, int> widths)
        {
            int columns = 0;
            foreach (int width in widths.Values)
            {
                columns += width;
            }

            Rect main = EditorGUIUtility.GetMainWindowPosition();
            float fitWidth = Mathf.Clamp(columns + TableChromeWidth, MinWindowWidth, main.width);

            int rowsOnPage = Mathf.Min(source.RowCount, FeederSheetTableSource.PageSize);
            float fitHeight = Mathf.Clamp(rowsOnPage * EstimatedRowHeight + TableChromeHeight,
                MinWindowHeight, main.height * MaxWindowScreenRatio);

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

            if (tableAttribute != null)
            {
                tableAttribute.ScrollViewHeight =
                    Mathf.Max(120, Mathf.FloorToInt(position.height - ToolbarHeight - 4f));
            }

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
