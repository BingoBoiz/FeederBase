#if UNITY_EDITOR
using System.Collections.Generic;
using Sirenix.OdinInspector.Editor;
using UnityEditor;
using UnityEngine;

namespace Feeder
{
    /// <summary>
    /// One-click scene analyzer: classifies every mesh's texture region into
    /// PaletteReady / Gradient / Pattern / Uncertain / Skipped (a prediction of whether it can be
    /// converted through the existing MeshColorRemapper palette pipeline) and visualizes the
    /// verdicts with colored boxes + labels in the Scene view. Thresholds are tunable for
    /// calibrating against real assets.
    /// </summary>
    public sealed class FMeshPaletteAnalyzerWindow : OdinEditorWindow
    {
        private const string ToolName = "Feeder Mesh Palette Analyzer";
        private const int MenuPriority = 4;

        private static readonly MeshPaletteVerdict[] AllVerdicts =
        {
            MeshPaletteVerdict.PaletteReady,
            MeshPaletteVerdict.Gradient,
            MeshPaletteVerdict.Pattern,
            MeshPaletteVerdict.Uncertain,
            MeshPaletteVerdict.Skipped
        };

        private readonly MeshPaletteAnalysisSession session = new MeshPaletteAnalysisSession();
        private MeshPaletteThresholds thresholds = MeshPaletteThresholds.Default;

        private readonly Dictionary<MeshPaletteVerdict, bool> sceneVisible = new Dictionary<MeshPaletteVerdict, bool>();
        private readonly Dictionary<MeshPaletteVerdict, bool> tableFilter = new Dictionary<MeshPaletteVerdict, bool>();
        private bool showSceneLabels = true;
        private bool thresholdsFoldout = true;

        private int hoverIndex = -1;
        private Vector2 scroll;

        [MenuItem("Tools/Feeder/Feeder Mesh Palette Analyzer", priority = MenuPriority)]
        private static void OpenWindow()
        {
            var window = GetWindow<FMeshPaletteAnalyzerWindow>();
            window.titleContent = FIconCatalog.CreateWindowTitle(ToolName, FIconCatalog.WindowMenuTitleIcon);
            window.minSize = new Vector2(720, 520);
            window.Show();
        }

        protected override void OnEnable()
        {
            base.OnEnable();
            wantsMouseMove = true;
            foreach (MeshPaletteVerdict v in AllVerdicts)
            {
                if (!sceneVisible.ContainsKey(v)) sceneVisible[v] = true;
                if (!tableFilter.ContainsKey(v)) tableFilter[v] = true;
            }
            SceneView.duringSceneGui -= OnSceneGUI;
            SceneView.duringSceneGui += OnSceneGUI;
        }

        protected override void OnDisable()
        {
            SceneView.duringSceneGui -= OnSceneGUI;
            base.OnDisable();
        }

        private void OnSceneGUI(SceneView sv)
        {
            if (!session.HasResults) return;
            MeshPaletteAnalyzerSceneOverlay.Draw(session.Results, hoverIndex, showSceneLabels, sceneVisible);
        }

        protected override void OnImGUI()
        {
            FStylesUtils.DrawDescription(
                "Bấm Analyze để quét mesh trong scene và dự đoán mesh nào có thể ép về bảng màu " +
                "(solid color) — mesh dùng gradient/pattern sẽ được đánh dấu là không convert được. " +
                "Kết quả hiển thị bằng box màu + nhãn trên Scene.");
            GUILayout.Space(4f);

            DrawActionSection();
            GUILayout.Space(4f);
            DrawThresholdSection();
            GUILayout.Space(4f);
            DrawResultsSection();
        }

        private void DrawActionSection()
        {
            using (new EditorGUILayout.VerticalScope(EditorStyles.helpBox))
            {
                using (new EditorGUILayout.HorizontalScope())
                {
                    GUI.backgroundColor = new Color(0.4f, 0.8f, 1f);
                    if (GUILayout.Button("Analyze Scene", GUILayout.Height(28f)))
                    {
                        session.AnalyzeScene(thresholds);
                        hoverIndex = -1;
                        RepaintScene();
                    }
                    if (GUILayout.Button("Analyze Selection", GUILayout.Height(28f)))
                    {
                        session.AnalyzeSelection(thresholds);
                        hoverIndex = -1;
                        RepaintScene();
                    }
                    GUI.backgroundColor = Color.white;

                    using (new EditorGUI.DisabledScope(!session.HasResults))
                    {
                        if (GUILayout.Button("Clear", GUILayout.Height(28f), GUILayout.Width(70f)))
                        {
                            session.Clear();
                            hoverIndex = -1;
                            RepaintScene();
                        }
                    }
                }

                if (session.HasResults)
                    DrawSummary();
            }
        }

        private void DrawSummary()
        {
            using (new EditorGUILayout.HorizontalScope())
            {
                GUILayout.Label($"Tổng: {session.Results.Count}", EditorStyles.miniLabel);
                foreach (MeshPaletteVerdict v in AllVerdicts)
                {
                    int n = session.CountOf(v);
                    if (n == 0) continue;
                    Color prev = GUI.contentColor;
                    GUI.contentColor = MeshPaletteAnalyzerSceneOverlay.VerdictColor(v);
                    GUILayout.Label($"■ {v}: {n}", EditorStyles.miniLabel);
                    GUI.contentColor = prev;
                }
                GUILayout.FlexibleSpace();
            }
        }

        private void DrawThresholdSection()
        {
            using (new EditorGUILayout.VerticalScope(EditorStyles.helpBox))
            {
                thresholdsFoldout = EditorGUILayout.Foldout(thresholdsFoldout, "Ngưỡng phân loại", true);
                if (!thresholdsFoldout) return;

                EditorGUI.BeginChangeCheck();
                thresholds.maxPaletteColors = EditorGUILayout.IntSlider(
                    new GUIContent("Max Palette Colors", "Số màu phẳng tối đa vẫn coi là 'cỡ palette'."),
                    thresholds.maxPaletteColors, 2, 24);
                thresholds.coverageThreshold = EditorGUILayout.Slider(
                    new GUIContent("Coverage Threshold", "Tỉ lệ pixel mà nhóm màu top phải phủ để coi là phẳng."),
                    thresholds.coverageThreshold, 0.5f, 1f);
                thresholds.gradFlatThreshold = EditorGUILayout.Slider(
                    new GUIContent("Gradient Threshold", "Tỉ lệ cặp pixel dải chuyển mượt vượt mức này ⇒ gradient."),
                    thresholds.gradFlatThreshold, 0.02f, 0.6f);
                thresholds.noiseThreshold = EditorGUILayout.Slider(
                    new GUIContent("Noise Threshold", "Mật độ cạnh sắc để mesh nhiều màu bị coi là pattern."),
                    thresholds.noiseThreshold, 0.05f, 0.8f);
                bool decisionChanged = EditorGUI.EndChangeCheck();

                EditorGUI.BeginChangeCheck();
                thresholds.downscaleSize = EditorGUILayout.IntSlider(
                    new GUIContent("Downscale Size", "Cạnh lớn nhất (px) khi phân tích. Đổi giá trị này cần Analyze lại."),
                    thresholds.downscaleSize, 64, 1024);
                bool downscaleChanged = EditorGUI.EndChangeCheck();

                using (new EditorGUILayout.HorizontalScope())
                {
                    if (GUILayout.Button("Reset thresholds", GUILayout.Width(140f)))
                    {
                        thresholds = MeshPaletteThresholds.Default;
                        decisionChanged = true;
                    }
                    if (downscaleChanged && session.HasResults)
                        GUILayout.Label("Downscale đã đổi — bấm Analyze lại để áp dụng.", EditorStyles.miniLabel);
                }

                if (decisionChanged && session.HasResults)
                {
                    session.Reclassify(thresholds);
                    RepaintScene();
                }
            }
        }

        private void DrawResultsSection()
        {
            if (!session.HasResults)
            {
                FStylesUtils.DrawInfoBox("Chưa có kết quả. Bấm Analyze Scene hoặc Analyze Selection.");
                return;
            }

            using (new EditorGUILayout.VerticalScope(EditorStyles.helpBox))
            {
                DrawFilterBar();

                scroll = EditorGUILayout.BeginScrollView(scroll, GUILayout.ExpandHeight(true));
                int newHover = -1;
                for (int i = 0; i < session.Results.Count; i++)
                {
                    MeshPaletteClassification c = session.Results[i];
                    if (tableFilter.TryGetValue(c.verdict, out bool show) && !show) continue;
                    DrawRow(i, c, ref newHover);
                }
                EditorGUILayout.EndScrollView();

                if (Event.current.type == EventType.MouseMove && newHover != hoverIndex)
                {
                    hoverIndex = newHover;
                    RepaintScene();
                }
            }
        }

        private void DrawFilterBar()
        {
            using (new EditorGUILayout.HorizontalScope())
            {
                GUILayout.Label("Hiện:", EditorStyles.miniLabel, GUILayout.Width(34f));
                foreach (MeshPaletteVerdict v in AllVerdicts)
                {
                    Color prev = GUI.contentColor;
                    GUI.contentColor = MeshPaletteAnalyzerSceneOverlay.VerdictColor(v);
                    tableFilter[v] = GUILayout.Toggle(tableFilter[v], v.ToString(), EditorStyles.miniButton);
                    GUI.contentColor = prev;
                }
                GUILayout.FlexibleSpace();
                showSceneLabels = GUILayout.Toggle(showSceneLabels, "Nhãn Scene", EditorStyles.miniButton);
            }
        }

        private void DrawRow(int index, MeshPaletteClassification c, ref int newHover)
        {
            using (new EditorGUILayout.HorizontalScope())
            {
                Rect swatch = GUILayoutUtility.GetRect(14f, 16f, GUIStyle.none, GUILayout.Width(14f));
                swatch.y += 2f; swatch.height = 12f; swatch.width = 12f;
                EditorGUI.DrawRect(swatch, MeshPaletteAnalyzerSceneOverlay.VerdictColor(c.verdict));

                if (GUILayout.Button(c.DisplayName, EditorStyles.label, GUILayout.Width(180f)))
                    FocusOn(c);

                GUILayout.Label(c.verdict.ToString(), EditorStyles.miniLabel, GUILayout.Width(90f));
                GUILayout.Label(c.TextureName, EditorStyles.miniLabel, GUILayout.Width(140f));

                string flags = BuildFlags(c);
                var content = new GUIContent(c.reason + flags,
                    c.metrics == null ? c.reason
                        : $"Slot {c.materialSlot} · {c.metrics.significantColorCount} màu · " +
                          $"gradient {c.metrics.gradientScore:P0} · noise {c.metrics.noiseScore:P0}{flags}");
                GUILayout.Label(content, EditorStyles.miniLabel);
                GUILayout.FlexibleSpace();
            }

            Rect row = GUILayoutUtility.GetLastRect();
            if (row.Contains(Event.current.mousePosition))
                newHover = index;
        }

        private static string BuildFlags(MeshPaletteClassification c)
        {
            string s = "";
            if (c.meshUnreadable) s += " ⚠read/write off";
            if (c.tiledUVs) s += " ⚠tiled";
            if (c.outOfBoundsUVs) s += " ⚠uv>1";
            if (c.analyzedWholeTexture && !c.meshUnreadable && !c.tiledUVs) s += " (toàn texture)";
            return s;
        }

        private static void FocusOn(MeshPaletteClassification c)
        {
            if (c.renderer == null) return;
            Selection.activeGameObject = c.renderer.gameObject;
            EditorGUIUtility.PingObject(c.renderer.gameObject);
            if (SceneView.lastActiveSceneView != null)
                SceneView.lastActiveSceneView.FrameSelected();
        }

        private static void RepaintScene()
        {
            SceneView.RepaintAll();
        }
    }
}
#endif
