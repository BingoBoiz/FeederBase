#if UNITY_EDITOR
using System.Collections.Generic;
using System.Linq;
using Feeder.MB.Core;
using Sirenix.OdinInspector.Editor;
using UnityEditor;
using UnityEngine;
using Object = UnityEngine.Object;

namespace Feeder
{
    /// <summary>
    /// Two-tab mesh utility window. Merge tab: bakes many meshes/materials into one
    /// combined mesh (textures packed into an atlas, UVs remapped, MeshBaker pipeline).
    /// Split tab: splits one scene mesh into separate part meshes (UV islands, manual
    /// vertex picking or a cutting volume) with a Scene View helper overlay.
    /// </summary>
    public sealed class FMeshModifierWindow : OdinEditorWindow
    {
        private const string ToolName = "Feeder Mesh Modifier";
        private const int MenuPriority = 3;
        private const float LeftColumnWidth = 300f;
        private const float RendererTableHeight = 180f;
        private const float PartTableHeight = 160f;

        private static readonly Color HoverFill = new Color(0f, 1f, 1f, 0.12f);
        private static readonly Color AltRowFill = new Color(1f, 1f, 1f, 0.03f);
        private static readonly Color WarnRed = new Color(1f, 0.45f, 0.45f);

        private enum Tab { Merge, Split }

        private static readonly string[] TabLabels = { "Gộp Mesh (Merge)", "Tách Mesh (Split)" };
        private static readonly string[] SplitModeLabels = { "Đơn giản (UV)", "Thủ công (Đỉnh)", "Nâng cao (Khối)" };

        private readonly FMeshBakeSession session = new FMeshBakeSession();
        private readonly FMeshBakeSettings settings = new FMeshBakeSettings();
        private readonly FMeshSplitSession splitSession = new FMeshSplitSession();
        private readonly FMeshSplitSettings splitSettings = new FMeshSplitSettings();

        private const string PrefsOwner = nameof(FMeshModifierWindow);

        private Tab tab
        {
            get => FToolPrefs.GetEnum(PrefsOwner, nameof(tab), Tab.Merge);
            set => FToolPrefs.SetEnum(PrefsOwner, nameof(tab), value);
        }
        private int splitSelectedTriangleCount;
        private Vector2 windowScroll;
        private Vector2 tableScroll;
        private Vector2 partTableScroll;
        private int hoverRowIndex = -1;
        private bool guideFoldout;
        private bool splitGuideFoldout;
        private bool advancedFoldout;
        private string customPropsText
        {
            get => FToolPrefs.GetString(PrefsOwner, nameof(customPropsText), string.Empty);
            set => FToolPrefs.SetString(PrefsOwner, nameof(customPropsText), value);
        }
        private string ignorePropsText
        {
            get => FToolPrefs.GetString(PrefsOwner, nameof(ignorePropsText), string.Empty);
            set => FToolPrefs.SetString(PrefsOwner, nameof(ignorePropsText), value);
        }

        [MenuItem("Tools/Feeder/Feeder Mesh Modifier", priority = MenuPriority)]
        private static void OpenWindow()
        {
            var window = GetWindow<FMeshModifierWindow>();
            window.titleContent = FIconCatalog.CreateWindowTitle(ToolName, FIconCatalog.WindowMenuTitleIcon);
            window.minSize = new Vector2(760, 560);
            window.Show();
        }

        protected override void OnEnable()
        {
            base.OnEnable();
            wantsMouseMove = true;
        }

        protected override void OnDisable()
        {
            FMeshSplitterSceneOverlay.CloseOverlay();
            base.OnDisable();
        }

        protected override void OnImGUI()
        {
            GUILayout.Space(4f);
            int newTab = GUILayout.Toolbar((int)tab, TabLabels, EditorStyles.toolbarButton, GUILayout.Height(24f));
            if (newTab != (int)tab)
                SwitchTab((Tab)newTab);

            GUILayout.Space(4f);
            windowScroll = EditorGUILayout.BeginScrollView(windowScroll, GUILayout.ExpandHeight(true));

            if (tab == Tab.Merge)
                DrawMergeTab();
            else
                DrawSplitTab();

            EditorGUILayout.EndScrollView();

            if (tab == Tab.Merge && session.IsAnalyzed && session.Report.Rows.Count > 0)
            {
                EditorGUILayout.Space();
                DrawActionBar();
            }
            else if (tab == Tab.Split && splitSession.IsAnalyzed && !splitSession.Analysis.IsStale)
            {
                EditorGUILayout.Space();
                DrawSplitActionBar();
            }
        }

        private void SwitchTab(Tab next)
        {
            if (tab == Tab.Split)
                FMeshSplitterSceneOverlay.CloseOverlay();

            tab = next;
            GUI.FocusControl(null);
            SceneView.RepaintAll();
            Repaint();
        }

        private void DrawMergeTab()
        {
            FStylesUtils.DrawDescription(
                "Gộp nhiều mesh và material thành một: texture của các object nguồn được đóng gói vào một atlas, " +
                "UV được ánh xạ lại, và toàn bộ mesh được merge thành một mesh + material + prefab duy nhất. " +
                "Object nguồn không bao giờ bị chỉnh sửa.");
            GUILayout.Space(4f);

            DrawGuideSection();
            GUILayout.Space(4f);

            DrawTargetsSection();

            if (session.IsAnalyzed && session.Report.Rows.Count > 0)
            {
                EditorGUILayout.Space();
                DrawSummarySection();
                EditorGUILayout.Space();
                DrawBakeSettingsSection();
                EditorGUILayout.Space();
                DrawOutputSection();
            }
            else
            {
                EditorGUILayout.Space();
                FStylesUtils.DrawInfoBox(
                    "Kéo-thả prefab hoặc object trong scene vào danh sách Targets (hoặc bấm \"Use Selection\"), " +
                    "sau đó bấm \"Analyze\" để kiểm tra mesh, material và texture trước khi bake.");
            }
        }

        // ================================================================ Guide

        private void DrawGuideSection()
        {
            using (new EditorGUILayout.VerticalScope(EditorStyles.helpBox))
            {
                guideFoldout = EditorGUILayout.Foldout(guideFoldout, "Hướng dẫn sử dụng", true, EditorStyles.foldoutHeader);
                if (!guideFoldout)
                    return;

                var stepStyle = new GUIStyle(EditorStyles.wordWrappedLabel) { fontSize = 11 };
                EditorGUILayout.LabelField(
                    "1.  Thêm mục tiêu: kéo-thả prefab từ cửa sổ Project hoặc object từ Hierarchy vào ô bên dưới, " +
                    "hoặc chọn sẵn trong Unity rồi bấm \"Use Selection\".", stepStyle);
                EditorGUILayout.LabelField(
                    "2.  Bấm \"Analyze\" để tool quét toàn bộ MeshRenderer, thống kê số đỉnh (vertex), material, texture " +
                    "và đưa ra cảnh báo nếu có vấn đề (mesh không đọc được, shader khác nhau, UV tràn...).", stepStyle);
                EditorGUILayout.LabelField(
                    "3.  Xem bảng Source Summary: di chuột lên từng dòng và click để ping object tương ứng trong scene/project.", stepStyle);
                EditorGUILayout.LabelField(
                    "4.  Điều chỉnh Bake Settings (kích thước atlas, padding, thuật toán đóng gói...). " +
                    "Di chuột lên từng ô để xem giải thích chi tiết bằng tiếng Việt.", stepStyle);
                EditorGUILayout.LabelField(
                    "5.  Chọn thư mục xuất và tên file trong mục Output, rồi bấm \"Bake Combined Mesh\". " +
                    "Kết quả gồm: prefab, mesh, material, texture atlas và file bake-results.", stepStyle);
                EditorGUILayout.LabelField(
                    "6.  Kéo prefab kết quả vào scene để so sánh với object gốc. Object gốc không bị thay đổi — " +
                    "bạn tự quyết định ẩn/xóa chúng sau khi hài lòng với kết quả.", stepStyle);

                GUILayout.Space(2f);
                EditorGUILayout.LabelField(
                    "Lưu ý: material kết quả dùng shader của material ĐẦU TIÊN trong danh sách nguồn. " +
                    "Nếu các object dùng shader khác nhau, kết quả có thể không giống hệt bản gốc.",
                    new GUIStyle(EditorStyles.wordWrappedMiniLabel) { fontStyle = FontStyle.Italic });
            }
        }

        // ================================================================ Targets

        private void DrawTargetsSection()
        {
            using (new EditorGUILayout.VerticalScope(EditorStyles.helpBox))
            {
                EditorGUILayout.LabelField(
                    new GUIContent("Targets", "Danh sách prefab / object trong scene sẽ được gộp mesh."),
                    EditorStyles.boldLabel);

                DrawDropZone();

                for (int i = session.Targets.Count - 1; i >= 0; i--)
                {
                    using (new EditorGUILayout.HorizontalScope())
                    {
                        EditorGUI.BeginChangeCheck();
                        var newObj = (GameObject)EditorGUILayout.ObjectField(
                            session.Targets[i], typeof(GameObject), true);
                        if (EditorGUI.EndChangeCheck())
                        {
                            if (newObj == null || session.Targets.Contains(newObj))
                                session.Targets.RemoveAt(i);
                            else
                                session.Targets[i] = newObj;
                            session.InvalidateAnalysis();
                        }

                        if (GUILayout.Button(new GUIContent("X", "Xóa mục tiêu này khỏi danh sách"), GUILayout.Width(22f)))
                        {
                            session.Targets.RemoveAt(i);
                            session.InvalidateAnalysis();
                            GUI.FocusControl(null);
                        }
                    }
                }

                GUILayout.Space(4f);
                using (new EditorGUILayout.HorizontalScope())
                {
                    if (GUILayout.Button(
                            new GUIContent("Use Selection", "Thêm các object đang chọn trong Hierarchy hoặc Project vào danh sách"),
                            GUILayout.Width(130)))
                        AddTargets(Selection.gameObjects);

                    if (GUILayout.Button(
                            new GUIContent("Clear Targets", "Xóa toàn bộ danh sách mục tiêu và kết quả phân tích"),
                            GUILayout.Width(130)))
                    {
                        session.Reset();
                        GUI.FocusControl(null);
                    }

                    GUILayout.FlexibleSpace();
                    EditorGUILayout.LabelField($"{session.Targets.Count} target(s)",
                        EditorStyles.miniLabel, GUILayout.Width(80f));
                }

                GUILayout.Space(4f);
                using (new EditorGUI.DisabledScope(session.Targets.Count == 0))
                {
                    Color originalBg = GUI.backgroundColor;
                    GUI.backgroundColor = new Color(0.4f, 0.8f, 1f);
                    if (GUILayout.Button(
                            new GUIContent("Analyze", "Quét mesh/material/texture của các mục tiêu và hiện thống kê + cảnh báo trước khi bake"),
                            GUILayout.Height(26)))
                        Analyze();
                    GUI.backgroundColor = originalBg;
                }
            }
        }

        private void DrawDropZone()
        {
            Rect dropRect = GUILayoutUtility.GetRect(0f, 34f, GUILayout.ExpandWidth(true));
            var boxStyle = new GUIStyle(EditorStyles.helpBox)
            {
                alignment = TextAnchor.MiddleCenter,
                fontStyle = FontStyle.Italic
            };
            GUI.Box(dropRect, "Kéo-thả prefab hoặc object trong scene vào đây", boxStyle);

            Event evt = Event.current;
            if (!dropRect.Contains(evt.mousePosition))
                return;

            if (evt.type == EventType.DragUpdated)
            {
                DragAndDrop.visualMode = DragAndDrop.objectReferences.OfType<GameObject>().Any()
                    ? DragAndDropVisualMode.Copy
                    : DragAndDropVisualMode.Rejected;
                evt.Use();
            }
            else if (evt.type == EventType.DragPerform)
            {
                DragAndDrop.AcceptDrag();
                AddTargets(DragAndDrop.objectReferences.OfType<GameObject>().ToArray());
                evt.Use();
            }
        }

        private void AddTargets(IEnumerable<GameObject> gameObjects)
        {
            bool changed = false;
            foreach (GameObject go in gameObjects)
            {
                if (go == null || session.Targets.Contains(go))
                    continue;
                session.Targets.Add(go);
                changed = true;
            }

            if (changed)
            {
                session.InvalidateAnalysis();
                Repaint();
            }
        }

        private void Analyze()
        {
            session.Report = FMeshBakeAnalyzer.Analyze(session.Targets, settings);
            session.LastResult = null;
            hoverRowIndex = -1;

            if (string.IsNullOrWhiteSpace(settings.baseName) || settings.baseName == "CombinedMesh")
            {
                var first = session.Targets.FirstOrDefault(t => t != null);
                if (first != null)
                    settings.baseName = first.name + "_Combined";
            }

            if (string.IsNullOrWhiteSpace(settings.outputFolder))
                settings.outputFolder = ResolveDefaultOutputFolder(session.Targets) ?? settings.outputFolder;
        }

        /// <summary>Folder of the first target's asset (via prefab source for scene instances), or null.</summary>
        private static string ResolveDefaultOutputFolder(IEnumerable<UnityEngine.Object> targets)
        {
            var first = targets.FirstOrDefault(t => t != null);
            if (first == null) return null;
            string p = AssetDatabase.GetAssetPath(first);
            if (string.IsNullOrEmpty(p))
            {
                var src = PrefabUtility.GetCorrespondingObjectFromOriginalSource(first);
                if (src != null) p = AssetDatabase.GetAssetPath(src);
            }
            if (string.IsNullOrEmpty(p)) return null;
            return System.IO.Path.GetDirectoryName(p)?.Replace('\\', '/');
        }

        // ================================================================ Summary

        private void DrawSummarySection()
        {
            using (new EditorGUILayout.VerticalScope(EditorStyles.helpBox))
            {
                EditorGUILayout.LabelField(
                    new GUIContent("Source Summary", "Thống kê các mesh nguồn sẽ được gộp. Click một dòng trong bảng để ping object."),
                    EditorStyles.boldLabel);

                using (new EditorGUILayout.HorizontalScope())
                {
                    using (new EditorGUILayout.VerticalScope(GUILayout.Width(LeftColumnWidth)))
                        DrawStatCard();

                    GUILayout.Space(12f);

                    using (new EditorGUILayout.VerticalScope(GUILayout.ExpandWidth(true)))
                        DrawRendererTable();
                }

                FMeshBakeReport report = session.Report;
                if (report.Warnings.Count > 0)
                {
                    GUILayout.Space(4f);
                    foreach (string warning in report.Warnings)
                        EditorGUILayout.HelpBox(warning, MessageType.Warning);
                }
            }
        }

        private void DrawStatCard()
        {
            FMeshBakeReport report = session.Report;

            DrawStatRow("Meshes", report.Rows.Count.ToString("N0"),
                tooltip: "Số MeshRenderer hợp lệ tìm thấy trong các mục tiêu");
            DrawStatRow("Vertices", report.TotalVertices.ToString("N0"), report.TotalVertices > 65534,
                "Tổng số đỉnh. Vượt 65.534 thì mesh gộp sẽ dùng index 32-bit");
            DrawStatRow("Triangles", report.TotalTriangles.ToString("N0"),
                tooltip: "Tổng số tam giác của các mesh nguồn");
            DrawStatRow("Materials", report.DistinctMaterialCount.ToString("N0"),
                tooltip: "Số material khác nhau — tất cả sẽ được gộp về 1 material atlas");
            DrawStatRow("Shaders", report.DistinctShaderCount.ToString("N0"), report.DistinctShaderCount > 1,
                "Số shader khác nhau. Nhiều hơn 1 thì kết quả dùng shader của material đầu tiên");
            DrawStatRow("Textures", report.DistinctTextureCount.ToString("N0"),
                tooltip: "Số texture chính (albedo) khác nhau sẽ được đóng gói vào atlas");

            long atlasCapacity = (long)settings.maxAtlasSize * settings.maxAtlasSize;
            float fillPercent = atlasCapacity > 0 ? report.EstimatedAtlasPixels * 100f / atlasCapacity : 0f;
            DrawStatRow($"Atlas fill ({settings.maxAtlasSize}px)", $"~{fillPercent:0.#}%", fillPercent > 100f,
                "Ước lượng tỉ lệ lấp đầy atlas. Vượt 100% thì texture nguồn sẽ bị thu nhỏ để vừa atlas");

            string inputKind = report.HasSceneObjects && report.HasPrefabAssets ? "Scene + Prefab assets"
                : report.HasSceneObjects ? "Scene objects" : "Prefab assets";
            DrawStatRow("Input", inputKind, tooltip: "Loại mục tiêu đầu vào (object trong scene hay prefab asset)");
        }

        private static void DrawStatRow(string label, string value, bool highlight = false, string tooltip = null)
        {
            using (new EditorGUILayout.HorizontalScope())
            {
                EditorGUILayout.LabelField(new GUIContent(label, tooltip), EditorStyles.miniLabel, GUILayout.Width(150f));

                var style = new GUIStyle(EditorStyles.miniBoldLabel);
                if (highlight)
                    style.normal.textColor = WarnRed;
                EditorGUILayout.LabelField(new GUIContent(value, tooltip), style);
            }
        }

        private void DrawRendererTable()
        {
            FMeshBakeReport report = session.Report;

            using (new EditorGUILayout.HorizontalScope())
            {
                EditorGUILayout.LabelField("Renderer", EditorStyles.miniBoldLabel, GUILayout.Width(150f));
                EditorGUILayout.LabelField("Verts", EditorStyles.miniBoldLabel, GUILayout.Width(60f));
                EditorGUILayout.LabelField("Materials", EditorStyles.miniBoldLabel, GUILayout.Width(140f));
                EditorGUILayout.LabelField("Main Texture", EditorStyles.miniBoldLabel);
            }

            tableScroll = EditorGUILayout.BeginScrollView(tableScroll, GUILayout.Height(RendererTableHeight));

            int newHover = -1;
            for (int i = 0; i < report.Rows.Count; i++)
            {
                FMeshBakeRendererRow row = report.Rows[i];

                Rect rowRect = EditorGUILayout.BeginHorizontal(GUILayout.Height(18f));
                if (Event.current.type == EventType.Repaint)
                {
                    if (i == hoverRowIndex)
                        EditorGUI.DrawRect(rowRect, HoverFill);
                    else if ((i & 1) == 1)
                        EditorGUI.DrawRect(rowRect, AltRowFill);
                }

                var nameStyle = new GUIStyle(EditorStyles.miniLabel);
                if (!row.IsMeshReadable)
                    nameStyle.normal.textColor = WarnRed;

                var nameContent = new GUIContent(row.Name,
                    row.IsMeshReadable ? "Click để ping object này" : "Mesh không đọc được (Read/Write đang tắt) — bake sẽ thất bại với mesh này");
                EditorGUILayout.LabelField(nameContent, nameStyle, GUILayout.Width(150f));
                EditorGUILayout.LabelField(row.VertexCount.ToString("N0"), EditorStyles.miniLabel, GUILayout.Width(60f));
                EditorGUILayout.LabelField(row.MaterialNames, EditorStyles.miniLabel, GUILayout.Width(140f));
                EditorGUILayout.LabelField(row.MainTextureInfo, EditorStyles.miniLabel);
                EditorGUILayout.EndHorizontal();

                if (rowRect.Contains(Event.current.mousePosition))
                {
                    newHover = i;
                    if (Event.current.type == EventType.MouseDown && row.GameObject != null)
                    {
                        EditorGUIUtility.PingObject(row.GameObject);
                        Event.current.Use();
                    }
                }
            }

            EditorGUILayout.EndScrollView();

            if (newHover != hoverRowIndex && Event.current.type != EventType.Layout)
            {
                hoverRowIndex = newHover;
                Repaint();
            }
        }

        // ================================================================ Bake settings

        private void DrawBakeSettingsSection()
        {
            using (new EditorGUILayout.VerticalScope(EditorStyles.helpBox))
            {
                EditorGUILayout.LabelField(
                    new GUIContent("Bake Settings", "Các tùy chọn đóng gói texture atlas và tạo mesh gộp"),
                    EditorStyles.boldLabel);

                settings.maxAtlasSize = EditorGUILayout.IntPopup(
                    new GUIContent("Max Atlas Size",
                        "Kích thước tối đa của texture atlas (px). Texture nguồn sẽ bị thu nhỏ nếu không đủ chỗ. " +
                        "4096 phù hợp cho hầu hết trường hợp."),
                    settings.maxAtlasSize, FMeshBakeSettings.AtlasSizeLabels.Select(l => new GUIContent(l)).ToArray(),
                    FMeshBakeSettings.AtlasSizes);
                settings.atlasPadding = EditorGUILayout.IntSlider(
                    new GUIContent("Atlas Padding",
                        "Số pixel đệm quanh mỗi texture trong atlas để tránh lem màu (bleeding) khi mipmap. Mặc định 2 là đủ."),
                    settings.atlasPadding, 0, 16);
                settings.packingAlgorithm = (MB2_PackingAlgorithmEnum)EditorGUILayout.EnumPopup(
                    new GUIContent("Packing Algorithm",
                        "Thuật toán xếp texture vào atlas:\n" +
                        "• MeshBakerTexturePacker — khuyên dùng, xếp chặt và ổn định.\n" +
                        "• Horizontal/Vertical — giữ 1 trục có thể tile (dùng cho texture lặp).\n" +
                        "• UnitysPackTextures — dùng hàm gốc của Unity.\n" +
                        "• Fast — xếp bằng GPU, nhanh nhưng ít tùy chọn hơn."),
                    settings.packingAlgorithm);

                settings.considerMeshUVs = EditorGUILayout.Toggle(
                    new GUIContent("Consider Mesh UVs",
                        "Bake đúng vùng texture mà UV thực sự dùng — cần bật khi mesh có UV tràn ra ngoài 0..1 (texture tile/lặp). " +
                        "Nên để BẬT cho an toàn."),
                    settings.considerMeshUVs);
                settings.resizePowerOfTwoTextures = EditorGUILayout.Toggle(
                    new GUIContent("Resize PoT Textures",
                        "Thu nhỏ texture power-of-two một chút (theo padding) để atlas cuối giữ được kích thước power-of-two."),
                    settings.resizePowerOfTwoTextures);
                settings.generateLightmapUV2 = EditorGUILayout.Toggle(
                    new GUIContent("Generate Lightmap UV2",
                        "Tạo layout UV2 mới cho mesh gộp để dùng với lightmap. Bật nếu object kết quả cần bake lightmap."),
                    settings.generateLightmapUV2);
                settings.pivotLocation = (MB_MeshPivotLocation)EditorGUILayout.EnumPopup(
                    new GUIContent("Pivot Location",
                        "Vị trí gốc (pivot) của mesh gộp:\n" +
                        "• boundsCenter — tâm khối bao (khuyên dùng).\n" +
                        "• worldOrigin — gốc tọa độ thế giới (0,0,0)."),
                    settings.pivotLocation);

                advancedFoldout = EditorGUILayout.Foldout(advancedFoldout, "Advanced", true);
                if (advancedFoldout)
                {
                    EditorGUI.indentLevel++;

                    settings.maxTilingBakeSize = EditorGUILayout.IntSlider(
                        new GUIContent("Max Tiling Bake Size",
                            "Giới hạn kích thước (px) khi bake vùng texture bị tile/lặp. Vùng tile lớn sẽ bị nén xuống mức này và có thể mờ đi."),
                        settings.maxTilingBakeSize, 64, 4096);
                    settings.considerNonTextureProperties = EditorGUILayout.Toggle(
                        new GUIContent("Blend Non-Texture Properties",
                            "Trộn các thuộc tính màu của material (ví dụ _Color) trực tiếp vào pixel atlas. " +
                            "Bật khi các material nguồn dùng cùng texture nhưng khác màu tint."),
                        settings.considerNonTextureProperties);

                    EditorGUI.BeginChangeCheck();
                    customPropsText = EditorGUILayout.TextField(
                        new GUIContent("Custom Shader Props",
                            "Tên các thuộc tính texture bổ sung cần bake (ngoài các tên chuẩn như _MainTex, _BumpMap...), " +
                            "cách nhau bằng dấu phẩy. Dùng khi shader của bạn có texture với tên riêng."),
                        customPropsText);
                    ignorePropsText = EditorGUILayout.TextField(
                        new GUIContent("Ignore Shader Props",
                            "Tên các thuộc tính texture KHÔNG muốn bake vào atlas, cách nhau bằng dấu phẩy."),
                        ignorePropsText);
                    if (EditorGUI.EndChangeCheck())
                    {
                        ParsePropList(customPropsText, settings.customShaderPropNames);
                        ParsePropList(ignorePropsText, settings.texturePropNamesToIgnore);
                    }

                    EditorGUI.indentLevel--;
                }
            }
        }

        private static void ParsePropList(string text, List<string> target)
        {
            target.Clear();
            foreach (string part in text.Split(','))
            {
                string trimmed = part.Trim();
                if (trimmed.Length > 0)
                    target.Add(trimmed);
            }
        }

        // ================================================================ Output

        private void DrawOutputSection()
        {
            using (new EditorGUILayout.VerticalScope(EditorStyles.helpBox))
            {
                EditorGUILayout.LabelField(
                    new GUIContent("Output", "Nơi lưu các asset kết quả (prefab, mesh, material, atlas)"),
                    EditorStyles.boldLabel);

                using (new EditorGUILayout.HorizontalScope())
                {
                    settings.outputFolder = EditorGUILayout.TextField(
                        new GUIContent("Output Folder", "Thư mục lưu kết quả — bắt buộc nằm trong Assets của project"),
                        settings.outputFolder);
                    if (GUILayout.Button(new GUIContent("...", "Chọn thư mục xuất"), GUILayout.Width(28f)))
                        BrowseOutputFolder();
                }

                settings.baseName = EditorGUILayout.TextField(
                    new GUIContent("Base Name", "Tên gốc cho các asset kết quả: {tên}.prefab, {tên}-mesh.asset, {tên}-mat.mat..."),
                    settings.baseName);

                if (session.Report != null && session.Report.HasSceneObjects)
                {
                    settings.placeResultInScene = EditorGUILayout.Toggle(
                        new GUIContent("Place Result In Scene",
                            "Để lại một bản sao của prefab kết quả trong scene sau khi bake để so sánh với object gốc."),
                        settings.placeResultInScene);
                }

                GUILayout.Space(4f);
                EditorGUILayout.LabelField(
                    new GUIContent("Sẽ tạo các file:", "Danh sách asset sẽ được ghi khi bấm Bake"),
                    EditorStyles.miniBoldLabel);
                EditorGUILayout.LabelField("  " + settings.PrefabPath, EditorStyles.miniLabel);
                EditorGUILayout.LabelField("  " + settings.MeshPath, EditorStyles.miniLabel);
                EditorGUILayout.LabelField("  " + settings.MaterialPath, EditorStyles.miniLabel);
                EditorGUILayout.LabelField("  " + settings.BakeResultsPath + "  (+ texture atlas)", EditorStyles.miniLabel);

                if (session.LastResult != null)
                    DrawBakeResult(session.LastResult);
            }
        }

        private void BrowseOutputFolder()
        {
            string chosen = EditorUtility.OpenFolderPanel("Chọn thư mục xuất", settings.outputFolder, "");
            if (string.IsNullOrEmpty(chosen))
                return;

            string projectRoot = Application.dataPath.Substring(0, Application.dataPath.Length - "Assets".Length);
            chosen = chosen.Replace('\\', '/');
            if (chosen.StartsWith(projectRoot))
                settings.outputFolder = chosen.Substring(projectRoot.Length);
            else
                EditorUtility.DisplayDialog(ToolName, "Thư mục xuất phải nằm trong thư mục Assets của project này.", "OK");

            GUI.FocusControl(null);
        }

        private void DrawBakeResult(FMeshBakeResult result)
        {
            GUILayout.Space(6f);
            EditorGUILayout.LabelField(
                new GUIContent("Kết quả bake gần nhất:", "Click vào ảnh atlas để ping texture trong Project"),
                EditorStyles.miniBoldLabel);

            using (new EditorGUILayout.HorizontalScope())
            {
                foreach (string atlasPath in result.AtlasPaths)
                {
                    var tex = AssetDatabase.LoadAssetAtPath<Texture2D>(atlasPath);
                    if (tex == null)
                        continue;

                    using (new EditorGUILayout.VerticalScope(GUILayout.Width(70f)))
                    {
                        Rect previewRect = GUILayoutUtility.GetRect(64f, 64f, GUILayout.Width(64f), GUILayout.Height(64f));
                        EditorGUI.DrawPreviewTexture(previewRect, tex, null, ScaleMode.ScaleToFit);
                        if (Event.current.type == EventType.MouseDown && previewRect.Contains(Event.current.mousePosition))
                        {
                            EditorGUIUtility.PingObject(tex);
                            Event.current.Use();
                        }

                        EditorGUILayout.LabelField(tex.name, EditorStyles.miniLabel, GUILayout.Width(64f));
                    }
                }

                GUILayout.FlexibleSpace();
            }
        }

        // ================================================================ Action bar

        private void DrawActionBar()
        {
            FStylesUtils.DrawInfoBox(
                "Bake sẽ ghi ra: prefab gộp, mesh, material, texture atlas và file bake-results vào thư mục xuất. " +
                "Object nguồn và asset gốc không bao giờ bị chỉnh sửa.");
            GUILayout.Space(4f);

            using (new EditorGUILayout.HorizontalScope())
            {
                bool outputInvalid = string.IsNullOrWhiteSpace(settings.baseName) ||
                                     string.IsNullOrWhiteSpace(settings.outputFolder);
                using (new EditorGUI.DisabledScope(outputInvalid))
                {
                    Color originalBg = GUI.backgroundColor;
                    GUI.backgroundColor = new Color(0.4f, 0.6f, 0.9f);
                    if (GUILayout.Button(
                            new GUIContent("Bake Combined Mesh",
                                "Chạy toàn bộ quy trình: bake texture atlas → gộp mesh → lưu prefab kết quả"),
                            GUILayout.Height(34)))
                        Bake();
                    GUI.backgroundColor = originalBg;
                }

                using (new EditorGUILayout.VerticalScope(GUILayout.Width(160f)))
                {
                    using (new EditorGUI.DisabledScope(session.LastResult == null))
                    {
                        if (GUILayout.Button(
                                new GUIContent("Ping Result Prefab", "Định vị prefab kết quả trong cửa sổ Project"),
                                GUILayout.Height(26)))
                        {
                            var prefab = AssetDatabase.LoadAssetAtPath<GameObject>(session.LastResult.PrefabPath);
                            if (prefab != null)
                                EditorGUIUtility.PingObject(prefab);
                        }
                    }
                }
            }

            GUILayout.Space(6f);
        }

        private void Bake()
        {
            if (AssetDatabase.LoadAssetAtPath<GameObject>(settings.PrefabPath) != null &&
                !EditorUtility.DisplayDialog(ToolName,
                    $"\"{settings.PrefabPath}\" đã tồn tại và sẽ bị ghi đè (cùng với mesh, material và atlas của nó).\n\nTiếp tục?",
                    "Ghi đè", "Hủy"))
                return;

            string error;
            FMeshBakeResult result = FMeshBakeService.Bake(session, settings, out error);
            if (result == null)
            {
                EditorUtility.DisplayDialog(ToolName, error ?? "Bake thất bại. Xem Console để biết chi tiết.", "OK");
                return;
            }

            session.LastResult = result;
            ShowNotification(new GUIContent("Đã bake xong mesh gộp"));
            Repaint();
        }

        // ================================================================ Split tab

        private void DrawSplitTab()
        {
            FStylesUtils.DrawDescription(
                "Tách một mesh thành nhiều phần riêng biệt: tự động theo UV island, chọn đỉnh thủ công, " +
                "hoặc cắt bằng khối (box/sphere/plane/circle). Object nguồn không bao giờ bị chỉnh sửa.");
            GUILayout.Space(4f);

            DrawSplitGuideSection();
            GUILayout.Space(4f);

            DrawSplitTargetSection();

            if (!splitSession.IsAnalyzed)
            {
                EditorGUILayout.Space();
                FStylesUtils.DrawInfoBox(
                    "Chọn một object trong scene có MeshFilter (kéo-thả hoặc bấm \"Use Selection\"), " +
                    "sau đó bấm \"Analyze\" để phân tích mesh thành các phần.");
                return;
            }

            if (splitSession.Analysis.IsStale)
            {
                EditorGUILayout.Space();
                EditorGUILayout.HelpBox("Mesh đã thay đổi sau khi Analyze. Hãy bấm Analyze lại.", MessageType.Warning);
                return;
            }

            // Computed once per repaint; the mask walk is O(triangles) and several sections need it.
            splitSelectedTriangleCount = splitSession.CountSelectedTriangles(splitSettings);

            EditorGUILayout.Space();
            DrawSplitModeSection();
            EditorGUILayout.Space();
            DrawSplitOutputSection();
        }

        private void DrawSplitGuideSection()
        {
            using (new EditorGUILayout.VerticalScope(EditorStyles.helpBox))
            {
                splitGuideFoldout = EditorGUILayout.Foldout(splitGuideFoldout, "Hướng dẫn sử dụng", true, EditorStyles.foldoutHeader);
                if (!splitGuideFoldout)
                    return;

                var stepStyle = new GUIStyle(EditorStyles.wordWrappedLabel) { fontSize = 11 };
                EditorGUILayout.LabelField(
                    "1.  Chọn object trong scene có MeshFilter (mesh cần bật Read/Write trong import settings).", stepStyle);
                EditorGUILayout.LabelField(
                    "2.  Bấm \"Analyze\" — tool phân tích UV để chia mesh thành các phần (ví dụ cây → lá + thân).", stepStyle);
                EditorGUILayout.LabelField(
                    "3.  Bấm \"Mở Scene Tool\" để chọn phần trực quan trong Scene View: " +
                    "hover để xem phần, click để chọn. Chế độ Thủ công cho phép chọn từng đỉnh (click/kéo brush); " +
                    "chế độ Nâng cao cho phép đặt khối cắt và di chuyển bằng gizmo.", stepStyle);
                EditorGUILayout.LabelField(
                    "4.  Bấm \"Tách phần đã chọn\" (2 mảnh: phần chọn + phần còn lại) hoặc " +
                    "\"Tách tất cả part\" (mỗi part một mesh riêng).", stepStyle);
                EditorGUILayout.LabelField(
                    "5.  Kết quả: mỗi phần một mesh asset + một prefab có con cho từng phần, giữ nguyên material.", stepStyle);
            }
        }

        private void DrawSplitTargetSection()
        {
            using (new EditorGUILayout.VerticalScope(EditorStyles.helpBox))
            {
                EditorGUILayout.LabelField(
                    new GUIContent("Target", "Object trong scene có MeshFilter sẽ được tách"),
                    EditorStyles.boldLabel);

                using (new EditorGUILayout.HorizontalScope())
                {
                    EditorGUI.BeginChangeCheck();
                    var newTarget = (GameObject)EditorGUILayout.ObjectField(splitSession.Target, typeof(GameObject), true);
                    if (EditorGUI.EndChangeCheck())
                        SetSplitTarget(newTarget);

                    if (GUILayout.Button(
                            new GUIContent("Use Selection", "Dùng object đang chọn trong Hierarchy"),
                            GUILayout.Width(110f)))
                        SetSplitTarget(Selection.activeGameObject);
                }

                Mesh mesh = splitSession.TargetMesh;
                bool canAnalyze = false;

                if (splitSession.Target == null)
                {
                    EditorGUILayout.HelpBox("Chưa chọn object mục tiêu.", MessageType.Info);
                }
                else if (splitSession.TargetMeshFilter == null || mesh == null)
                {
                    bool hasSkinned = splitSession.Target.GetComponentInChildren<SkinnedMeshRenderer>() != null;
                    EditorGUILayout.HelpBox(hasSkinned
                            ? "SkinnedMeshRenderer chưa được hỗ trợ (v1). Object cần có MeshFilter với mesh."
                            : "Object cần có component MeshFilter với mesh hợp lệ.",
                        MessageType.Info);
                }
                else if (!mesh.isReadable)
                {
                    EditorGUILayout.HelpBox(
                        $"Mesh \"{mesh.name}\" không đọc được từ CPU (Read/Write đang tắt). " +
                        "Hãy bật Read/Write trong import settings của model rồi Analyze lại.",
                        MessageType.Warning);
                }
                else
                {
                    canAnalyze = true;
                    long indexCount = 0;
                    for (int s = 0; s < mesh.subMeshCount; s++)
                        indexCount += mesh.GetIndexCount(s);
                    EditorGUILayout.LabelField(
                        $"Mesh: {mesh.name} — {mesh.vertexCount:N0} vertices, {indexCount / 3:N0} triangles, {mesh.subMeshCount} submesh",
                        EditorStyles.miniLabel);
                    if (splitSession.IsAnalyzed && !splitSession.Analysis.HasUv)
                        FStylesUtils.DrawInfoBox(
                            "Mesh không có UV — chế độ Đơn giản tách theo các khối liền nhau (spatial islands) thay vì UV island.");
                }

                GUILayout.Space(4f);
                using (new EditorGUI.DisabledScope(!canAnalyze))
                {
                    Color originalBg = GUI.backgroundColor;
                    GUI.backgroundColor = new Color(0.4f, 0.8f, 1f);
                    if (GUILayout.Button(
                            new GUIContent("Analyze", "Phân tích mesh: weld vertex, tìm UV island và gộp thành các part"),
                            GUILayout.Height(26)))
                        AnalyzeSplitTarget();
                    GUI.backgroundColor = originalBg;
                }
            }
        }

        private void SetSplitTarget(GameObject newTarget)
        {
            if (newTarget == splitSession.Target)
                return;

            FMeshSplitterSceneOverlay.CloseOverlay();
            splitSession.Target = newTarget;
            splitSession.InvalidateAnalysis();
            GUI.FocusControl(null);
        }

        private void AnalyzeSplitTarget()
        {
            Mesh mesh = splitSession.TargetMesh;
            if (mesh == null)
                return;

            splitSession.InvalidateAnalysis();
            splitSession.Analysis = FMeshSplitAnalyzer.Analyze(mesh, splitSettings);
            if (splitSession.Analysis == null)
            {
                EditorUtility.DisplayDialog(ToolName, "Analyze thất bại — mesh không đọc được.", "OK");
                return;
            }

            if (string.IsNullOrWhiteSpace(splitSettings.baseName) || splitSettings.baseName == "SplitMesh")
                splitSettings.baseName = splitSession.Target.name + "_Split";

            if (string.IsNullOrWhiteSpace(splitSettings.outputFolder) && splitSession.Target != null)
                splitSettings.outputFolder =
                    ResolveDefaultOutputFolder(new[] { splitSession.Target }) ?? splitSettings.outputFolder;

            FMeshSplitterSceneOverlay.NotifyAnalysisChanged();
            Repaint();
        }

        private void DrawSplitModeSection()
        {
            using (new EditorGUILayout.VerticalScope(EditorStyles.helpBox))
            {
                EditorGUILayout.LabelField(
                    new GUIContent("Split Mode", "Cách chọn phần mesh cần tách"),
                    EditorStyles.boldLabel);

                EditorGUI.BeginChangeCheck();
                int newMode = GUILayout.Toolbar((int)splitSettings.mode, SplitModeLabels, GUILayout.Height(22f));
                if (EditorGUI.EndChangeCheck() && newMode != (int)splitSettings.mode)
                {
                    splitSettings.mode = (FMeshSplitMode)newMode;
                    GUI.FocusControl(null);
                    FMeshSplitterSceneOverlay.NotifySelectionChanged();
                    SceneView.RepaintAll();
                }

                GUILayout.Space(4f);
                switch (splitSettings.mode)
                {
                    case FMeshSplitMode.Simple:
                        DrawSimpleModeSettings();
                        break;
                    case FMeshSplitMode.Manual:
                        DrawManualModeSettings();
                        break;
                    case FMeshSplitMode.Advanced:
                        DrawAdvancedModeSettings();
                        break;
                }

                GUILayout.Space(6f);
                DrawSceneToolButton();
            }
        }

        private void DrawSimpleModeSettings()
        {
            FMeshSplitAnalysis analysis = splitSession.Analysis;

            if (analysis.HasUv)
            {
                EditorGUI.BeginChangeCheck();
                splitSettings.mergeRepeatedIslands = EditorGUILayout.Toggle(
                    new GUIContent("Gộp island lặp",
                        "Gộp các UV island dùng chung vùng atlas (ví dụ nhiều lá cây cùng texture) thành một part."),
                    splitSettings.mergeRepeatedIslands);
                using (new EditorGUI.DisabledScope(!splitSettings.mergeRepeatedIslands))
                {
                    splitSettings.uvMergeOverlap = EditorGUILayout.Slider(
                        new GUIContent("Ngưỡng gộp UV",
                            "Tỉ lệ chồng lấp UV tối thiểu để gộp 2 island thành 1 part. Tăng lên nếu bị gộp nhầm, giảm xuống nếu ra quá nhiều part."),
                        splitSettings.uvMergeOverlap, 0f, 1f);
                }
                if (EditorGUI.EndChangeCheck())
                {
                    FMeshSplitAnalyzer.Remerge(analysis, splitSettings);
                    splitSession.SelectedPartIds.Clear();
                    FMeshSplitterSceneOverlay.NotifyAnalysisChanged();
                }
            }

            GUILayout.Space(4f);
            if (analysis.Parts.Count > 256)
                EditorGUILayout.HelpBox(
                    $"Quá nhiều part ({analysis.Parts.Count}) — hãy tăng \"Ngưỡng gộp UV\" hoặc bật \"Gộp island lặp\".",
                    MessageType.Warning);

            EditorGUILayout.LabelField(
                $"Parts ({analysis.Parts.Count}) — {splitSession.SelectedPartIds.Count} đã chọn",
                EditorStyles.miniBoldLabel);
            DrawPartTable(analysis);
        }

        private void DrawPartTable(FMeshSplitAnalysis analysis)
        {
            using (new EditorGUILayout.HorizontalScope())
            {
                EditorGUILayout.LabelField("Chọn", EditorStyles.miniBoldLabel, GUILayout.Width(40f));
                EditorGUILayout.LabelField("Màu", EditorStyles.miniBoldLabel, GUILayout.Width(34f));
                EditorGUILayout.LabelField("Tên part", EditorStyles.miniBoldLabel, GUILayout.Width(150f));
                EditorGUILayout.LabelField("Tam giác", EditorStyles.miniBoldLabel, GUILayout.Width(70f));
                EditorGUILayout.LabelField(new GUIContent("Xuất", "Bỏ tick để gộp part này vào phần \"Rest\" khi Tách tất cả part"),
                    EditorStyles.miniBoldLabel, GUILayout.Width(40f));
            }

            partTableScroll = EditorGUILayout.BeginScrollView(partTableScroll,
                GUILayout.Height(Mathf.Min(PartTableHeight, 20f * analysis.Parts.Count + 8f)));

            for (int i = 0; i < analysis.Parts.Count; i++)
            {
                FMeshPartInfo part = analysis.Parts[i];

                Rect rowRect = EditorGUILayout.BeginHorizontal(GUILayout.Height(18f));
                if (Event.current.type == EventType.Repaint && (i & 1) == 1)
                    EditorGUI.DrawRect(rowRect, AltRowFill);

                bool wasSelected = splitSession.SelectedPartIds.Contains(part.PartId);
                bool nowSelected = EditorGUILayout.Toggle(wasSelected, GUILayout.Width(40f));
                if (nowSelected != wasSelected)
                {
                    if (nowSelected) splitSession.SelectedPartIds.Add(part.PartId);
                    else splitSession.SelectedPartIds.Remove(part.PartId);
                    FMeshSplitterSceneOverlay.NotifySelectionChanged();
                }

                Rect swatchRect = GUILayoutUtility.GetRect(26f, 14f, GUILayout.Width(34f));
                swatchRect.y += 2f;
                swatchRect.width = 26f;
                EditorGUI.DrawRect(swatchRect, part.Color);

                part.Name = EditorGUILayout.DelayedTextField(part.Name, GUILayout.Width(150f));
                EditorGUILayout.LabelField(part.TriangleCount.ToString("N0"), EditorStyles.miniLabel, GUILayout.Width(70f));
                part.IncludeInSplit = EditorGUILayout.Toggle(part.IncludeInSplit, GUILayout.Width(40f));

                EditorGUILayout.EndHorizontal();
            }

            EditorGUILayout.EndScrollView();
        }

        private void DrawManualModeSettings()
        {
            splitSettings.pickRadiusPixels = EditorGUILayout.Slider(
                new GUIContent("Pick Radius (px)", "Khoảng cách tối đa (pixel) từ chuột đến đỉnh khi click chọn từng đỉnh."),
                splitSettings.pickRadiusPixels, 4f, 40f);
            splitSettings.brushRadiusPixels = EditorGUILayout.Slider(
                new GUIContent("Brush Radius (px)", "Bán kính brush (pixel) khi kéo chuột để chọn nhiều đỉnh."),
                splitSettings.brushRadiusPixels, 5f, 120f);

            GUILayout.Space(2f);
            EditorGUILayout.LabelField(
                $"{splitSession.SelectedCanonicalVerts.Count:N0} đỉnh đã chọn — {splitSelectedTriangleCount:N0} tam giác",
                EditorStyles.miniLabel);
            FStylesUtils.DrawInfoBox(
                "Mở Scene Tool để chọn đỉnh trong Scene View: click = chọn/bỏ (Shift thêm, Ctrl bỏ), kéo = brush. " +
                "Tam giác được tách khi cả 3 đỉnh được chọn — dùng Grow để mở rộng vùng chọn.");
        }

        private void DrawAdvancedModeSettings()
        {
            EditorGUI.BeginChangeCheck();
            splitSettings.primitiveKind = (FSplitPrimitiveKind)EditorGUILayout.EnumPopup(
                new GUIContent("Khối cắt",
                    "Box: hộp theo scale. Sphere: cầu, bán kính = max(scale)/2. " +
                    "Plane: mặt phẳng tách đôi theo trục +Y của gizmo. Circle: trụ vô hạn quanh trục Y."),
                splitSettings.primitiveKind);
            if (EditorGUI.EndChangeCheck())
            {
                splitSession.Primitive.Kind = splitSettings.primitiveKind;
                FMeshSplitterSceneOverlay.NotifySelectionChanged();
            }

            GUILayout.Space(2f);
            EditorGUILayout.LabelField(
                $"{splitSelectedTriangleCount:N0} tam giác bên trong khối",
                EditorStyles.miniLabel);
            FStylesUtils.DrawInfoBox(
                "Mở Scene Tool — tool sẽ tạo một khối cắt trong scene, di chuyển/xoay/scale bằng gizmo của Unity. " +
                "Tam giác có tâm nằm trong khối sẽ thuộc phần được tách (không slice tam giác).");
        }

        private void DrawSceneToolButton()
        {
            if (FMeshSplitterSceneOverlay.IsOpen)
            {
                using (new EditorGUILayout.HorizontalScope())
                {
                    EditorGUILayout.LabelField("Scene Tool đang mở trong Scene View.", EditorStyles.miniBoldLabel);
                    if (GUILayout.Button("Đóng", GUILayout.Width(70f)))
                    {
                        FMeshSplitterSceneOverlay.CloseOverlay();
                        Repaint();
                    }
                }
                return;
            }

            Color originalBg = GUI.backgroundColor;
            GUI.backgroundColor = new Color(0.5f, 0.9f, 0.6f);
            if (GUILayout.Button(
                    new GUIContent("Mở Scene Tool",
                        "Mở cửa sổ tool nổi trong Scene View để chọn phần mesh trực quan (hover/click/brush/gizmo)."),
                    GUILayout.Height(26)))
            {
                FMeshSplitterSceneOverlay.Open(splitSession, splitSettings, Repaint, DoSplitSelected, DoSplitAllParts);
                Repaint();
            }
            GUI.backgroundColor = originalBg;
        }

        private void DrawSplitOutputSection()
        {
            using (new EditorGUILayout.VerticalScope(EditorStyles.helpBox))
            {
                EditorGUILayout.LabelField(
                    new GUIContent("Output", "Nơi lưu các asset kết quả (prefab + mesh của từng phần)"),
                    EditorStyles.boldLabel);

                using (new EditorGUILayout.HorizontalScope())
                {
                    splitSettings.outputFolder = EditorGUILayout.TextField(
                        new GUIContent("Output Folder", "Thư mục lưu kết quả — bắt buộc nằm trong Assets của project"),
                        splitSettings.outputFolder);
                    if (GUILayout.Button(new GUIContent("...", "Chọn thư mục xuất"), GUILayout.Width(28f)))
                        BrowseSplitOutputFolder();
                }

                splitSettings.baseName = EditorGUILayout.TextField(
                    new GUIContent("Base Name", "Tên gốc: {tên}/{tên}.prefab + {tên}_{part}.asset"),
                    splitSettings.baseName);

                splitSettings.placeResultInScene = EditorGUILayout.Toggle(
                    new GUIContent("Place Result In Scene",
                        "Đặt một instance của prefab kết quả vào scene tại đúng vị trí object gốc để so sánh."),
                    splitSettings.placeResultInScene);

                GUILayout.Space(4f);
                EditorGUILayout.LabelField(
                    new GUIContent("Sẽ tạo các file:", "Danh sách asset sẽ được ghi khi tách"),
                    EditorStyles.miniBoldLabel);
                EditorGUILayout.LabelField("  " + splitSettings.PrefabPath, EditorStyles.miniLabel);
                EditorGUILayout.LabelField("  " + splitSettings.PartMeshPath("{part}"), EditorStyles.miniLabel);
            }
        }

        private void BrowseSplitOutputFolder()
        {
            string chosen = EditorUtility.OpenFolderPanel("Chọn thư mục xuất", splitSettings.outputFolder, "");
            if (string.IsNullOrEmpty(chosen))
                return;

            string projectRoot = Application.dataPath.Substring(0, Application.dataPath.Length - "Assets".Length);
            chosen = chosen.Replace('\\', '/');
            if (chosen.StartsWith(projectRoot))
                splitSettings.outputFolder = chosen.Substring(projectRoot.Length);
            else
                EditorUtility.DisplayDialog(ToolName, "Thư mục xuất phải nằm trong thư mục Assets của project này.", "OK");

            GUI.FocusControl(null);
        }

        private void DrawSplitActionBar()
        {
            FStylesUtils.DrawInfoBox(
                "Tách sẽ ghi ra: mesh asset cho từng phần + một prefab có con cho từng phần. " +
                "Object nguồn không bao giờ bị chỉnh sửa.");
            GUILayout.Space(4f);

            bool outputInvalid = string.IsNullOrWhiteSpace(splitSettings.baseName) ||
                                 string.IsNullOrWhiteSpace(splitSettings.outputFolder);
            int selectedTriangles = splitSelectedTriangleCount;

            using (new EditorGUILayout.HorizontalScope())
            {
                Color originalBg = GUI.backgroundColor;

                using (new EditorGUI.DisabledScope(outputInvalid || selectedTriangles == 0))
                {
                    GUI.backgroundColor = new Color(0.4f, 0.6f, 0.9f);
                    if (GUILayout.Button(
                            new GUIContent("Tách phần đã chọn",
                                "Tách mesh thành 2 phần: phần đã chọn và phần còn lại"),
                            GUILayout.Height(34)))
                        DoSplitSelected();
                }

                if (splitSettings.mode == FMeshSplitMode.Simple)
                {
                    using (new EditorGUI.DisabledScope(outputInvalid || splitSession.Analysis.Parts.Count < 2))
                    {
                        GUI.backgroundColor = new Color(0.55f, 0.75f, 1f);
                        if (GUILayout.Button(
                                new GUIContent("Tách tất cả part",
                                    "Mỗi part được tick \"Xuất\" thành một mesh riêng; các part còn lại gộp vào \"Rest\""),
                                GUILayout.Height(34), GUILayout.Width(160f)))
                            DoSplitAllParts();
                    }
                }

                GUI.backgroundColor = originalBg;

                using (new EditorGUILayout.VerticalScope(GUILayout.Width(160f)))
                {
                    using (new EditorGUI.DisabledScope(splitSession.LastResult == null))
                    {
                        if (GUILayout.Button(
                                new GUIContent("Ping Result Prefab", "Định vị prefab kết quả trong cửa sổ Project"),
                                GUILayout.Height(26)))
                        {
                            var prefab = AssetDatabase.LoadAssetAtPath<GameObject>(splitSession.LastResult.PrefabPath);
                            if (prefab != null)
                                EditorGUIUtility.PingObject(prefab);
                        }
                    }
                }
            }

            GUILayout.Space(6f);
        }

        private void DoSplitSelected()
        {
            bool[] mask = splitSession.GetSelectedTriangleMask(splitSettings);
            if (mask == null)
                return;

            var selected = new List<int>();
            var rest = new List<int>();
            for (int t = 0; t < mask.Length; t++)
                (mask[t] ? selected : rest).Add(t);

            bool isPlane = splitSettings.mode == FMeshSplitMode.Advanced &&
                           splitSettings.primitiveKind == FSplitPrimitiveKind.Plane;
            string selectedName = isPlane ? "SideA" : BuildSelectedPieceName();
            string restName = isPlane ? "SideB" : "Rest";

            RunSplit(new List<(string, List<int>)>
            {
                (selectedName, selected),
                (restName, rest)
            });
        }

        private string BuildSelectedPieceName()
        {
            if (splitSettings.mode == FMeshSplitMode.Simple && splitSession.SelectedPartIds.Count == 1)
            {
                foreach (FMeshPartInfo part in splitSession.Analysis.Parts)
                    if (splitSession.SelectedPartIds.Contains(part.PartId))
                        return part.Name;
            }
            return "Selected";
        }

        private void DoSplitAllParts()
        {
            FMeshSplitAnalysis analysis = splitSession.Analysis;
            var pieces = new List<(string, List<int>)>();
            var partBuckets = new Dictionary<int, List<int>>();
            var rest = new List<int>();

            foreach (FMeshPartInfo part in analysis.Parts)
                if (part.IncludeInSplit)
                    partBuckets[part.PartId] = new List<int>();

            for (int t = 0; t < analysis.TriangleCount; t++)
            {
                if (partBuckets.TryGetValue(analysis.TrianglePartId[t], out List<int> bucket))
                    bucket.Add(t);
                else
                    rest.Add(t);
            }

            foreach (FMeshPartInfo part in analysis.Parts)
                if (partBuckets.TryGetValue(part.PartId, out List<int> bucket))
                    pieces.Add((part.Name, bucket));
            if (rest.Count > 0)
                pieces.Add(("Rest", rest));

            RunSplit(pieces);
        }

        private void RunSplit(List<(string, List<int>)> pieces)
        {
            if (AssetDatabase.LoadAssetAtPath<GameObject>(splitSettings.PrefabPath) != null &&
                !EditorUtility.DisplayDialog(ToolName,
                    $"\"{splitSettings.PrefabPath}\" đã tồn tại và sẽ bị ghi đè (cùng với các mesh part của nó).\n\nTiếp tục?",
                    "Ghi đè", "Hủy"))
                return;

            string error;
            FMeshSplitResult result = FMeshSplitService.Split(splitSession, splitSettings, pieces, out error);
            if (result == null)
            {
                EditorUtility.DisplayDialog(ToolName, error ?? "Tách mesh thất bại. Xem Console để biết chi tiết.", "OK");
                return;
            }

            splitSession.LastResult = result;
            ShowNotification(new GUIContent("Đã tách mesh xong"));
            Repaint();
        }
    }
}
#endif
