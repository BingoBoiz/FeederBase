using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using UnityEditor;
using UnityEngine;
using Object = UnityEngine.Object;

namespace Feeder
{
    public sealed class FBuildSizeWindow : EditorWindow
    {
        private enum ViewMode
        {
            Current,
            Diff,
            Budget,
            Settings
        }

        private enum AssetSortType
        {
            AssetFullPath,
            AssetFilename,
            Type,
            PackedSize,
            PercentSize
        }

        private enum DiffSortType
        {
            AssetFullPath,
            Type,
            Status,
            SizeA,
            SizeB,
            Delta
        }

        private enum SortOrder
        {
            Ascending,
            Descending
        }

        private static readonly string[] ViewModeLabels = { "Current", "Diff", "Budget", "Settings" };
        private static readonly int[] PageSizeValues = { 25, 50, 100, 200, 500 };
        private static readonly string[] PageSizeLabels = { "25", "50", "100", "200", "500" };

        private static readonly float[] AssetColumnWidths = { 110f, 120f, 70f };            // Type | Packed Size | %
        private static readonly float[] DiffColumnWidths = { 90f, 74f, 100f, 100f, 110f };  // Type | Status | Size A | Size B | Delta
        private static readonly float[] BudgetColumnWidths = { 90f, 110f, 110f, 110f };     // Type | Packed Size | Budget | Over By

        private const float ScrollbarReserveWidth = 18f;
        private const float IconAndPaddingWidth = 24f;
        private const long BytesPerMB = 1024L * 1024L;

        private static readonly Color OverBudgetTint = new Color(1f, 0.3f, 0.2f, 0.12f);
        private static readonly Color HoverTint = new Color(0.25f, 0.55f, 1f, 0.12f);

        [NonSerialized] private ViewMode viewMode = ViewMode.Current;
        [NonSerialized] private int currentIndex = -1;   // -1 = latest snapshot
        [NonSerialized] private int diffIndexA = -1;     // -1 = second newest
        [NonSerialized] private int diffIndexB = -1;     // -1 = newest
        [NonSerialized] private string searchText;
        [NonSerialized] private bool onlyChanged = true;
        [NonSerialized] private bool onlyOverBudget;
        [NonSerialized] private Vector2 scroll;
        [NonSerialized] private int pageIndex;
        [NonSerialized] private int pageSize = 100;
        [NonSerialized] private bool viewDirty = true;
        [NonSerialized] private AssetSortType assetSortType = AssetSortType.PackedSize;
        [NonSerialized] private SortOrder assetSortOrder = SortOrder.Descending;
        [NonSerialized] private DiffSortType diffSortType = DiffSortType.Delta;
        [NonSerialized] private SortOrder diffSortOrder = SortOrder.Descending;

        [NonSerialized] private List<FBuildAssetRow> displayAssetRows;
        [NonSerialized] private List<FBuildDiffRow> allDiffRows;
        [NonSerialized] private List<FBuildDiffRow> displayDiffRows;
        [NonSerialized] private FBuildDiffSummary diffSummary;
        [NonSerialized] private int overBudgetCount;
        [NonSerialized] private long historyStamp = long.MinValue;
        [NonSerialized] private float measuredRowWidth = -1f;

        private GUIStyle deltaUpStyle;
        private GUIStyle deltaDownStyle;
        private GUIStyle warningCountStyle;

        [MenuItem("Tools/Feeder/Feeder Build Size Diff", priority = 3)]
        private static void OpenWindow()
        {
            var window = GetWindow<FBuildSizeWindow>("Build Size Diff");
            window.minSize = new Vector2(560f, 320f);
            window.Show();
        }

        private void OnFocus()
        {
            CheckHistoryChanged();
        }

        private void OnGUI()
        {
            CheckHistoryChanged();
            DrawTopToolbar();

            switch (viewMode)
            {
                case ViewMode.Current: DrawCurrentView(); break;
                case ViewMode.Diff: DrawDiffView(); break;
                case ViewMode.Budget: DrawBudgetView(); break;
                case ViewMode.Settings: DrawSettingsView(); break;
            }
        }

        private void CheckHistoryChanged()
        {
            var history = FBuildSizeHistory.instance;
            long stamp = history.snapshots.Count;
            if (history.HasData)
                stamp = stamp * 397 ^ history.Latest.buildEndedTicksUtc;

            if (stamp == historyStamp) return;
            historyStamp = stamp;
            currentIndex = -1;
            diffIndexA = -1;
            diffIndexB = -1;
            pageIndex = 0;
            viewDirty = true;
            Repaint();
        }

        // ---------------------------------------------------------------- toolbar

        private void DrawTopToolbar()
        {
            var history = FBuildSizeHistory.instance;

            EditorGUILayout.BeginHorizontal(EditorStyles.toolbar);

            EditorGUI.BeginChangeCheck();
            var nextMode = (ViewMode)GUILayout.Toolbar((int)viewMode, ViewModeLabels, EditorStyles.toolbarButton, GUILayout.Width(280));
            if (EditorGUI.EndChangeCheck())
            {
                viewMode = nextMode;
                pageIndex = 0;
                scroll = Vector2.zero;
                viewDirty = true;
            }

            GUILayout.Space(8);

            if (history.HasData && viewMode != ViewMode.Settings)
            {
                string[] labels = history.snapshots.Select(s => s.Label).ToArray();

                if (viewMode == ViewMode.Diff)
                {
                    GUILayout.Label("A", GUILayout.Width(14));
                    DrawSnapshotPopup(labels, ref diffIndexA, Math.Max(0, labels.Length - 2));
                    GUILayout.Label("B", GUILayout.Width(14));
                    DrawSnapshotPopup(labels, ref diffIndexB, labels.Length - 1);
                }
                else
                {
                    GUILayout.Label("Build", GUILayout.Width(36));
                    DrawSnapshotPopup(labels, ref currentIndex, labels.Length - 1);
                }
            }

            if (GUILayout.Button("Import Last Report", EditorStyles.toolbarButton, GUILayout.Width(120)))
                ImportLastReport();

            GUILayout.FlexibleSpace();
            GUILayout.Label(history.HasData ? $"{history.snapshots.Count} snapshot(s)" : "No snapshots", EditorStyles.miniLabel);
            EditorGUILayout.EndHorizontal();
        }

        private void DrawSnapshotPopup(string[] labels, ref int index, int defaultIndex)
        {
            int resolved = index >= 0 && index < labels.Length ? index : defaultIndex;
            EditorGUI.BeginChangeCheck();
            int next = EditorGUILayout.Popup(resolved, labels, EditorStyles.toolbarPopup, GUILayout.MinWidth(180), GUILayout.MaxWidth(280));
            if (EditorGUI.EndChangeCheck())
            {
                index = next;
                pageIndex = 0;
                scroll = Vector2.zero;
                viewDirty = true;
            }
        }

        private void ImportLastReport()
        {
            if (FBuildSizeCapture.ImportLastBuildReport(out string error))
            {
                CheckHistoryChanged();
                ShowNotification(new GUIContent("Last build report imported"));
            }
            else
            {
                EditorUtility.DisplayDialog("Import Last Report", error, "OK");
            }
        }

        private FBuildSnapshot GetSnapshot(int index, int defaultIndex)
        {
            var snapshots = FBuildSizeHistory.instance.snapshots;
            if (snapshots == null || snapshots.Count == 0) return null;
            int resolved = index >= 0 && index < snapshots.Count ? index : defaultIndex;
            if (resolved < 0 || resolved >= snapshots.Count) return null;
            return snapshots[resolved];
        }

        private FBuildSnapshot CurrentSnapshot => GetSnapshot(currentIndex, FBuildSizeHistory.instance.snapshots.Count - 1);
        private FBuildSnapshot DiffSnapshotA => GetSnapshot(diffIndexA, FBuildSizeHistory.instance.snapshots.Count - 2);
        private FBuildSnapshot DiffSnapshotB => GetSnapshot(diffIndexB, FBuildSizeHistory.instance.snapshots.Count - 1);

        // ---------------------------------------------------------------- Current view

        private void DrawCurrentView()
        {
            FBuildSnapshot snapshot = CurrentSnapshot;
            if (snapshot == null)
            {
                DrawNoSnapshotHelp();
                return;
            }

            EnsureCurrentRows(snapshot);
            DrawSnapshotSummary(snapshot);
            DrawAssetFilterToolbar(snapshot);

            if (displayAssetRows == null || displayAssetRows.Count == 0)
            {
                EditorGUILayout.HelpBox("No asset matches the current filter.", MessageType.Info);
                return;
            }

            DrawPageToolbar(displayAssetRows.Count);
            DrawAssetHeaderRow();

            var history = FBuildSizeHistory.instance;
            scroll = EditorGUILayout.BeginScrollView(scroll);
            foreach (FBuildAssetRow row in GetPage(displayAssetRows))
                DrawAssetRow(row, history.IsOverBudget(row));
            EditorGUILayout.EndScrollView();

            DrawPageToolbar(displayAssetRows.Count);
        }

        private void DrawNoSnapshotHelp()
        {
            EditorGUILayout.HelpBox(
                "No build snapshots yet.\nMake a player build, or click \"Import Last Report\" to read Library/LastBuild.buildreport from the most recent build.",
                MessageType.Info);
        }

        private void DrawSnapshotSummary(FBuildSnapshot snapshot)
        {
            var history = FBuildSizeHistory.instance;

            EditorGUILayout.BeginVertical(EditorStyles.helpBox);
            EditorGUILayout.LabelField("Build Summary", EditorStyles.boldLabel);
            EditorGUILayout.LabelField("Platform", $"{snapshot.platform}  ({snapshot.result})");
            EditorGUILayout.LabelField("Output", string.IsNullOrEmpty(snapshot.outputPath) ? "-" : snapshot.outputPath);
            EditorGUILayout.LabelField("Built At", snapshot.BuildEndedLocal.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture) +
                                                   (snapshot.buildSeconds > 0 ? $"  ({TimeSpan.FromSeconds(snapshot.buildSeconds):hh\\:mm\\:ss})" : string.Empty));

            string totalLabel = FBuildSizeUtil.FormatBytes(snapshot.DisplayTotal);
            if (snapshot.totalSize <= 0)
                totalLabel += "  (from packed assets)";
            EditorGUILayout.LabelField("Total Size", totalLabel);
            EditorGUILayout.LabelField("Packed Assets", $"{snapshot.assetCount} assets, {FBuildSizeUtil.FormatBytes(snapshot.packedTotal)}");

            if (history.totalBuildBudget > 0)
            {
                long over = snapshot.DisplayTotal - history.totalBuildBudget;
                EditorGUILayout.LabelField("Total Budget", over > 0
                    ? $"{FBuildSizeUtil.FormatBytes(history.totalBuildBudget)}  — OVER by {FBuildSizeUtil.FormatBytes(over)}"
                    : $"{FBuildSizeUtil.FormatBytes(history.totalBuildBudget)}  — OK ({FBuildSizeUtil.FormatBytes(-over)} left)");
            }

            if (overBudgetCount > 0)
            {
                Rect rect = EditorGUILayout.GetControlRect(false, 18f);
                EditorGUI.LabelField(rect, "Over Budget", $"{overBudgetCount} asset(s) over budget", GetWarningCountStyle());
            }

            GUILayout.Label(
                "Packed assets exclude scene serialized data, code and StreamingAssets — the sum of rows is less than the total build size.",
                EditorStyles.miniLabel);
            EditorGUILayout.EndVertical();
            EditorGUILayout.Space(4);
        }

        private void EnsureCurrentRows(FBuildSnapshot snapshot)
        {
            if (!viewDirty && displayAssetRows != null) return;

            var history = FBuildSizeHistory.instance;
            overBudgetCount = snapshot.rows != null ? snapshot.rows.Count(history.IsOverBudget) : 0;

            IEnumerable<FBuildAssetRow> query = snapshot.rows ?? Enumerable.Empty<FBuildAssetRow>();
            if (!string.IsNullOrWhiteSpace(searchText))
            {
                query = query.Where(r =>
                    Contains(r.path, searchText) ||
                    Contains(r.fileName, searchText) ||
                    Contains(r.typeName, searchText));
            }

            displayAssetRows = SortAssetRows(query).ToList();
            viewDirty = false;
            ClampPage(displayAssetRows.Count);
        }

        private IOrderedEnumerable<FBuildAssetRow> SortAssetRows(IEnumerable<FBuildAssetRow> source)
        {
            bool desc = assetSortOrder == SortOrder.Descending;

            switch (assetSortType)
            {
                case AssetSortType.AssetFullPath:
                    return desc
                        ? source.OrderByDescending(r => r.path, StringComparer.OrdinalIgnoreCase)
                        : source.OrderBy(r => r.path, StringComparer.OrdinalIgnoreCase);
                case AssetSortType.AssetFilename:
                    return desc
                        ? source.OrderByDescending(r => r.fileName, StringComparer.OrdinalIgnoreCase).ThenByDescending(r => r.path, StringComparer.OrdinalIgnoreCase)
                        : source.OrderBy(r => r.fileName, StringComparer.OrdinalIgnoreCase).ThenBy(r => r.path, StringComparer.OrdinalIgnoreCase);
                case AssetSortType.Type:
                    return desc
                        ? source.OrderByDescending(r => r.typeName, StringComparer.OrdinalIgnoreCase).ThenByDescending(r => r.packedSize)
                        : source.OrderBy(r => r.typeName, StringComparer.OrdinalIgnoreCase).ThenByDescending(r => r.packedSize);
                case AssetSortType.PercentSize:
                    return desc
                        ? source.OrderByDescending(r => r.percent).ThenBy(r => r.path, StringComparer.OrdinalIgnoreCase)
                        : source.OrderBy(r => r.percent).ThenBy(r => r.path, StringComparer.OrdinalIgnoreCase);
                case AssetSortType.PackedSize:
                default:
                    return desc
                        ? source.OrderByDescending(r => r.packedSize).ThenBy(r => r.path, StringComparer.OrdinalIgnoreCase)
                        : source.OrderBy(r => r.packedSize).ThenBy(r => r.path, StringComparer.OrdinalIgnoreCase);
            }
        }

        private void DrawAssetFilterToolbar(FBuildSnapshot snapshot)
        {
            EditorGUILayout.BeginHorizontal(EditorStyles.toolbar);
            DrawSearchField();

            GUILayout.Space(8);
            GUILayout.Label("Sort", GUILayout.Width(32));
            EditorGUI.BeginChangeCheck();
            var nextSortType = (AssetSortType)EditorGUILayout.EnumPopup(assetSortType, EditorStyles.toolbarPopup, GUILayout.Width(120));
            var nextSortOrder = (SortOrder)EditorGUILayout.EnumPopup(assetSortOrder, EditorStyles.toolbarPopup, GUILayout.Width(94));
            if (EditorGUI.EndChangeCheck())
            {
                assetSortType = nextSortType;
                assetSortOrder = nextSortOrder;
                pageIndex = 0;
                viewDirty = true;
            }

            using (new EditorGUI.DisabledScope(displayAssetRows == null || displayAssetRows.Count == 0))
            {
                if (GUILayout.Button("Copy CSV", EditorStyles.toolbarButton, GUILayout.Width(70)))
                    CopyAssetRows(displayAssetRows);
            }

            GUILayout.FlexibleSpace();
            int total = snapshot.rows != null ? snapshot.rows.Count : 0;
            GUILayout.Label($"{(displayAssetRows != null ? displayAssetRows.Count : 0)}/{total} shown", EditorStyles.miniLabel);
            EditorGUILayout.EndHorizontal();
        }

        private void DrawAssetHeaderRow()
        {
            Rect rect = GUILayoutUtility.GetRect(0f, 22f, GUILayout.ExpandWidth(true));
            GUI.Box(rect, GUIContent.none, EditorStyles.toolbar);
            Rect[] cols = GetColumnRects(rect, AssetColumnWidths, GetHeaderReserve(rect));

            if (DrawHeaderButton(cols[0], "Asset", assetSortType == AssetSortType.AssetFullPath, assetSortOrder))
                ToggleAssetSort(AssetSortType.AssetFullPath, false);
            if (DrawHeaderButton(cols[1], "Type", assetSortType == AssetSortType.Type, assetSortOrder))
                ToggleAssetSort(AssetSortType.Type, false);
            if (DrawHeaderButton(cols[2], "Packed Size", assetSortType == AssetSortType.PackedSize, assetSortOrder))
                ToggleAssetSort(AssetSortType.PackedSize, true);
            if (DrawHeaderButton(cols[3], "Percent", assetSortType == AssetSortType.PercentSize, assetSortOrder))
                ToggleAssetSort(AssetSortType.PercentSize, true);
        }

        private void ToggleAssetSort(AssetSortType nextType, bool sizeSort)
        {
            if (assetSortType == nextType)
                assetSortOrder = assetSortOrder == SortOrder.Descending ? SortOrder.Ascending : SortOrder.Descending;
            else
            {
                assetSortType = nextType;
                assetSortOrder = sizeSort ? SortOrder.Descending : SortOrder.Ascending;
            }

            pageIndex = 0;
            scroll = Vector2.zero;
            viewDirty = true;
        }

        private void DrawAssetRow(FBuildAssetRow row, bool overBudget)
        {
            Rect rect = GetRowRect();
            if (overBudget)
                EditorGUI.DrawRect(rect, OverBudgetTint);
            if (rect.Contains(Event.current.mousePosition))
                EditorGUI.DrawRect(rect, HoverTint);

            Rect[] cols = GetColumnRects(rect, AssetColumnWidths, 0f);
            DrawAssetPathCell(cols[0], rect, row.path);
            GUI.Label(InsetCell(cols[1]), row.typeName, EditorStyles.miniLabel);
            GUI.Label(InsetCell(cols[2]), FBuildSizeUtil.FormatBytes(row.packedSize), EditorStyles.miniLabel);
            GUI.Label(InsetCell(cols[3]), $"{row.percent:0.##}%", EditorStyles.miniLabel);

            HandleAssetClick(rect, row.path);
        }

        // ---------------------------------------------------------------- Diff view

        private void DrawDiffView()
        {
            var history = FBuildSizeHistory.instance;
            if (!history.HasData)
            {
                DrawNoSnapshotHelp();
                return;
            }

            if (history.snapshots.Count < 2)
            {
                EditorGUILayout.HelpBox("Need at least two build snapshots to diff. Make another build or import a report.", MessageType.Info);
                return;
            }

            FBuildSnapshot a = DiffSnapshotA;
            FBuildSnapshot b = DiffSnapshotB;
            if (a == null || b == null) return;

            if (a == b)
                EditorGUILayout.HelpBox("A and B are the same snapshot.", MessageType.Warning);
            else if (!string.Equals(a.platform, b.platform, StringComparison.Ordinal))
                EditorGUILayout.HelpBox($"Comparing different platforms: {a.platform} vs {b.platform}.", MessageType.Warning);

            EnsureDiffRows(a, b);
            DrawDiffSummary(a, b);
            DrawDiffFilterToolbar();

            if (displayDiffRows == null || displayDiffRows.Count == 0)
            {
                EditorGUILayout.HelpBox("No diff row matches the current filter.", MessageType.Info);
                return;
            }

            DrawPageToolbar(displayDiffRows.Count);
            DrawDiffHeaderRow();

            scroll = EditorGUILayout.BeginScrollView(scroll);
            foreach (FBuildDiffRow row in GetPage(displayDiffRows))
                DrawDiffRow(row);
            EditorGUILayout.EndScrollView();

            DrawPageToolbar(displayDiffRows.Count);
        }

        private void EnsureDiffRows(FBuildSnapshot a, FBuildSnapshot b)
        {
            if (!viewDirty && displayDiffRows != null) return;

            allDiffRows = FBuildSizeDiffEngine.Compute(a, b);
            diffSummary = FBuildSizeDiffEngine.Summarize(allDiffRows);

            var history = FBuildSizeHistory.instance;
            IEnumerable<FBuildDiffRow> query = allDiffRows;

            if (onlyChanged)
                query = query.Where(r => r.status != FBuildDiffStatus.Same);
            if (onlyOverBudget)
                query = query.Where(r => IsDiffRowOverBudget(history, r));
            if (!string.IsNullOrWhiteSpace(searchText))
            {
                query = query.Where(r =>
                    Contains(r.path, searchText) ||
                    Contains(r.fileName, searchText) ||
                    Contains(r.typeName, searchText) ||
                    Contains(r.status.ToString(), searchText));
            }

            displayDiffRows = SortDiffRows(query).ToList();
            viewDirty = false;
            ClampPage(displayDiffRows.Count);
        }

        private static bool IsDiffRowOverBudget(FBuildSizeHistory history, FBuildDiffRow row)
        {
            long budget = history.GetBudgetFor(row.category);
            return budget > 0 && row.sizeB > budget;
        }

        private IOrderedEnumerable<FBuildDiffRow> SortDiffRows(IEnumerable<FBuildDiffRow> source)
        {
            bool desc = diffSortOrder == SortOrder.Descending;

            switch (diffSortType)
            {
                case DiffSortType.AssetFullPath:
                    return desc
                        ? source.OrderByDescending(r => r.path, StringComparer.OrdinalIgnoreCase)
                        : source.OrderBy(r => r.path, StringComparer.OrdinalIgnoreCase);
                case DiffSortType.Type:
                    return desc
                        ? source.OrderByDescending(r => r.typeName, StringComparer.OrdinalIgnoreCase).ThenByDescending(r => r.delta)
                        : source.OrderBy(r => r.typeName, StringComparer.OrdinalIgnoreCase).ThenByDescending(r => r.delta);
                case DiffSortType.Status:
                    return desc
                        ? source.OrderByDescending(r => r.status).ThenByDescending(r => r.delta)
                        : source.OrderBy(r => r.status).ThenByDescending(r => r.delta);
                case DiffSortType.SizeA:
                    return desc
                        ? source.OrderByDescending(r => r.sizeA).ThenBy(r => r.path, StringComparer.OrdinalIgnoreCase)
                        : source.OrderBy(r => r.sizeA).ThenBy(r => r.path, StringComparer.OrdinalIgnoreCase);
                case DiffSortType.SizeB:
                    return desc
                        ? source.OrderByDescending(r => r.sizeB).ThenBy(r => r.path, StringComparer.OrdinalIgnoreCase)
                        : source.OrderBy(r => r.sizeB).ThenBy(r => r.path, StringComparer.OrdinalIgnoreCase);
                case DiffSortType.Delta:
                default:
                    return desc
                        ? source.OrderByDescending(r => r.delta).ThenBy(r => r.path, StringComparer.OrdinalIgnoreCase)
                        : source.OrderBy(r => r.delta).ThenBy(r => r.path, StringComparer.OrdinalIgnoreCase);
            }
        }

        private void DrawDiffSummary(FBuildSnapshot a, FBuildSnapshot b)
        {
            EditorGUILayout.BeginVertical(EditorStyles.helpBox);
            EditorGUILayout.LabelField("Diff Summary  (B - A)", EditorStyles.boldLabel);
            EditorGUILayout.LabelField("A (base)", a.Label);
            EditorGUILayout.LabelField("B (new)", b.Label);

            long totalDelta = b.DisplayTotal - a.DisplayTotal;
            Rect rect = EditorGUILayout.GetControlRect(false, 18f);
            EditorGUI.LabelField(rect, "Total Size Delta",
                FBuildSizeUtil.FormatSignedBytes(totalDelta),
                totalDelta > 0 ? GetDeltaUpStyle() : totalDelta < 0 ? GetDeltaDownStyle() : EditorStyles.label);

            rect = EditorGUILayout.GetControlRect(false, 18f);
            EditorGUI.LabelField(rect, "Packed Assets Delta",
                FBuildSizeUtil.FormatSignedBytes(diffSummary.totalDelta),
                diffSummary.totalDelta > 0 ? GetDeltaUpStyle() : diffSummary.totalDelta < 0 ? GetDeltaDownStyle() : EditorStyles.label);

            EditorGUILayout.LabelField("Changes",
                $"Added {diffSummary.addedCount} ({FBuildSizeUtil.FormatSignedBytes(diffSummary.addedSize)})   " +
                $"Removed {diffSummary.removedCount} ({FBuildSizeUtil.FormatSignedBytes(-diffSummary.removedSize)})   " +
                $"Bigger {diffSummary.biggerCount}   Smaller {diffSummary.smallerCount}   Same {diffSummary.sameCount}");
            EditorGUILayout.EndVertical();
            EditorGUILayout.Space(4);
        }

        private void DrawDiffFilterToolbar()
        {
            EditorGUILayout.BeginHorizontal(EditorStyles.toolbar);
            DrawSearchField();

            GUILayout.Space(8);
            EditorGUI.BeginChangeCheck();
            bool nextOnlyChanged = GUILayout.Toggle(onlyChanged, "Only Changed", EditorStyles.toolbarButton, GUILayout.Width(94));
            bool nextOnlyOverBudget = GUILayout.Toggle(onlyOverBudget, "Only Over Budget", EditorStyles.toolbarButton, GUILayout.Width(114));
            if (EditorGUI.EndChangeCheck())
            {
                onlyChanged = nextOnlyChanged;
                onlyOverBudget = nextOnlyOverBudget;
                pageIndex = 0;
                viewDirty = true;
            }

            GUILayout.Space(8);
            GUILayout.Label("Sort", GUILayout.Width(32));
            EditorGUI.BeginChangeCheck();
            var nextSortType = (DiffSortType)EditorGUILayout.EnumPopup(diffSortType, EditorStyles.toolbarPopup, GUILayout.Width(110));
            var nextSortOrder = (SortOrder)EditorGUILayout.EnumPopup(diffSortOrder, EditorStyles.toolbarPopup, GUILayout.Width(94));
            if (EditorGUI.EndChangeCheck())
            {
                diffSortType = nextSortType;
                diffSortOrder = nextSortOrder;
                pageIndex = 0;
                viewDirty = true;
            }

            using (new EditorGUI.DisabledScope(displayDiffRows == null || displayDiffRows.Count == 0))
            {
                if (GUILayout.Button("Copy CSV", EditorStyles.toolbarButton, GUILayout.Width(70)))
                    CopyDiffRows(displayDiffRows);
            }

            GUILayout.FlexibleSpace();
            GUILayout.Label($"{(displayDiffRows != null ? displayDiffRows.Count : 0)}/{(allDiffRows != null ? allDiffRows.Count : 0)} shown", EditorStyles.miniLabel);
            EditorGUILayout.EndHorizontal();
        }

        private void DrawDiffHeaderRow()
        {
            Rect rect = GUILayoutUtility.GetRect(0f, 22f, GUILayout.ExpandWidth(true));
            GUI.Box(rect, GUIContent.none, EditorStyles.toolbar);
            Rect[] cols = GetColumnRects(rect, DiffColumnWidths, GetHeaderReserve(rect));

            if (DrawHeaderButton(cols[0], "Asset", diffSortType == DiffSortType.AssetFullPath, diffSortOrder))
                ToggleDiffSort(DiffSortType.AssetFullPath, false);
            if (DrawHeaderButton(cols[1], "Type", diffSortType == DiffSortType.Type, diffSortOrder))
                ToggleDiffSort(DiffSortType.Type, false);
            if (DrawHeaderButton(cols[2], "Status", diffSortType == DiffSortType.Status, diffSortOrder))
                ToggleDiffSort(DiffSortType.Status, false);
            if (DrawHeaderButton(cols[3], "Size A", diffSortType == DiffSortType.SizeA, diffSortOrder))
                ToggleDiffSort(DiffSortType.SizeA, true);
            if (DrawHeaderButton(cols[4], "Size B", diffSortType == DiffSortType.SizeB, diffSortOrder))
                ToggleDiffSort(DiffSortType.SizeB, true);
            if (DrawHeaderButton(cols[5], "Delta", diffSortType == DiffSortType.Delta, diffSortOrder))
                ToggleDiffSort(DiffSortType.Delta, true);
        }

        private void ToggleDiffSort(DiffSortType nextType, bool sizeSort)
        {
            if (diffSortType == nextType)
                diffSortOrder = diffSortOrder == SortOrder.Descending ? SortOrder.Ascending : SortOrder.Descending;
            else
            {
                diffSortType = nextType;
                diffSortOrder = sizeSort ? SortOrder.Descending : SortOrder.Ascending;
            }

            pageIndex = 0;
            scroll = Vector2.zero;
            viewDirty = true;
        }

        private void DrawDiffRow(FBuildDiffRow row)
        {
            Rect rect = GetRowRect();
            if (IsDiffRowOverBudget(FBuildSizeHistory.instance, row))
                EditorGUI.DrawRect(rect, OverBudgetTint);
            if (rect.Contains(Event.current.mousePosition))
                EditorGUI.DrawRect(rect, HoverTint);

            Rect[] cols = GetColumnRects(rect, DiffColumnWidths, 0f);
            DrawAssetPathCell(cols[0], rect, row.path);
            GUI.Label(InsetCell(cols[1]), row.typeName, EditorStyles.miniLabel);
            GUI.Label(InsetCell(cols[2]), row.status.ToString(), EditorStyles.miniLabel);
            GUI.Label(InsetCell(cols[3]), row.sizeA > 0 || row.status != FBuildDiffStatus.Added ? FBuildSizeUtil.FormatBytes(row.sizeA) : "-", EditorStyles.miniLabel);
            GUI.Label(InsetCell(cols[4]), row.sizeB > 0 || row.status != FBuildDiffStatus.Removed ? FBuildSizeUtil.FormatBytes(row.sizeB) : "-", EditorStyles.miniLabel);

            GUIStyle deltaStyle = row.delta > 0 ? GetDeltaUpStyle() : row.delta < 0 ? GetDeltaDownStyle() : EditorStyles.miniLabel;
            GUI.Label(InsetCell(cols[5]), FBuildSizeUtil.FormatSignedBytes(row.delta), deltaStyle);

            HandleAssetClick(rect, row.path);
        }

        // ---------------------------------------------------------------- Budget view

        private void DrawBudgetView()
        {
            FBuildSnapshot snapshot = CurrentSnapshot;
            if (snapshot == null)
            {
                DrawNoSnapshotHelp();
                return;
            }

            var history = FBuildSizeHistory.instance;

            if (!history.AnyAssetBudgetEnabled() && history.totalBuildBudget <= 0)
            {
                EditorGUILayout.HelpBox("All budgets are disabled. Set budgets in the Settings tab (0 = off).", MessageType.Info);
                return;
            }

            List<FBuildAssetRow> violations = snapshot.rows != null
                ? snapshot.rows.Where(history.IsOverBudget)
                    .OrderByDescending(r => r.packedSize - history.GetBudgetFor(r.category))
                    .ToList()
                : new List<FBuildAssetRow>();

            EditorGUILayout.BeginVertical(EditorStyles.helpBox);
            EditorGUILayout.LabelField("Budget Report", EditorStyles.boldLabel);
            EditorGUILayout.LabelField("Build", snapshot.Label);

            if (history.totalBuildBudget > 0)
            {
                long over = snapshot.DisplayTotal - history.totalBuildBudget;
                Rect line = EditorGUILayout.GetControlRect(false, 18f);
                EditorGUI.LabelField(line, "Total Build",
                    over > 0
                        ? $"{FBuildSizeUtil.FormatBytes(snapshot.DisplayTotal)} / {FBuildSizeUtil.FormatBytes(history.totalBuildBudget)} — OVER by {FBuildSizeUtil.FormatBytes(over)}"
                        : $"{FBuildSizeUtil.FormatBytes(snapshot.DisplayTotal)} / {FBuildSizeUtil.FormatBytes(history.totalBuildBudget)} — OK",
                    over > 0 ? GetWarningCountStyle() : EditorStyles.label);
            }

            if (history.AnyAssetBudgetEnabled())
                EditorGUILayout.LabelField("Asset Violations", violations.Count.ToString());
            EditorGUILayout.EndVertical();
            EditorGUILayout.Space(4);

            if (violations.Count == 0)
            {
                EditorGUILayout.HelpBox("No budget violations in this build.", MessageType.Info);
                return;
            }

            Rect rect = GUILayoutUtility.GetRect(0f, 22f, GUILayout.ExpandWidth(true));
            GUI.Box(rect, GUIContent.none, EditorStyles.toolbar);
            Rect[] cols = GetColumnRects(rect, BudgetColumnWidths, GetHeaderReserve(rect));
            GUI.Label(cols[0], "Asset", EditorStyles.toolbarButton);
            GUI.Label(cols[1], "Type", EditorStyles.toolbarButton);
            GUI.Label(cols[2], "Packed Size", EditorStyles.toolbarButton);
            GUI.Label(cols[3], "Budget", EditorStyles.toolbarButton);
            GUI.Label(cols[4], "Over By", EditorStyles.toolbarButton);

            scroll = EditorGUILayout.BeginScrollView(scroll);
            foreach (FBuildAssetRow row in violations)
            {
                Rect rowRect = GetRowRect();
                EditorGUI.DrawRect(rowRect, OverBudgetTint);
                if (rowRect.Contains(Event.current.mousePosition))
                    EditorGUI.DrawRect(rowRect, HoverTint);

                long budget = history.GetBudgetFor(row.category);
                Rect[] rowCols = GetColumnRects(rowRect, BudgetColumnWidths, 0f);
                DrawAssetPathCell(rowCols[0], rowRect, row.path);
                GUI.Label(InsetCell(rowCols[1]), row.typeName, EditorStyles.miniLabel);
                GUI.Label(InsetCell(rowCols[2]), FBuildSizeUtil.FormatBytes(row.packedSize), EditorStyles.miniLabel);
                GUI.Label(InsetCell(rowCols[3]), FBuildSizeUtil.FormatBytes(budget), EditorStyles.miniLabel);
                GUI.Label(InsetCell(rowCols[4]), FBuildSizeUtil.FormatSignedBytes(row.packedSize - budget), GetDeltaUpStyle());

                HandleAssetClick(rowRect, row.path);
            }
            EditorGUILayout.EndScrollView();
        }

        // ---------------------------------------------------------------- Settings view

        private void DrawSettingsView()
        {
            var history = FBuildSizeHistory.instance;

            EditorGUILayout.BeginVertical(EditorStyles.helpBox);
            EditorGUILayout.LabelField("Budgets (MB, 0 = off)", EditorStyles.boldLabel);

            EditorGUI.BeginChangeCheck();
            long nextDefault = DrawBudgetFieldMB("Default Asset Budget", history.defaultAssetBudget);
            long nextTexture = DrawBudgetFieldMB("Texture Budget", history.textureBudget);
            long nextMesh = DrawBudgetFieldMB("Mesh Budget", history.meshBudget);
            long nextAudio = DrawBudgetFieldMB("Audio Budget", history.audioBudget);
            long nextOther = DrawBudgetFieldMB("Other Budget", history.otherBudget);
            long nextTotal = DrawBudgetFieldMB("Total Build Budget", history.totalBuildBudget);
            bool nextLog = EditorGUILayout.Toggle("Log Warnings After Build", history.logWarningsAfterBuild);
            if (EditorGUI.EndChangeCheck())
            {
                history.defaultAssetBudget = nextDefault;
                history.textureBudget = nextTexture;
                history.meshBudget = nextMesh;
                history.audioBudget = nextAudio;
                history.otherBudget = nextOther;
                history.totalBuildBudget = nextTotal;
                history.logWarningsAfterBuild = nextLog;
                history.SaveHistory();
                viewDirty = true;
            }

            GUILayout.Label("Per-category budgets override the default; 0 falls back to the default asset budget.", EditorStyles.miniLabel);
            EditorGUILayout.EndVertical();
            EditorGUILayout.Space(4);

            EditorGUILayout.BeginVertical(EditorStyles.helpBox);
            EditorGUILayout.LabelField("History", EditorStyles.boldLabel);

            EditorGUI.BeginChangeCheck();
            int nextMax = EditorGUILayout.IntSlider("Max Snapshots", history.maxSnapshots, 2, 30);
            if (EditorGUI.EndChangeCheck())
            {
                history.maxSnapshots = nextMax;
                history.TrimToMax();
                history.SaveHistory();
                historyStamp = long.MinValue;
            }

            if (history.HasData)
            {
                int deleteIndex = -1;
                for (int i = history.snapshots.Count - 1; i >= 0; i--)
                {
                    EditorGUILayout.BeginHorizontal();
                    GUILayout.Label(history.snapshots[i].Label, EditorStyles.miniLabel);
                    GUILayout.FlexibleSpace();
                    if (GUILayout.Button("Delete", EditorStyles.miniButton, GUILayout.Width(56)))
                        deleteIndex = i;
                    EditorGUILayout.EndHorizontal();
                }

                if (deleteIndex >= 0)
                {
                    history.RemoveSnapshotAt(deleteIndex);
                    historyStamp = long.MinValue;
                }

                EditorGUILayout.Space(4);
                if (GUILayout.Button("Clear History", GUILayout.Width(110)) &&
                    EditorUtility.DisplayDialog("Clear History", "Delete all build snapshots?", "Clear", "Cancel"))
                {
                    history.ClearHistory();
                    historyStamp = long.MinValue;
                }
            }
            else
            {
                GUILayout.Label("No snapshots recorded yet.", EditorStyles.miniLabel);
            }

            EditorGUILayout.EndVertical();
        }

        private static long DrawBudgetFieldMB(string label, long bytes)
        {
            double mb = bytes / (double)BytesPerMB;
            double next = EditorGUILayout.DelayedDoubleField(label, Math.Round(mb, 3));
            if (next < 0) next = 0;
            return (long)Math.Round(next * BytesPerMB);
        }

        // ---------------------------------------------------------------- shared helpers

        private void DrawSearchField()
        {
            GUILayout.Label("Search", GUILayout.Width(46));
            string nextSearch = GUILayout.TextField(searchText ?? string.Empty, GetToolbarSearchStyle(), GUILayout.MinWidth(90));
            if (nextSearch != searchText)
            {
                searchText = nextSearch;
                pageIndex = 0;
                viewDirty = true;
            }

            using (new EditorGUI.DisabledScope(string.IsNullOrEmpty(searchText)))
            {
                if (GUILayout.Button("Clear", EditorStyles.toolbarButton, GUILayout.Width(46)))
                {
                    searchText = string.Empty;
                    pageIndex = 0;
                    viewDirty = true;
                }
            }
        }

        private void DrawPageToolbar(int count)
        {
            int pageCount = GetPageCount(count);
            ClampPage(count);

            int first = count == 0 ? 0 : pageIndex * pageSize + 1;
            int last = Math.Min(count, (pageIndex + 1) * pageSize);

            EditorGUILayout.BeginHorizontal(EditorStyles.toolbar);
            using (new EditorGUI.DisabledScope(pageIndex <= 0))
            {
                if (GUILayout.Button("First", EditorStyles.toolbarButton, GUILayout.Width(44)))
                {
                    pageIndex = 0;
                    scroll = Vector2.zero;
                }

                if (GUILayout.Button("Prev", EditorStyles.toolbarButton, GUILayout.Width(42)))
                {
                    pageIndex--;
                    scroll = Vector2.zero;
                }
            }

            GUILayout.Label("Page", GUILayout.Width(34));
            EditorGUI.BeginChangeCheck();
            int nextPage = EditorGUILayout.IntField(pageIndex + 1, GUILayout.Width(44));
            if (EditorGUI.EndChangeCheck())
            {
                pageIndex = Mathf.Clamp(nextPage - 1, 0, pageCount - 1);
                scroll = Vector2.zero;
            }

            GUILayout.Label($"/ {pageCount}", GUILayout.Width(48));

            using (new EditorGUI.DisabledScope(pageIndex >= pageCount - 1))
            {
                if (GUILayout.Button("Next", EditorStyles.toolbarButton, GUILayout.Width(42)))
                {
                    pageIndex++;
                    scroll = Vector2.zero;
                }

                if (GUILayout.Button("Last", EditorStyles.toolbarButton, GUILayout.Width(42)))
                {
                    pageIndex = pageCount - 1;
                    scroll = Vector2.zero;
                }
            }

            GUILayout.Space(8);
            GUILayout.Label("Rows", GUILayout.Width(34));
            EditorGUI.BeginChangeCheck();
            int nextPageSize = EditorGUILayout.IntPopup(pageSize, PageSizeLabels, PageSizeValues, EditorStyles.toolbarPopup, GUILayout.Width(58));
            if (EditorGUI.EndChangeCheck())
            {
                pageSize = nextPageSize;
                pageIndex = 0;
                scroll = Vector2.zero;
            }

            GUILayout.FlexibleSpace();
            GUILayout.Label($"Showing {first}-{last} / {count}", EditorStyles.miniLabel, GUILayout.Width(150));
            EditorGUILayout.EndHorizontal();
        }

        private IEnumerable<T> GetPage<T>(List<T> rows)
        {
            if (rows == null || rows.Count == 0)
                return Enumerable.Empty<T>();

            ClampPage(rows.Count);
            return rows.Skip(pageIndex * pageSize).Take(pageSize);
        }

        private int GetPageCount(int count)
        {
            return Math.Max(1, Mathf.CeilToInt(count / (float)Mathf.Max(1, pageSize)));
        }

        private void ClampPage(int count)
        {
            pageSize = Mathf.Max(1, pageSize);
            int pageCount = GetPageCount(count);
            pageIndex = Mathf.Clamp(pageIndex, 0, pageCount - 1);
        }

        private static bool DrawHeaderButton(Rect rect, string label, bool isActive, SortOrder order)
        {
            string marker = isActive ? order == SortOrder.Descending ? " v" : " ^" : string.Empty;
            return GUI.Button(rect, label + marker, EditorStyles.toolbarButton);
        }

        private static void DrawAssetPathCell(Rect cellRect, Rect rowRect, string path)
        {
            bool generated = FBuildSizeUtil.IsGeneratedPath(path);
            Rect pathRect = cellRect;

            if (!generated)
            {
                Rect iconRect = new Rect(cellRect.x + 3f, rowRect.y + 3f, 16f, 16f);
                Texture icon = AssetDatabase.GetCachedIcon(path);
                if (icon != null) GUI.DrawTexture(iconRect, icon);
                pathRect.xMin += IconAndPaddingWidth;
            }
            else
            {
                pathRect.xMin += 4f;
            }

            pathRect.xMax -= 4f;
            pathRect.y += 2f;
            pathRect.height = 18f;
            GUI.Label(pathRect, new GUIContent(path, path), EditorStyles.miniLabel);
        }

        // Header rows live outside the scroll view while data rows live inside it, so
        // their layout widths differ (scrollbar + margins). Rows are measured on repaint
        // and the header shrinks by the exact difference to keep columns aligned.
        private Rect GetRowRect()
        {
            Rect rect = GUILayoutUtility.GetRect(0f, 22f, GUILayout.ExpandWidth(true));
            if (Event.current.type == EventType.Repaint)
                measuredRowWidth = rect.width;
            return rect;
        }

        private float GetHeaderReserve(Rect headerRect)
        {
            return measuredRowWidth > 0f
                ? Mathf.Clamp(headerRect.width - measuredRowWidth, 0f, 40f)
                : ScrollbarReserveWidth;
        }

        private static Rect[] GetColumnRects(Rect rect, float[] fixedWidths, float reserve)
        {
            rect.xMax -= Mathf.Min(reserve, Mathf.Max(0f, rect.width - 1f));

            float fixedTotal = 0f;
            foreach (float width in fixedWidths)
                fixedTotal += width;

            float availableWidth = Mathf.Max(1f, rect.width);
            float assetWidth = Mathf.Max(80f, availableWidth - fixedTotal);
            float remaining = availableWidth - assetWidth;

            if (remaining < fixedTotal)
            {
                float minAssetWidth = availableWidth >= 220f
                    ? 80f
                    : Mathf.Max(40f, availableWidth * 0.35f);

                assetWidth = Mathf.Min(availableWidth, minAssetWidth);
                remaining = Mathf.Max(0f, availableWidth - assetWidth);
            }

            float scale = fixedTotal > 0f ? remaining / fixedTotal : 0f;
            var rects = new Rect[fixedWidths.Length + 1];
            float x = rect.x;
            rects[0] = new Rect(x, rect.y, assetWidth, rect.height);
            x += assetWidth;

            for (int i = 0; i < fixedWidths.Length; i++)
            {
                float width = i == fixedWidths.Length - 1
                    ? Mathf.Max(0f, rect.xMax - x)
                    : fixedWidths[i] * scale;
                rects[i + 1] = new Rect(x, rect.y, width, rect.height);
                x += width;
            }

            return rects;
        }

        private static Rect InsetCell(Rect rect)
        {
            rect.xMin += 4f;
            rect.xMax -= 4f;
            rect.y += 2f;
            rect.height = 18f;
            return rect;
        }

        private static void HandleAssetClick(Rect rect, string path)
        {
            if (FBuildSizeUtil.IsGeneratedPath(path)) return;

            Event e = Event.current;
            if (e.type != EventType.MouseDown || !rect.Contains(e.mousePosition)) return;

            Object obj = AssetDatabase.LoadAssetAtPath<Object>(path);
            if (obj != null)
            {
                Selection.activeObject = obj;
                EditorGUIUtility.PingObject(obj);
                if (e.clickCount == 2)
                    AssetDatabase.OpenAsset(obj);
            }

            e.Use();
        }

        private static void CopyAssetRows(IEnumerable<FBuildAssetRow> rows)
        {
            EditorGUIUtility.systemCopyBuffer = string.Join(
                "\n",
                rows.Select(r => string.Join(
                    ",",
                    CsvEscape(r.path),
                    CsvEscape(r.typeName),
                    r.packedSize.ToString(CultureInfo.InvariantCulture),
                    r.percent.ToString("0.##", CultureInfo.InvariantCulture))));
        }

        private static void CopyDiffRows(IEnumerable<FBuildDiffRow> rows)
        {
            EditorGUIUtility.systemCopyBuffer = string.Join(
                "\n",
                rows.Select(r => string.Join(
                    ",",
                    CsvEscape(r.path),
                    CsvEscape(r.typeName),
                    r.status.ToString(),
                    r.sizeA.ToString(CultureInfo.InvariantCulture),
                    r.sizeB.ToString(CultureInfo.InvariantCulture),
                    r.delta.ToString(CultureInfo.InvariantCulture))));
        }

        private static bool Contains(string source, string value)
        {
            return !string.IsNullOrEmpty(source) &&
                   !string.IsNullOrEmpty(value) &&
                   source.IndexOf(value, StringComparison.OrdinalIgnoreCase) >= 0;
        }

        private static string CsvEscape(string value)
        {
            if (string.IsNullOrEmpty(value))
                return string.Empty;

            bool mustQuote = value.IndexOfAny(new[] { ',', '"', '\r', '\n' }) >= 0;
            if (!mustQuote)
                return value;

            return "\"" + value.Replace("\"", "\"\"") + "\"";
        }

        private static GUIStyle GetToolbarSearchStyle()
        {
            return GUI.skin.FindStyle("ToolbarSeachTextField") ?? EditorStyles.toolbarTextField;
        }

        private GUIStyle GetDeltaUpStyle()
        {
            if (deltaUpStyle == null)
                deltaUpStyle = new GUIStyle(EditorStyles.miniLabel)
                {
                    normal = { textColor = new Color(0.95f, 0.4f, 0.3f) },
                    hover = { textColor = new Color(0.95f, 0.4f, 0.3f) }
                };
            return deltaUpStyle;
        }

        private GUIStyle GetDeltaDownStyle()
        {
            if (deltaDownStyle == null)
                deltaDownStyle = new GUIStyle(EditorStyles.miniLabel)
                {
                    normal = { textColor = new Color(0.35f, 0.8f, 0.4f) },
                    hover = { textColor = new Color(0.35f, 0.8f, 0.4f) }
                };
            return deltaDownStyle;
        }

        private GUIStyle GetWarningCountStyle()
        {
            if (warningCountStyle == null)
                warningCountStyle = new GUIStyle(EditorStyles.label)
                {
                    normal = { textColor = new Color(0.95f, 0.4f, 0.3f) },
                    hover = { textColor = new Color(0.95f, 0.4f, 0.3f) },
                    fontStyle = FontStyle.Bold
                };
            return warningCountStyle;
        }
    }
}
