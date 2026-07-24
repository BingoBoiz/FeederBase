using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Sirenix.OdinInspector;
using UnityEditor;
using UnityEngine;
using Object = UnityEngine.Object;

namespace Feeder
{
    public sealed class FFbxUnpackMode : FRefModeBase
    {
        private List<Object> sourceFbxList => FDataPersistenceService.GetOrCreateDataContainer().FbxUnpackSources;

        private string saveFolderPath
        {
            get => FToolPrefs.GetString(nameof(FFbxUnpackMode), nameof(saveFolderPath), "Assets");
            set => FToolPrefs.SetString(nameof(FFbxUnpackMode), nameof(saveFolderPath), value);
        }

        [NonSerialized] private List<FFbxUnpackPlan> plans = new List<FFbxUnpackPlan>();
        [NonSerialized] private FFbxUnpackBatchResult lastBatchResult;
        [NonSerialized] private bool needsScan;
        [NonSerialized] private string searchText;
        [NonSerialized] private Vector2 scroll;
        [NonSerialized] private FFbxUnpackPlan selectedPlan;
        [NonSerialized] private FFbxSubAssetInfo selectedSubAsset;
        [NonSerialized] private bool showLogs;
        [NonSerialized] private bool initialized;

        private static readonly Color RowHover = new Color(0.25f, 0.55f, 1f, 0.10f);
        private static readonly Color RowSelected = new Color(0.25f, 0.55f, 1f, 0.28f);
        private static readonly Color DropNormal = new Color(0.18f, 0.55f, 0.95f, 0.14f);
        private static readonly Color DropHover = new Color(0.18f, 0.75f, 1f, 0.26f);
        private static readonly Color StatusExtracted = new Color(0.45f, 0.9f, 0.55f);
        private static readonly Color StatusFailed = new Color(1f, 0.45f, 0.45f);
        private static readonly Color StatusPending = new Color(1f, 0.85f, 0.4f);
        private static readonly Color StatusUnused = new Color(0.7f, 0.7f, 0.7f);

        protected override string GetDescription() =>
            "Unpack one or more FBX into standalone assets, then remap every prefab/scene/asset reference so the source FBX is left unused. Select a sub-asset to see where the project uses it.";

        [OnInspectorGUI]
        private void Draw()
        {
            if (!initialized)
            {
                initialized = true;
                if (sourceFbxList.Count > 0) needsScan = true;
            }

            EnsureScanned();

            DrawInputPanel();
            EditorGUILayout.Space(4);
            DrawToolbar();
            EditorGUILayout.Space(4);
            DrawSummary();
            EditorGUILayout.Space(4);
            DrawResults();
        }

        // ---------------------------------------------------------------- input

        private void DrawInputPanel()
        {
            EditorGUILayout.BeginVertical(EditorStyles.helpBox);

            EditorGUILayout.LabelField($"Source FBX ({sourceFbxList.Count})", EditorStyles.boldLabel);
            DrawDropArea();
            DrawFbxList();
            DrawInputActions();
            DrawSaveFolder();

            EditorGUILayout.EndVertical();
        }

        private void DrawDropArea()
        {
            Rect rect = GUILayoutUtility.GetRect(0, 40f, GUILayout.ExpandWidth(true));
            Event e = Event.current;
            bool hover = rect.Contains(e.mousePosition);

            EditorGUI.DrawRect(rect, hover ? DropHover : DropNormal);
            GUI.Box(rect, GUIContent.none, EditorStyles.helpBox);
            var style = new GUIStyle(EditorStyles.centeredGreyMiniLabel)
                { fontSize = 12, fontStyle = FontStyle.Bold, alignment = TextAnchor.MiddleCenter };
            GUI.Label(rect, "Drop FBX files or folders here", style);

            if (!hover) return;
            if (e.type == EventType.DragUpdated)
            {
                DragAndDrop.visualMode = HasFbx(DragAndDrop.objectReferences)
                    ? DragAndDropVisualMode.Copy
                    : DragAndDropVisualMode.Rejected;
                e.Use();
            }
            else if (e.type == EventType.DragPerform)
            {
                DragAndDrop.AcceptDrag();
                AddObjects(DragAndDrop.objectReferences);
                e.Use();
            }
        }

        private void DrawFbxList()
        {
            if (sourceFbxList.Count == 0)
            {
                EditorGUILayout.HelpBox("No FBX added. Drop FBX/folders above or click Add Selection.", MessageType.Info);
                return;
            }

            int removeAt = -1;
            for (int i = 0; i < sourceFbxList.Count; i++)
            {
                EditorGUILayout.BeginHorizontal();
                GUILayout.Label((i + 1).ToString(), EditorStyles.miniLabel, GUILayout.Width(22));

                EditorGUI.BeginChangeCheck();
                Object newObj = EditorGUILayout.ObjectField(sourceFbxList[i], typeof(Object), false);
                if (EditorGUI.EndChangeCheck())
                {
                    if (newObj == null || IsFbx(newObj))
                    {
                        sourceFbxList[i] = newObj;
                        MarkDirtyScan();
                    }
                    else
                    {
                        Debug.LogWarning("[FBX Unpack] Only .fbx assets are accepted.");
                    }
                }

                if (GUILayout.Button("X", EditorStyles.miniButton, GUILayout.Width(22)))
                    removeAt = i;

                EditorGUILayout.EndHorizontal();
            }

            if (removeAt >= 0)
            {
                sourceFbxList.RemoveAt(removeAt);
                MarkDirtyScan();
            }
        }

        private void DrawInputActions()
        {
            EditorGUILayout.BeginHorizontal();

            using (new EditorGUI.DisabledScope(!HasFbx(Selection.objects)))
            {
                if (GUILayout.Button("Add Selection", GUILayout.Height(20)))
                    AddObjects(Selection.objects);
            }

            using (new EditorGUI.DisabledScope(!sourceFbxList.Any(f => f == null)))
            {
                if (GUILayout.Button("Remove Nulls", GUILayout.Height(20), GUILayout.Width(105)))
                {
                    sourceFbxList.RemoveAll(f => f == null);
                    MarkDirtyScan();
                }
            }

            using (new EditorGUI.DisabledScope(sourceFbxList.Count == 0))
            {
                if (GUILayout.Button("Clear", GUILayout.Height(20), GUILayout.Width(60)))
                {
                    sourceFbxList.Clear();
                    MarkDirtyScan();
                }
            }

            EditorGUILayout.EndHorizontal();
        }

        private void DrawSaveFolder()
        {
            EditorGUILayout.BeginHorizontal();
            EditorGUILayout.LabelField("Save Folder", GUILayout.Width(80));

            EditorGUI.BeginChangeCheck();
            string folder = EditorGUILayout.TextField(saveFolderPath);
            if (EditorGUI.EndChangeCheck())
            {
                saveFolderPath = folder;
                MarkDirtyScan();
            }

            if (GUILayout.Button("Pick", GUILayout.Width(50)))
            {
                string start = AssetDatabase.IsValidFolder(saveFolderPath) ? saveFolderPath : "Assets";
                string abs = EditorUtility.OpenFolderPanel("Save Folder", start, string.Empty);
                string rel = ToProjectRelative(abs);
                if (!string.IsNullOrEmpty(rel))
                {
                    saveFolderPath = rel;
                    MarkDirtyScan();
                }
                else if (!string.IsNullOrEmpty(abs))
                {
                    Debug.LogWarning("[FBX Unpack] Save folder must be inside the project's Assets folder.");
                }
            }

            EditorGUILayout.EndHorizontal();
        }

        // -------------------------------------------------------------- toolbar

        private void DrawToolbar()
        {
            EditorGUILayout.BeginHorizontal(EditorStyles.toolbar);

            using (new EditorGUI.DisabledScope(sourceFbxList.Count == 0))
            {
                if (GUILayout.Button("Rescan", EditorStyles.toolbarButton, GUILayout.Width(70)))
                {
                    if (RequireDatabaseReady()) ForceScan();
                }
            }

            using (new EditorGUI.DisabledScope(!HasApplyable()))
            {
                if (GUILayout.Button("Apply", EditorStyles.toolbarButton, GUILayout.Width(64)))
                    RunApply();
            }

            GUILayout.FlexibleSpace();
            GUILayout.Label("Search", GUILayout.Width(46));
            searchText = GUILayout.TextField(searchText ?? string.Empty, GetToolbarSearchStyle(), GUILayout.Width(200));
            using (new EditorGUI.DisabledScope(string.IsNullOrEmpty(searchText)))
            {
                if (GUILayout.Button("Clear", EditorStyles.toolbarButton, GUILayout.Width(46)))
                    searchText = string.Empty;
            }

            EditorGUILayout.EndHorizontal();
        }

        // -------------------------------------------------------------- summary

        private void DrawSummary()
        {
            if (!Db.IsReady)
            {
                EditorGUILayout.HelpBox(
                    "Asset Cache database is not ready. Open Scan Database and click Scan / Refresh Database first.",
                    MessageType.Warning);
                return;
            }

            if (plans.Count == 0)
            {
                EditorGUILayout.HelpBox("Add FBX files, then references are scanned automatically.", MessageType.Info);
                return;
            }

            int subAssets = plans.Sum(p => p.SubAssets.Count);
            int refRows = plans.Sum(p => p.ReferenceHits.Count);
            int refAssets = plans.SelectMany(p => p.ReferenceHits.Select(h => h.AssetPath)).Distinct().Count();
            EditorGUILayout.LabelField(
                $"{plans.Count} FBX | {subAssets} sub-assets | {refRows} reference rows in {refAssets} assets",
                EditorStyles.miniBoldLabel);

            DrawVerificationBanner();
        }

        private void DrawVerificationBanner()
        {
            if (lastBatchResult == null) return;
            FFbxUnpackBatchResult b = lastBatchResult;

            string msg =
                $"Last Apply — extracted {b.TotalExtracted}, touched {b.TotalTouched}, replaced {b.TotalReplaced} ref(s); " +
                $"fully severed {b.FullySeveredCount}/{b.Results.Count} FBX.";
            if (b.TotalRemaining > 0)
                msg += $" {b.TotalRemaining} asset(s) still reference an FBX.";
            if (b.AbortedCount > 0)
                msg += $" {b.AbortedCount} FBX aborted (extraction errors).";

            MessageType type = b.AbortedCount > 0 ? MessageType.Error
                : b.AllVerified ? MessageType.Info
                : MessageType.Warning;
            EditorGUILayout.HelpBox(msg, type);
        }

        // -------------------------------------------------------------- results

        private void DrawResults()
        {
            if (plans.Count == 0) return;

            scroll = EditorGUILayout.BeginScrollView(scroll);

            foreach (FFbxUnpackPlan plan in plans)
                DrawPlan(plan);

            DrawLogs();

            EditorGUILayout.EndScrollView();
        }

        private void DrawPlan(FFbxUnpackPlan plan)
        {
            EditorGUILayout.Space(4);
            EditorGUILayout.BeginVertical(EditorStyles.helpBox);

            int refAssets = plan.ReferenceHits.Select(h => h.AssetPath).Distinct().Count();
            EditorGUILayout.LabelField(
                $"{plan.SourceFbxName}   —   {plan.SubAssets.Count} sub-assets, {plan.ReferenceHits.Sum(h => h.Count)} refs in {refAssets} assets",
                EditorStyles.boldLabel);

            foreach (string warning in plan.Warnings)
                EditorGUILayout.HelpBox(warning, MessageType.Warning);

            DrawSubAssetTable(plan);

            if (selectedSubAsset != null && ReferenceEquals(selectedPlan, plan))
                DrawWhereUsed(plan, selectedSubAsset);

            EditorGUILayout.EndVertical();
        }

        private void DrawSubAssetTable(FFbxUnpackPlan plan)
        {
            List<FFbxSubAssetInfo> rows = FilterSubAssets(plan.SubAssets);

            Rect header = EditorGUILayout.GetControlRect(false, 18f);
            var c = new Columns(header, false);
            GUI.Label(c.name, "Name", EditorStyles.miniBoldLabel);
            GUI.Label(c.type, "Type", EditorStyles.miniBoldLabel);
            GUI.Label(c.id, "Local ID", EditorStyles.miniBoldLabel);
            GUI.Label(c.used, "Used By", EditorStyles.miniBoldLabel);
            GUI.Label(c.status, "Status", EditorStyles.miniBoldLabel);

            if (plan.SubAssets.Count == 0)
            {
                EditorGUILayout.HelpBox("No supported FBX sub-assets found.", MessageType.Warning);
                return;
            }
            if (rows.Count == 0)
            {
                EditorGUILayout.LabelField("   (no sub-assets match the search)", EditorStyles.miniLabel);
                return;
            }

            foreach (FFbxSubAssetInfo info in rows)
            {
                Rect rect = EditorGUILayout.GetControlRect(false, 20f);
                bool selected = ReferenceEquals(info, selectedSubAsset);
                if (selected) EditorGUI.DrawRect(rect, RowSelected);
                else if (rect.Contains(Event.current.mousePosition)) EditorGUI.DrawRect(rect, RowHover);

                Texture icon = EditorGUIUtility.ObjectContent(
                    info.SourceObject, info.SourceObject != null ? info.SourceObject.GetType() : typeof(Object)).image;
                if (icon != null) GUI.DrawTexture(new Rect(rect.x + 3f, rect.y + 2f, 16f, 16f), icon);

                var col = new Columns(rect, true);
                GUI.Label(col.name, new GUIContent(info.DisplayName, info.Key.ToString()));
                GUI.Label(col.type, info.DisplayType, EditorStyles.miniLabel);
                GUI.Label(col.id, info.Key.LocalFileId.ToString(), EditorStyles.miniLabel);
                GUI.Label(col.used, info.UsedByCount > 0 ? info.UsedByCount.ToString() : "-", EditorStyles.miniLabel);
                DrawStatus(col.status, info);

                HandleRowClick(rect, plan, info);
            }
        }

        private static void DrawStatus(Rect rect, FFbxSubAssetInfo info)
        {
            Color prev = GUI.contentColor;
            switch (info.ExtractStatus)
            {
                case "Extracted": GUI.contentColor = StatusExtracted; break;
                case "Failed": GUI.contentColor = StatusFailed; break;
                case "Pending": GUI.contentColor = StatusPending; break;
                default: GUI.contentColor = StatusUnused; break;
            }
            GUIContent content = string.IsNullOrEmpty(info.ExtractError)
                ? new GUIContent(info.ExtractStatus, info.ExtractedPath)
                : new GUIContent(info.ExtractStatus, info.ExtractError);
            GUI.Label(rect, content, EditorStyles.miniLabel);
            GUI.contentColor = prev;
        }

        private void DrawWhereUsed(FFbxUnpackPlan plan, FFbxSubAssetInfo info)
        {
            List<FFbxReferenceHit> hits = plan.GetHitsForKey(info.Key);

            EditorGUILayout.Space(2);
            int assetCount = hits.Select(h => h.AssetPath).Distinct().Count();
            EditorGUILayout.LabelField(
                $"Used by  —  {info.DisplayName} <{info.DisplayType}>   ({hits.Sum(h => h.Count)} refs in {assetCount} assets)",
                EditorStyles.boldLabel);

            if (hits.Count == 0)
            {
                EditorGUILayout.HelpBox(
                    "Not referenced by any scanned asset. It is either unused (safe to drop) or referenced only dynamically (Resources/Addressables).",
                    MessageType.Info);
                return;
            }

            FRefResultGUI.DrawAssetPathActions(hits.Select(h => h.AssetPath));
            foreach (IGrouping<string, FFbxReferenceHit> group in hits.GroupBy(h => h.AssetPath).OrderBy(g => g.Key))
            {
                int count = group.Sum(h => h.Count);
                string kind = group.First().ReferenceKind;
                EditorGUILayout.LabelField($"{kind}  •  x{count}", EditorStyles.miniLabel);
                FRefResultGUI.DrawAssetRow(group.Key, 1);
            }
        }

        private void DrawLogs()
        {
            if (lastBatchResult == null) return;

            EditorGUILayout.Space(4);
            showLogs = EditorGUILayout.Foldout(showLogs, "Apply Log", true);
            if (!showLogs) return;

            foreach (FFbxUnpackApplyResult r in lastBatchResult.Results)
            {
                EditorGUILayout.LabelField(Path.GetFileName(r.SourceFbxPath ?? "(unknown)"), EditorStyles.miniBoldLabel);

                foreach (string error in r.Errors)
                    EditorGUILayout.HelpBox(error, MessageType.Error);

                foreach (string referrer in r.RemainingReferrers)
                    EditorGUILayout.LabelField($"   still referenced by: {referrer}", EditorStyles.wordWrappedMiniLabel);

                foreach (string log in r.Logs)
                    EditorGUILayout.LabelField(log, EditorStyles.wordWrappedMiniLabel);
            }
        }

        // ---------------------------------------------------------------- apply

        private void RunApply()
        {
            if (!RequireDatabaseReady()) return;
            ForceScan();

            List<FFbxUnpackPlan> applyable = plans.Where(p => p != null && p.HasReferences).ToList();
            if (applyable.Count == 0)
            {
                EditorUtility.DisplayDialog("FBX Unpack", "No FBX sub-asset references found to remap.", "OK");
                return;
            }

            int fileCount = applyable.SelectMany(p => p.ReferenceHits.Select(h => h.AssetPath)).Distinct().Count();
            bool confirmed = EditorUtility.DisplayDialog(
                "FBX Unpack",
                $"Unpack {applyable.Count} FBX, create standalone assets and modify {fileCount} asset file(s).\n\n" +
                "Commit your work first — this rewrites prefabs/scenes/assets. The source FBX files are kept (left unreferenced).",
                "Apply",
                "Cancel");
            if (!confirmed) return;

            try
            {
                lastBatchResult = FFbxUnpackService.ApplyBatch(applyable);
                ForceScan();
                ShowApplySummary(lastBatchResult);
            }
            catch (Exception ex)
            {
                EditorUtility.DisplayDialog("FBX Unpack", ex.Message, "OK");
            }
        }

        private static void ShowApplySummary(FFbxUnpackBatchResult batch)
        {
            string msg =
                $"Extracted: {batch.TotalExtracted}\n" +
                $"Touched assets: {batch.TotalTouched}\n" +
                $"Replaced refs: {batch.TotalReplaced}\n" +
                $"Fully severed: {batch.FullySeveredCount}/{batch.Results.Count} FBX\n" +
                $"Remaining referrers: {batch.TotalRemaining}";
            if (batch.AbortedCount > 0)
                msg += $"\nAborted (extraction errors): {batch.AbortedCount}";
            EditorUtility.DisplayDialog("FBX Unpack", msg, "OK");
        }

        // --------------------------------------------------------------- scan

        private void MarkDirtyScan()
        {
            needsScan = true;
            // sourceFbxList mutate trực tiếp trên list của container (Add/Remove/indexer) → flush ra disk ở đây.
            FDataPersistenceService.SaveData(FDataPersistenceService.GetOrCreateDataContainer());
        }

        private void EnsureScanned()
        {
            if (!needsScan || !Db.IsReady) return;
            ForceScan();
        }

        private void ForceScan()
        {
            FFbxSubAssetKey? keep = selectedSubAsset?.Key;
            plans = FFbxUnpackService.BuildPreviewBatch(sourceFbxList, saveFolderPath);
            needsScan = false;

            selectedPlan = null;
            selectedSubAsset = null;
            if (keep == null) return;

            foreach (FFbxUnpackPlan plan in plans)
            foreach (FFbxSubAssetInfo info in plan.SubAssets)
                if (info.Key.Equals(keep.Value))
                {
                    selectedPlan = plan;
                    selectedSubAsset = info;
                    return;
                }
        }

        // ------------------------------------------------------------- helpers

        private void AddObjects(IEnumerable<Object> objects)
        {
            if (objects == null) return;
            bool changed = false;

            foreach (Object obj in objects)
            {
                if (obj == null) continue;
                string path = AssetDatabase.GetAssetPath(obj);
                if (string.IsNullOrEmpty(path)) continue;

                if (AssetDatabase.IsValidFolder(path))
                {
                    foreach (string guid in AssetDatabase.FindAssets("t:GameObject", new[] { path }))
                    {
                        string p = AssetDatabase.GUIDToAssetPath(guid);
                        if (p.EndsWith(".fbx", StringComparison.OrdinalIgnoreCase))
                            changed |= AddFbxPath(p);
                    }
                }
                else if (path.EndsWith(".fbx", StringComparison.OrdinalIgnoreCase))
                {
                    changed |= AddFbx(obj, path);
                }
            }

            if (changed) MarkDirtyScan();
        }

        private bool AddFbxPath(string path)
        {
            Object obj = AssetDatabase.LoadAssetAtPath<Object>(path);
            return obj != null && AddFbx(obj, path);
        }

        private bool AddFbx(Object obj, string path)
        {
            foreach (Object existing in sourceFbxList)
                if (existing != null && string.Equals(AssetDatabase.GetAssetPath(existing), path, StringComparison.OrdinalIgnoreCase))
                    return false;

            sourceFbxList.Add(obj);
            return true;
        }

        private void HandleRowClick(Rect rect, FFbxUnpackPlan plan, FFbxSubAssetInfo info)
        {
            EditorGUIUtility.AddCursorRect(rect, MouseCursor.Link);
            Event e = Event.current;
            if (e.type != EventType.MouseDown || e.button != 0 || !rect.Contains(e.mousePosition)) return;

            selectedPlan = plan;
            selectedSubAsset = info;
            if (info.SourceObject != null)
            {
                EditorGUIUtility.PingObject(info.SourceObject);
                if (e.clickCount == 2) AssetDatabase.OpenAsset(info.SourceObject);
            }
            e.Use();
        }

        private List<FFbxSubAssetInfo> FilterSubAssets(List<FFbxSubAssetInfo> source)
        {
            if (string.IsNullOrWhiteSpace(searchText)) return source;
            return source.Where(s =>
                Contains(s.DisplayName, searchText) ||
                Contains(s.DisplayType, searchText) ||
                Contains(s.ExtractStatus, searchText) ||
                Contains(s.Key.LocalFileId.ToString(), searchText)).ToList();
        }

        private bool HasApplyable() => plans.Any(p => p != null && p.HasReferences);

        private static bool HasFbx(Object[] objects)
        {
            if (objects == null) return false;
            foreach (Object obj in objects)
            {
                if (IsFbx(obj)) return true;
                string path = AssetDatabase.GetAssetPath(obj);
                if (!string.IsNullOrEmpty(path) && AssetDatabase.IsValidFolder(path)) return true;
            }
            return false;
        }

        private static bool IsFbx(Object obj)
        {
            if (obj == null) return false;
            string path = AssetDatabase.GetAssetPath(obj);
            return !string.IsNullOrEmpty(path) && path.EndsWith(".fbx", StringComparison.OrdinalIgnoreCase);
        }

        private static string ToProjectRelative(string absolute)
        {
            if (string.IsNullOrEmpty(absolute)) return null;
            absolute = absolute.Replace('\\', '/');
            string dataPath = Application.dataPath.Replace('\\', '/');
            if (absolute.Equals(dataPath, StringComparison.OrdinalIgnoreCase)) return "Assets";
            if (absolute.StartsWith(dataPath + "/", StringComparison.OrdinalIgnoreCase))
                return "Assets" + absolute.Substring(dataPath.Length);
            return null;
        }

        private static bool Contains(string source, string value) =>
            !string.IsNullOrEmpty(source) && !string.IsNullOrEmpty(value) &&
            source.IndexOf(value, StringComparison.OrdinalIgnoreCase) >= 0;

        private static GUIStyle GetToolbarSearchStyle() =>
            GUI.skin.FindStyle("ToolbarSeachTextField") ?? EditorStyles.toolbarTextField;

        // -------------------------------------------------------- column layout

        private struct Columns
        {
            public Rect name, type, id, used, status;

            public Columns(Rect rect, bool withIcon)
            {
                const float typeW = 96f, idW = 120f, usedW = 64f, statusW = 80f;
                float right = rect.x + rect.width;
                status = new Rect(right - statusW, rect.y, statusW, rect.height);
                used = new Rect(status.x - usedW, rect.y, usedW, rect.height);
                id = new Rect(used.x - idW, rect.y, idW, rect.height);
                type = new Rect(id.x - typeW, rect.y, typeW, rect.height);
                float nameX = rect.x + (withIcon ? 24f : 2f);
                name = new Rect(nameX, rect.y, Mathf.Max(40f, type.x - nameX - 4f), rect.height);
            }
        }
    }
}
