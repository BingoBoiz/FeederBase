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
    /// Editor window that bakes many meshes/materials into one combined mesh:
    /// source textures are packed into an atlas, UVs are remapped and all meshes
    /// are merged into a single mesh + material + prefab (MeshBaker pipeline).
    /// </summary>
    public sealed class FMeshModifierWindow : OdinEditorWindow
    {
        private const string ToolName = "Feeder Mesh Modifier";
        private const int MenuPriority = 3;
        private const float LeftColumnWidth = 300f;
        private const float RendererTableHeight = 180f;

        private static readonly Color HoverFill = new Color(0f, 1f, 1f, 0.12f);
        private static readonly Color AltRowFill = new Color(1f, 1f, 1f, 0.03f);
        private static readonly Color WarnRed = new Color(1f, 0.45f, 0.45f);

        private readonly FMeshBakeSession session = new FMeshBakeSession();
        private readonly FMeshBakeSettings settings = new FMeshBakeSettings();

        private Vector2 windowScroll;
        private Vector2 tableScroll;
        private int hoverRowIndex = -1;
        private bool guideFoldout;
        private bool advancedFoldout;
        private string customPropsText = string.Empty;
        private string ignorePropsText = string.Empty;

        [MenuItem("Tools/Feeder/Feeder Mesh Modifier", priority = MenuPriority)]
        private static void OpenWindow()
        {
            var window = GetWindow<FMeshModifierWindow>();
            window.titleContent = FeederIconCatalog.CreateWindowTitle(ToolName, FeederIconCatalog.WindowMenuTitleIcon);
            window.minSize = new Vector2(760, 560);
            window.Show();
        }

        protected override void OnEnable()
        {
            base.OnEnable();
            wantsMouseMove = true;
        }

        protected override void OnImGUI()
        {
            GUILayout.Space(6f);
            windowScroll = EditorGUILayout.BeginScrollView(windowScroll, GUILayout.ExpandHeight(true));

            StylesUtils.DrawDescription(
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
                StylesUtils.DrawInfoBox(
                    "Kéo-thả prefab hoặc object trong scene vào danh sách Targets (hoặc bấm \"Use Selection\"), " +
                    "sau đó bấm \"Analyze\" để kiểm tra mesh, material và texture trước khi bake.");
            }

            EditorGUILayout.EndScrollView();

            if (session.IsAnalyzed && session.Report.Rows.Count > 0)
            {
                EditorGUILayout.Space();
                DrawActionBar();
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
            StylesUtils.DrawInfoBox(
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
    }
}
#endif
