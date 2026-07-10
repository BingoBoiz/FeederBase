using System.Collections.Generic;
using UnityEditor;
using UnityEngine;
using Object = UnityEngine.Object;

namespace Feeder
{
    /// <summary>Shared IMGUI blocks for the Feeder Asset Cache window.</summary>
    public static class FRefHeaderGUI
    {
        private const int MaxVisibleTargets = 8;
        private const float TargetRowHeight = 22f;
        private const string SharedTargetHint =
            "TargetAssets are shared with Feeder Menu Editor Window and the other Feeder asset tools.";

        private static readonly Color DropAreaColor = new Color(0.18f, 0.55f, 0.95f, 0.18f);
        private static readonly Color DropAreaHoverColor = new Color(0.18f, 0.75f, 1f, 0.28f);

        private static bool s_ShowTargets = true;
        private static Vector2 s_TargetScroll;

        public static void Draw(string guide)
        {
            DrawGuide(guide);
            DrawTargetPanel();
            DrawDatabasePanel(FRefDatabase.instance);
            EditorGUILayout.Space(6);
        }

        public static void DrawGuide(string guide)
        {
            string resolvedGuide = string.IsNullOrEmpty(guide)
                ? "Choose targets, scan the database, then run the asset cache view you need."
                : guide;

            FStylesUtils.DrawDescription(resolvedGuide + "\n" + SharedTargetHint);
            GUILayout.Space(4);
        }

        public static void DrawTargetPanel()
        {
            var targets = FRefData.Targets;
            TargetStats stats = BuildStats(targets);

            EditorGUILayout.BeginVertical(EditorStyles.helpBox);
            DrawTargetHeader(stats);
            DrawDropArea();
            DrawTargetList(targets);
            DrawTargetActions(targets, stats);
            EditorGUILayout.EndVertical();
        }

        public static void DrawDatabasePanel(FRefDatabase db)
        {
            EditorGUILayout.BeginVertical(EditorStyles.helpBox);

            EditorGUILayout.BeginHorizontal();
            EditorGUILayout.LabelField("Reference Database", EditorStyles.boldLabel);
            GUILayout.FlexibleSpace();
            EditorGUILayout.LabelField(GetDatabaseStatus(db), EditorStyles.miniBoldLabel, GUILayout.Width(220));
            EditorGUILayout.EndHorizontal();

            Rect progressRect = GUILayoutUtility.GetRect(18f, 18f, GUILayout.ExpandWidth(true));
            float progress = db.IsBuilding ? db.Progress : db.IsReady ? 1f : 0f;
            EditorGUI.ProgressBar(progressRect, progress, GetDatabaseProgressLabel(db));

            EditorGUILayout.BeginHorizontal();
            using (new EditorGUI.DisabledScope(db.IsBuilding))
            {
                if (GUILayout.Button(db.IsReady ? "Scan / Refresh Database" : "Scan Database", GUILayout.Height(24)))
                    db.StartBuild();
            }

            using (new EditorGUI.DisabledScope(!db.IsBuilding))
            {
                if (GUILayout.Button("Cancel", GUILayout.Width(90), GUILayout.Height(24)))
                    db.CancelBuild();
            }
            EditorGUILayout.EndHorizontal();

            EditorGUILayout.EndVertical();
        }

        private static void DrawTargetHeader(TargetStats stats)
        {
            EditorGUILayout.BeginHorizontal();
            s_ShowTargets = EditorGUILayout.Foldout(
                s_ShowTargets,
                $"Target Assets ({stats.valid}/{stats.total} usable)",
                true);

            GUILayout.FlexibleSpace();
            GUILayout.Label($"{stats.folder} folder", EditorStyles.miniLabel, GUILayout.Width(70));
            GUILayout.Label($"{stats.ignored} ignored", EditorStyles.miniLabel, GUILayout.Width(75));
            EditorGUILayout.EndHorizontal();

            if (stats.nulls > 0 || stats.ignored > 0)
            {
                EditorGUILayout.HelpBox(
                    $"{stats.nulls} null and {stats.ignored} non-asset target will be ignored by reference queries.",
                    MessageType.Warning);
            }
        }

        private static void DrawDropArea()
        {
            Rect rect = GUILayoutUtility.GetRect(0, 44f, GUILayout.ExpandWidth(true));
            Event e = Event.current;
            bool hover = rect.Contains(e.mousePosition);

            EditorGUI.DrawRect(rect, hover ? DropAreaHoverColor : DropAreaColor);
            GUI.Box(rect, GUIContent.none, EditorStyles.helpBox);

            var style = new GUIStyle(EditorStyles.centeredGreyMiniLabel)
            {
                fontSize = 12,
                fontStyle = FontStyle.Bold,
                alignment = TextAnchor.MiddleCenter
            };
            GUI.Label(rect, "Drop assets or folders here", style);

            if (!hover) return;

            if (e.type == EventType.DragUpdated)
            {
                DragAndDrop.visualMode = HasAssetTarget(DragAndDrop.objectReferences)
                    ? DragAndDropVisualMode.Copy
                    : DragAndDropVisualMode.Rejected;
                e.Use();
            }
            else if (e.type == EventType.DragPerform)
            {
                DragAndDrop.AcceptDrag();
                FRefData.AddAssets(DragAndDrop.objectReferences, false);
                e.Use();
            }
        }

        private static void DrawTargetList(List<Object> targets)
        {
            if (!s_ShowTargets) return;

            if (targets.Count == 0)
            {
                EditorGUILayout.HelpBox("TargetAssets is empty. Drop assets/folders here or click Add Selection.", MessageType.Info);
                return;
            }

            int removeAt = -1;
            float height = Mathf.Min(targets.Count, MaxVisibleTargets) * TargetRowHeight + 4f;
            s_TargetScroll = EditorGUILayout.BeginScrollView(
                s_TargetScroll,
                GUILayout.MinHeight(Mathf.Min(height, 180f)),
                GUILayout.MaxHeight(Mathf.Min(height, 180f)));

            for (int i = 0; i < targets.Count; i++)
            {
                Object current = targets[i];
                EditorGUILayout.BeginHorizontal(GUILayout.Height(TargetRowHeight));
                GUILayout.Label((i + 1).ToString(), EditorStyles.miniLabel, GUILayout.Width(24));

                EditorGUI.BeginChangeCheck();
                Object newObj = EditorGUILayout.ObjectField(current, typeof(Object), false);
                if (EditorGUI.EndChangeCheck())
                {
                    if (newObj == null || FRefData.IsAssetTarget(newObj))
                    {
                        targets[i] = newObj;
                        FRefData.SaveData();
                        current = newObj;
                    }
                    else
                    {
                        Debug.LogWarning("[Feeder Asset Cache] Only Project assets or folders are accepted.");
                    }
                }

                GUILayout.Label(GetTargetLabel(current), EditorStyles.miniLabel, GUILayout.MinWidth(120));

                using (new EditorGUI.DisabledScope(!FRefData.IsAssetTarget(current)))
                {
                    if (GUILayout.Button("Ping", EditorStyles.miniButton, GUILayout.Width(42)))
                        Ping(current);
                }

                if (GUILayout.Button("X", EditorStyles.miniButton, GUILayout.Width(24)))
                    removeAt = i;

                EditorGUILayout.EndHorizontal();
            }

            EditorGUILayout.EndScrollView();

            if (removeAt >= 0)
            {
                targets.RemoveAt(removeAt);
                FRefData.SaveData();
            }
        }

        private static void DrawTargetActions(List<Object> targets, TargetStats stats)
        {
            EditorGUILayout.BeginHorizontal();

            using (new EditorGUI.DisabledScope(!HasAssetTarget(Selection.objects)))
            {
                if (GUILayout.Button("Add Selection", GUILayout.Height(22)))
                    FRefData.AddAssets(Selection.objects, false);

                if (GUILayout.Button("Replace", GUILayout.Height(22), GUILayout.Width(70)))
                    FRefData.AddAssets(Selection.objects, true);
            }

            using (new EditorGUI.DisabledScope(stats.nulls == 0))
            {
                if (GUILayout.Button("Remove Nulls", GUILayout.Height(22), GUILayout.Width(105)))
                {
                    targets.RemoveAll(t => t == null);
                    FRefData.SaveData();
                }
            }

            using (new EditorGUI.DisabledScope(stats.valid == 0))
            {
                if (GUILayout.Button("Ping First", GUILayout.Height(22), GUILayout.Width(85)))
                    PingFirst(targets);
            }

            using (new EditorGUI.DisabledScope(targets.Count == 0))
            {
                if (GUILayout.Button("Clear", GUILayout.Height(22), GUILayout.Width(60)))
                {
                    targets.Clear();
                    FRefData.SaveData();
                }
            }

            EditorGUILayout.EndHorizontal();
        }

        private static TargetStats BuildStats(List<Object> targets)
        {
            var stats = new TargetStats { total = targets.Count };
            foreach (Object target in targets)
            {
                if (target == null)
                {
                    stats.nulls++;
                    continue;
                }

                if (!FRefData.IsAssetTarget(target))
                {
                    stats.ignored++;
                    continue;
                }

                stats.valid++;
                if (AssetDatabase.IsValidFolder(AssetDatabase.GetAssetPath(target)))
                    stats.folder++;
            }

            return stats;
        }

        private static string GetTargetLabel(Object target)
        {
            if (target == null) return "(null)";

            string path = AssetDatabase.GetAssetPath(target);
            if (string.IsNullOrEmpty(path)) return "(ignored: not an asset)";

            return ShortenPath(path, 48);
        }

        private static string ShortenPath(string path, int maxLength)
        {
            if (string.IsNullOrEmpty(path) || path.Length <= maxLength)
                return path;

            return "..." + path.Substring(path.Length - maxLength + 3);
        }

        private static bool HasAssetTarget(Object[] objects)
        {
            if (objects == null) return false;
            foreach (Object obj in objects)
                if (FRefData.IsAssetTarget(obj))
                    return true;
            return false;
        }

        private static void PingFirst(List<Object> targets)
        {
            foreach (Object target in targets)
            {
                if (!FRefData.IsAssetTarget(target)) continue;
                Ping(target);
                return;
            }
        }

        private static void Ping(Object target)
        {
            if (target == null) return;
            Selection.activeObject = target;
            EditorGUIUtility.PingObject(target);
        }

        private static string GetDatabaseStatus(FRefDatabase db)
        {
            if (db.IsBuilding) return $"Scanning... {(db.Progress * 100f):0}%";
            if (db.IsReady) return $"Ready - {db.AssetCount} assets";
            return "Not built";
        }

        private static string GetDatabaseProgressLabel(FRefDatabase db)
        {
            if (db.IsBuilding) return $"Scanning assets... {(db.Progress * 100f):0}%";
            if (db.IsReady) return $"Ready - {db.AssetCount} assets indexed";
            return "Click Scan Database before running project-wide queries";
        }

        private struct TargetStats
        {
            public int total;
            public int valid;
            public int folder;
            public int nulls;
            public int ignored;
        }
    }
}
