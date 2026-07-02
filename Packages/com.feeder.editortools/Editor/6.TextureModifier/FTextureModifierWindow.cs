#if UNITY_EDITOR
using System.Collections.Generic;
using System.IO;
using Sirenix.OdinInspector.Editor;
using UnityEditor;
using UnityEngine;

namespace Feeder
{
    /// <summary>
    /// Editor window for recoloring meshes and textures.
    /// Tab 1 (Mesh Palette): writes new colors into free palette cells and remaps mesh UVs.
    /// Tab 2 (Texture Recolor): recolors the texture pixels directly while preserving shading.
    /// </summary>
    public sealed class FTextureModifierWindow : OdinEditorWindow
    {
        private enum Tab { MeshPalette, TextureRecolor }

        private const string ToolName = "Feeder Texture Modifier";
        private const string GeneratedFolder = "Assets/Generated Mesh Palette Colors";
        private const int MenuPriority = 2;
        private const float LeftColumnWidth = 300f;

        private static readonly string[] TabLabels = { "Mesh Palette", "Texture Recolor" };
        private static readonly string[] RecolorPreviewLabels = { "Original", "Result", "Mask" };

        private Tab tab;

        // ---- Mesh Palette tab state ----
        private readonly MeshPaletteColorizerSession session = new MeshPaletteColorizerSession();
        private readonly MeshPalettePreviewController preview = new MeshPalettePreviewController();

        private Renderer targetRenderer;
        private int materialSlotIndex;
        private int colorTolerance = 4;
        private PaletteColorBuildResult currentBuildResult;

        private int hoverColorIndex = -1;
        private bool colorEditing;
        private bool applied;
        private static readonly Color HighlightFill = new Color(0f, 1f, 1f, 0.45f);

        // ---- Texture Recolor tab state ----
        private readonly TextureRecolorSession recolorSession = new TextureRecolorSession();
        private readonly TextureRecolorPreviewController recolorPreview = new TextureRecolorPreviewController();

        private Renderer recolorRenderer;
        private int recolorSlotIndex;
        private int recolorMaxClusters = 8;
        private RecolorMaskSettings recolorMask = RecolorMaskSettings.Default;
        private RecolorPreviewMode recolorPreviewMode = RecolorPreviewMode.Result;
        private bool recolorAdvancedFoldout;
        private bool recolorScenePreview = true;
        private bool recolorPreviewDirty;
        private bool recolorApplied;

        private Vector2 windowScroll;

        private PaletteTexture Palette => session.Palette;
        private List<UsedColor> UsedColors => session.UsedColors;
        private int TexW => session.TextureWidth;
        private int TexH => session.TextureHeight;

        [MenuItem("Tools/Feeder/Feeder Texture Modifier", priority = MenuPriority)]
        private static void OpenWindow()
        {
            var window = GetWindow<FTextureModifierWindow>();
            window.titleContent = FeederIconCatalog.CreateWindowTitle(ToolName, FeederIconCatalog.WindowMenuTitleIcon);
            window.minSize = new Vector2(760, 520);
            window.Show();
        }

        protected override void OnEnable()
        {
            base.OnEnable();
            wantsMouseMove = true;
            SceneView.duringSceneGui -= OnSceneGUIDraw;
            SceneView.duringSceneGui += OnSceneGUIDraw;
        }

        protected override void OnDisable()
        {
            SceneView.duringSceneGui -= OnSceneGUIDraw;
            if (!applied)
                preview.Revert(session.TargetRenderer);

            preview.Cleanup();
            session.Reset();

            if (!recolorApplied)
                recolorPreview.RevertScenePreview(recolorSession.TargetRenderer);

            recolorPreview.Cleanup();
            recolorSession.Reset();
            base.OnDisable();
        }

        protected override void OnImGUI()
        {
            GUILayout.Space(4f);
            int newTab = GUILayout.Toolbar((int)tab, TabLabels, EditorStyles.toolbarButton, GUILayout.Height(24f));
            if (newTab != (int)tab)
                SwitchTab((Tab)newTab);

            windowScroll = EditorGUILayout.BeginScrollView(windowScroll, GUILayout.ExpandHeight(true));
            GUILayout.Space(4f);

            if (tab == Tab.MeshPalette)
                DrawMeshPaletteTab();
            else
                DrawTextureRecolorTab();

            EditorGUILayout.EndScrollView();

            if (tab == Tab.MeshPalette && Palette != null)
            {
                EditorGUILayout.Space();
                DrawActionButtons();
            }
            else if (tab == Tab.TextureRecolor && recolorSession.IsLoaded)
            {
                EditorGUILayout.Space();
                DrawRecolorActionButtons();
            }
        }

        private void SwitchTab(Tab next)
        {
            if (tab == Tab.MeshPalette)
            {
                // hide the mesh preview while the other tab is active; the session survives
                RevertPreview();
                hoverColorIndex = -1;
                colorEditing = false;
            }
            else
            {
                recolorPreview.RevertScenePreview(recolorSession.TargetRenderer);
            }

            tab = next;
            GUI.FocusControl(null);

            if (tab == Tab.MeshPalette)
            {
                if (session.IsLoaded && AnyChanged())
                    BuildPreview();
            }
            else
            {
                recolorPreviewDirty = true;
            }

            SceneView.RepaintAll();
            Repaint();
        }

        // ================================================================ Mesh Palette tab

        private void DrawMeshPaletteTab()
        {
            StylesUtils.DrawDescription(
                "Recolor meshes that use a palette texture: select a Renderer, load and analyze its palette usage, edit colors, then apply.");
            GUILayout.Space(6f);

            DrawTargetSection();

            if (Palette != null)
            {
                EditorGUILayout.Space();
                using (new EditorGUILayout.HorizontalScope())
                {
                    using (new EditorGUILayout.VerticalScope(GUILayout.Width(LeftColumnWidth)))
                        DrawUsedColorsSection();

                    GUILayout.Space(12f);

                    using (new EditorGUILayout.VerticalScope(GUILayout.ExpandWidth(true)))
                        DrawPaletteSection();
                }
            }
            else
            {
                EditorGUILayout.Space();
                StylesUtils.DrawInfoBox("Select a renderer that uses a palette texture, then click \"Load & Analyze\".");
            }
        }

        private void DrawTargetSection()
        {
            using (new EditorGUILayout.VerticalScope(EditorStyles.helpBox))
            {
                EditorGUILayout.LabelField("Target", EditorStyles.boldLabel);

                using (new EditorGUILayout.HorizontalScope())
                {
                    EditorGUI.BeginChangeCheck();
                    targetRenderer = (Renderer)EditorGUILayout.ObjectField(
                        "Target Renderer", targetRenderer, typeof(Renderer), true);
                    if (EditorGUI.EndChangeCheck())
                        ResetLoadedState();

                    if (GUILayout.Button("Use Selection", GUILayout.Width(130)))
                    {
                        var go = Selection.activeGameObject;
                        var r = go != null ? go.GetComponent<Renderer>() : null;
                        if (r == null)
                        {
                            EditorUtility.DisplayDialog(ToolName, "The selected GameObject does not have a Renderer.", "OK");
                        }
                        else
                        {
                            targetRenderer = r;
                            ResetLoadedState();
                            materialSlotIndex = session.AutoDetectSlot(targetRenderer, materialSlotIndex);
                        }
                    }
                }

                if (targetRenderer == null)
                    return;

                var mats = targetRenderer.sharedMaterials;
                if (mats != null && mats.Length > 1)
                {
                    var names = new string[mats.Length];
                    for (int i = 0; i < mats.Length; i++)
                        names[i] = $"{i}: {(mats[i] != null ? mats[i].name : "<null>")}";

                    materialSlotIndex = Mathf.Clamp(materialSlotIndex, 0, mats.Length - 1);
                    materialSlotIndex = EditorGUILayout.Popup("Material Slot", materialSlotIndex, names);
                }

                colorTolerance = EditorGUILayout.IntSlider("Color Tolerance", colorTolerance, 0, 64);

                Color originalBg = GUI.backgroundColor;
                GUI.backgroundColor = new Color(0.4f, 0.8f, 1f);
                if (GUILayout.Button("Load & Analyze", GUILayout.Height(26)))
                    LoadAndAnalyze();
                GUI.backgroundColor = originalBg;
            }
        }

        private void DrawUsedColorsSection()
        {
            using (new EditorGUILayout.VerticalScope(EditorStyles.helpBox))
            {
                var usedColors = UsedColors;
                EditorGUILayout.LabelField($"Mesh uses {usedColors.Count} palette color groups", EditorStyles.boldLabel);

                if (usedColors.Count == 0)
                {
                    EditorGUILayout.HelpBox(
                        "No colors were detected. The mesh may not use UV0 to sample this palette, or the model may have Read/Write disabled.",
                        MessageType.Warning);
                    return;
                }

                EditorGUILayout.LabelField(
                    "Hover a color row to highlight the related mesh faces in the Scene view. Clicking a ColorField temporarily disables the highlight.",
                    EditorStyles.wordWrappedMiniLabel);

                Event e = Event.current;
                bool canHover = e.type == EventType.Repaint || e.type == EventType.MouseMove;
                int hoverCandidate = -1;

                for (int i = 0; i < usedColors.Count; i++)
                {
                    var uc = usedColors[i];
                    var marker = preview.MarkerForRow(i);
                    var row = new EditorGUILayout.HorizontalScope();
                    using (row)
                    {
                        if (i == hoverColorIndex)
                            EditorGUI.DrawRect(row.rect, new Color(0f, 1f, 1f, 0.12f));

                        using (new EditorGUILayout.VerticalScope())
                        {
                            using (new EditorGUILayout.HorizontalScope())
                            {
                                Rect sw = GUILayoutUtility.GetRect(22, 18, GUILayout.Width(22));
                                EditorGUI.DrawRect(sw, (Color)uc.color);

                                EditorGUI.BeginChangeCheck();
                                uc.newColor = EditorGUILayout.ColorField(uc.newColor, GUILayout.ExpandWidth(true));
                                Rect colorFieldRect = GUILayoutUtility.GetLastRect();
                                if (EditorGUI.EndChangeCheck())
                                    BuildPreview();

                                if (e.type == EventType.MouseDown && colorFieldRect.Contains(e.mousePosition))
                                {
                                    colorEditing = true;
                                    if (hoverColorIndex != -1)
                                    {
                                        hoverColorIndex = -1;
                                        SceneView.RepaintAll();
                                    }
                                }

                                Rect chip = GUILayoutUtility.GetRect(14, 14, GUILayout.Width(14));
                                if (marker != null)
                                {
                                    EditorGUI.DrawRect(chip, new Color(0f, 0f, 0f, 0.85f));
                                    PaletteGUI.DrawBorder(chip, PaletteGUI.FrameColor(marker.kind), 2f);
                                }
                            }

                            using (new EditorGUILayout.HorizontalScope())
                            {
                                EditorGUILayout.LabelField($"{uc.faceCount} faces", EditorStyles.miniLabel, GUILayout.Width(60));

                                string tag =
                                    marker == null ? "" :
                                    marker.kind == SwatchKind.New ? $"#{i + 1} new cell" :
                                    marker.kind == SwatchKind.Reused ? $"#{i + 1} reused" : $"#{i + 1} no space";
                                EditorGUILayout.LabelField(tag, EditorStyles.miniLabel, GUILayout.ExpandWidth(true));

                                using (new EditorGUI.DisabledScope(!uc.Changed))
                                {
                                    if (GUILayout.Button("Reset", GUILayout.Width(50)))
                                    {
                                        uc.newColor = (Color)uc.color;
                                        BuildPreview();
                                    }
                                }
                            }
                        }
                    }

                    if (canHover && row.rect.Contains(e.mousePosition))
                        hoverCandidate = i;
                }

                if (canHover && !colorEditing && hoverCandidate != hoverColorIndex)
                {
                    hoverColorIndex = hoverCandidate;
                    SceneView.RepaintAll();
                    Repaint();
                }
            }
        }

        private void OnSceneGUIDraw(SceneView sv)
        {
            if (tab != Tab.MeshPalette)
                return;

            MeshPaletteSceneOverlay.Draw(
                session.TargetRenderer,
                session.Analysis,
                hoverColorIndex,
                colorEditing,
                HighlightFill);
        }

        private void DrawPaletteSection()
        {
            using (new EditorGUILayout.VerticalScope(EditorStyles.helpBox))
            {
                bool hasPreview = preview.PreviewTexture != null;

                EditorGUILayout.LabelField(
                    hasPreview ? "Palette (Live Preview)" : "Reference Palette",
                    EditorStyles.boldLabel);

                using (new EditorGUILayout.HorizontalScope())
                {
                    EditorGUI.BeginChangeCheck();
                    int newCell = EditorGUILayout.IntField("Cell Size (px)", Palette.CellSize);
                    if (EditorGUI.EndChangeCheck())
                    {
                        Palette.CellSize = Mathf.Clamp(newCell, 2, Mathf.Min(Palette.Width, Palette.Height));
                        BuildPreview();
                    }
                    EditorGUILayout.LabelField($"~{Palette.Cols}x{Palette.Rows} cells", GUILayout.Width(100));
                }

                if (hasPreview)
                {
                    StylesUtils.DrawInfoBox(
                        "Numbers on cells match the color rows. Hover a row to highlight its target cell.");
                    PaletteGUI.DrawLegend();

                    int unplaced = currentBuildResult != null ? currentBuildResult.UnplacedCount : 0;
                    if (unplaced > 0)
                        EditorGUILayout.HelpBox(
                            $"{unplaced} color(s) could not be added because the palette has no free cells. " +
                            "Those mesh faces will keep their original colors. Reduce Cell Size or use a palette with more free cells.",
                            MessageType.Warning);
                }

                Texture2D shown = hasPreview ? preview.PreviewTexture : session.WorkingTexture;
                List<SwatchMarker> markers = hasPreview ? preview.PreviewMarkers : null;

                PaletteGUI.DrawTextureFitWidth(
                    hasPreview ? "Preview" : "Original",
                    shown, markers, hoverColorIndex, TexW, TexH);
            }
        }

        private void DrawActionButtons()
        {
            bool anyChange = AnyChanged();

            using (new EditorGUILayout.VerticalScope(EditorStyles.helpBox))
            {
                StylesUtils.DrawInfoBox(
                    "Apply overwrites the source palette texture by writing each new color into one free transparent or white cell, " +
                    "or by reusing an existing matching cell. Existing colored cells are never overwritten, so other meshes that share " +
                    "the palette are not recolored unexpectedly. If the palette has no free cell for a color, that color is skipped and " +
                    "the related faces keep their old UVs. Existing materials are reused; .asset meshes are overwritten in place, while " +
                    "meshes imported from model files create a separate mesh asset.");

                using (new EditorGUI.DisabledScope(!anyChange))
                {
                    Color originalBg = GUI.backgroundColor;
                    GUI.backgroundColor = new Color(0.4f, 0.6f, 0.9f);
                    if (GUILayout.Button("Apply (overwrite texture/mesh, reuse material)", GUILayout.Height(34)))
                        Apply();
                    GUI.backgroundColor = originalBg;
                }
            }
        }

        private void ResetLoadedState()
        {
            if (!applied)
                preview.Revert(session.TargetRenderer);

            preview.Cleanup();
            session.Reset();
            currentBuildResult = null;
            applied = false;
            hoverColorIndex = -1;
            colorEditing = false;
        }

        private void LoadAndAnalyze()
        {
            preview.Revert(session.TargetRenderer);
            preview.Cleanup();
            currentBuildResult = null;

            if (!session.Load(targetRenderer, materialSlotIndex, colorTolerance, out string error))
            {
                EditorUtility.DisplayDialog(ToolName, error, "OK");
                return;
            }

            materialSlotIndex = session.MaterialSlotIndex;
            hoverColorIndex = -1;
            applied = false;
            Repaint();
        }

        private bool AnyChanged()
        {
            return PaletteColorChangeBuilder.HasChanges(UsedColors);
        }

        private void BuildPreview()
        {
            if (!session.IsLoaded || !AnyChanged())
            {
                RevertPreview();
                return;
            }

            currentBuildResult = PaletteColorChangeBuilder.Build(Palette, UsedColors, session.ColorTolerance);
            preview.BuildPreview(session, currentBuildResult);
            SceneView.RepaintAll();
            Repaint();
        }

        private void RevertPreview()
        {
            preview.Revert(session.TargetRenderer);
            preview.Cleanup();
            currentBuildResult = null;
            SceneView.RepaintAll();
            Repaint();
        }

        private void Apply()
        {
            if (!session.IsLoaded || !AnyChanged())
                return;

            if (session.SourceMesh.vertexCount == 0 || session.SourceMesh.subMeshCount == 0)
            {
                EditorUtility.DisplayDialog(ToolName,
                    "Mesh data could not be read (vertexCount=0). Enable Read/Write on the model and try again.", "OK");
                return;
            }

            try
            {
                var buildResult = PaletteColorChangeBuilder.Build(Palette, UsedColors, session.ColorTolerance);
                preview.Revert(session.TargetRenderer);

                MeshPaletteApplyResult applyResult =
                    MeshPaletteApplyService.Apply(session, buildResult, GeneratedFolder);

                preview.Cleanup();
                currentBuildResult = null;
                applied = true;

                string unplacedWarning = applyResult.UnplacedCount > 0
                    ? $" (WARNING: {applyResult.UnplacedCount} color(s) could not be added because no free cell was available; related faces kept their original colors)"
                    : "";

                Debug.Log($"<color=green>[{ToolName}]</color> Applied in place:\n - Texture overwritten: {applyResult.SourceTexturePath}\n - Mesh {applyResult.MeshAction}: {applyResult.WrittenMeshPath}\n - Material reused: {applyResult.SourceMaterialName}\nRenderer: {applyResult.TargetRendererName}.{unplacedWarning}");

                if (applyResult.PingTarget != null)
                    EditorGUIUtility.PingObject(applyResult.PingTarget);

                ShowNotification(new GUIContent("Applied in place"));
            }
            catch (System.Exception ex)
            {
                preview.Revert(session.TargetRenderer);
                Debug.LogError($"[{ToolName}] Apply failed: {ex}");
                EditorUtility.DisplayDialog(ToolName, "Apply failed:\n" + ex.Message, "OK");
            }
        }

        // ================================================================ Texture Recolor tab

        private void DrawTextureRecolorTab()
        {
            StylesUtils.DrawDescription(
                "Recolor a texture directly: detect its dominant colors, pick replacements, and rewrite the pixels " +
                "while preserving shading. The mesh and its UVs are not modified.");
            GUILayout.Space(6f);

            DrawRecolorTargetSection();

            if (recolorSession.IsLoaded)
            {
                EditorGUILayout.Space();
                using (new EditorGUILayout.HorizontalScope())
                {
                    using (new EditorGUILayout.VerticalScope(GUILayout.Width(LeftColumnWidth)))
                        DrawRecolorClustersSection();

                    GUILayout.Space(12f);

                    using (new EditorGUILayout.VerticalScope(GUILayout.ExpandWidth(true)))
                        DrawRecolorPreviewSection();
                }
            }
            else
            {
                EditorGUILayout.Space();
                StylesUtils.DrawInfoBox("Select a renderer whose material uses a texture, then click \"Load & Analyze\".");
            }
        }

        private void DrawRecolorTargetSection()
        {
            using (new EditorGUILayout.VerticalScope(EditorStyles.helpBox))
            {
                EditorGUILayout.LabelField("Target", EditorStyles.boldLabel);

                using (new EditorGUILayout.HorizontalScope())
                {
                    EditorGUI.BeginChangeCheck();
                    recolorRenderer = (Renderer)EditorGUILayout.ObjectField(
                        "Target Renderer", recolorRenderer, typeof(Renderer), true);
                    if (EditorGUI.EndChangeCheck())
                        ResetRecolorState();

                    if (GUILayout.Button("Use Selection", GUILayout.Width(130)))
                    {
                        var go = Selection.activeGameObject;
                        var r = go != null ? go.GetComponent<Renderer>() : null;
                        if (r == null)
                        {
                            EditorUtility.DisplayDialog(ToolName, "The selected GameObject does not have a Renderer.", "OK");
                        }
                        else
                        {
                            recolorRenderer = r;
                            ResetRecolorState();
                        }
                    }
                }

                if (recolorRenderer == null)
                    return;

                var mats = recolorRenderer.sharedMaterials;
                if (mats != null && mats.Length > 1)
                {
                    var names = new string[mats.Length];
                    for (int i = 0; i < mats.Length; i++)
                        names[i] = $"{i}: {(mats[i] != null ? mats[i].name : "<null>")}";

                    recolorSlotIndex = Mathf.Clamp(recolorSlotIndex, 0, mats.Length - 1);
                    recolorSlotIndex = EditorGUILayout.Popup("Material Slot", recolorSlotIndex, names);
                }

                recolorMaxClusters = EditorGUILayout.IntSlider(
                    new GUIContent("Max Colors", "Maximum number of dominant color groups to detect."),
                    recolorMaxClusters, 4, 12);

                Color originalBg = GUI.backgroundColor;
                GUI.backgroundColor = new Color(0.4f, 0.8f, 1f);
                if (GUILayout.Button("Load & Analyze", GUILayout.Height(26)))
                    LoadAndAnalyzeRecolor();
                GUI.backgroundColor = originalBg;

                if (recolorSession.IsLoaded)
                {
                    EditorGUILayout.LabelField(
                        $"Texture: {Path.GetFileName(recolorSession.SourceTexturePath)}  ({recolorSession.FullWidth}x{recolorSession.FullHeight})",
                        EditorStyles.miniLabel);

                    if (recolorSession.SourceIsLinear)
                        EditorGUILayout.HelpBox(
                            "This texture is imported as linear data (sRGB off), e.g. a mask or normal map. " +
                            "The recolor math assumes color data, so the result may be incorrect.",
                            MessageType.Warning);
                }
            }
        }

        private void DrawRecolorClustersSection()
        {
            using (new EditorGUILayout.VerticalScope(EditorStyles.helpBox))
            {
                var clusters = recolorSession.Clusters;
                EditorGUILayout.LabelField($"Texture uses {clusters.Count} dominant colors", EditorStyles.boldLabel);

                if (clusters.Count == 0)
                {
                    EditorGUILayout.HelpBox(
                        "No dominant colors were detected. The texture may be fully transparent.",
                        MessageType.Warning);
                    return;
                }

                EditorGUILayout.LabelField(
                    "Pick a new color for any group. Pixels similar to the original color are recolored with their shading preserved; other pixels stay untouched.",
                    EditorStyles.wordWrappedMiniLabel);

                for (int i = 0; i < clusters.Count; i++)
                {
                    var cluster = clusters[i];
                    using (new EditorGUILayout.HorizontalScope())
                    {
                        using (new EditorGUILayout.VerticalScope())
                        {
                            using (new EditorGUILayout.HorizontalScope())
                            {
                                Rect sw = GUILayoutUtility.GetRect(22, 18, GUILayout.Width(22));
                                EditorGUI.DrawRect(sw, (Color)cluster.color);

                                EditorGUI.BeginChangeCheck();
                                cluster.newColor = EditorGUILayout.ColorField(cluster.newColor, GUILayout.ExpandWidth(true));
                                if (EditorGUI.EndChangeCheck())
                                    recolorPreviewDirty = true;
                            }

                            using (new EditorGUILayout.HorizontalScope())
                            {
                                EditorGUILayout.LabelField($"{cluster.coverage:P1} of pixels", EditorStyles.miniLabel, GUILayout.ExpandWidth(true));

                                using (new EditorGUI.DisabledScope(!cluster.Changed))
                                {
                                    if (GUILayout.Button("Reset", GUILayout.Width(50)))
                                    {
                                        cluster.newColor = (Color)cluster.color;
                                        recolorPreviewDirty = true;
                                    }
                                }
                            }
                        }
                    }
                }

                using (new EditorGUI.DisabledScope(!recolorSession.AnyChanged))
                {
                    if (GUILayout.Button("Reset All", GUILayout.Height(20)))
                    {
                        recolorSession.ResetClusterEdits();
                        recolorPreviewDirty = true;
                    }
                }

                GUILayout.Space(4f);
                recolorAdvancedFoldout = EditorGUILayout.Foldout(recolorAdvancedFoldout, "Advanced Mask Settings", true);
                if (recolorAdvancedFoldout)
                {
                    EditorGUI.indentLevel++;
                    EditorGUI.BeginChangeCheck();
                    recolorMask.hueToleranceDeg = EditorGUILayout.Slider(
                        new GUIContent("Hue Tolerance (deg)", "How far a pixel's hue may drift from the group color and still be recolored."),
                        recolorMask.hueToleranceDeg, 1f, 180f);
                    recolorMask.satTolerance = EditorGUILayout.Slider(
                        new GUIContent("Saturation Tolerance"), recolorMask.satTolerance, 0.01f, 1f);
                    recolorMask.valTolerance = EditorGUILayout.Slider(
                        new GUIContent("Value Tolerance"), recolorMask.valTolerance, 0.01f, 1f);
                    recolorMask.softness = EditorGUILayout.Slider(
                        new GUIContent("Edge Softness", "Soft falloff at the mask border to avoid hard seams."),
                        recolorMask.softness, 0.01f, 1f);
                    recolorMask.shadingClamp = EditorGUILayout.Slider(
                        new GUIContent("Shading Clamp", "Limits how much brighter than the group color a recolored pixel can get."),
                        recolorMask.shadingClamp, 1f, 4f);
                    if (EditorGUI.EndChangeCheck())
                        recolorPreviewDirty = true;

                    if (GUILayout.Button("Reset Mask Settings", GUILayout.Height(20)))
                    {
                        recolorMask = RecolorMaskSettings.Default;
                        recolorPreviewDirty = true;
                    }
                    EditorGUI.indentLevel--;
                }
            }
        }

        private void DrawRecolorPreviewSection()
        {
            using (new EditorGUILayout.VerticalScope(EditorStyles.helpBox))
            {
                bool anyChange = recolorSession.AnyChanged;

                EditorGUILayout.LabelField(
                    anyChange ? "Texture (Live Preview)" : "Texture",
                    EditorStyles.boldLabel);

                using (new EditorGUILayout.HorizontalScope())
                {
                    EditorGUI.BeginChangeCheck();
                    recolorPreviewMode = (RecolorPreviewMode)GUILayout.Toolbar(
                        (int)recolorPreviewMode, RecolorPreviewLabels, GUILayout.Height(20f));
                    if (EditorGUI.EndChangeCheck())
                        recolorPreviewDirty = true;

                    GUILayout.Space(8f);

                    EditorGUI.BeginChangeCheck();
                    recolorScenePreview = GUILayout.Toggle(recolorScenePreview,
                        new GUIContent("Preview on Renderer", "Temporarily shows the recolored texture on the target renderer in the Scene view."),
                        GUILayout.Width(150f));
                    if (EditorGUI.EndChangeCheck())
                    {
                        if (!recolorScenePreview)
                            recolorPreview.RevertScenePreview(recolorSession.TargetRenderer);
                        recolorPreviewDirty = true;
                    }
                }

                if (recolorPreviewMode == RecolorPreviewMode.Mask)
                    StylesUtils.DrawInfoBox("White = pixels that will be recolored, black = pixels kept as-is.");

                UpdateRecolorPreviewIfDirty();

                PaletteGUI.DrawTextureFitWidth(
                    RecolorPreviewLabels[(int)recolorPreviewMode],
                    recolorPreview.PreviewTexture, null, -1,
                    recolorSession.PreviewWidth, recolorSession.PreviewHeight);

                EditorGUILayout.LabelField(
                    "The window preview is downscaled; Apply always processes the full resolution texture.",
                    EditorStyles.wordWrappedMiniLabel);
            }
        }

        private void UpdateRecolorPreviewIfDirty()
        {
            bool needsInitial = recolorPreview.PreviewTexture == null;
            if ((!recolorPreviewDirty && !needsInitial) || Event.current.type != EventType.Repaint)
                return;

            recolorPreview.UpdatePreview(recolorSession, recolorMask, recolorPreviewMode);

            if (recolorSession.AnyChanged && recolorScenePreview)
                recolorPreview.ApplyScenePreview(recolorSession);
            else if (recolorPreview.IsScenePreviewActive)
                recolorPreview.RevertScenePreview(recolorSession.TargetRenderer);

            recolorPreviewDirty = false;
            SceneView.RepaintAll();
        }

        private void DrawRecolorActionButtons()
        {
            bool anyChange = recolorSession.AnyChanged;

            using (new EditorGUILayout.VerticalScope(EditorStyles.helpBox))
            {
                StylesUtils.DrawInfoBox(
                    "Apply rewrites the source texture file in place at full resolution. Every mesh and material that shares " +
                    "this texture is affected, and the file change cannot be undone. Non-PNG sources (.tga/.jpg/...) are exported " +
                    "as a new \"_recolored.png\" next to the original and assigned to the material instead.");

                using (new EditorGUI.DisabledScope(!anyChange))
                {
                    Color originalBg = GUI.backgroundColor;
                    GUI.backgroundColor = new Color(0.4f, 0.6f, 0.9f);
                    if (GUILayout.Button("Apply (overwrite texture in place)", GUILayout.Height(34)))
                        ApplyRecolor();
                    GUI.backgroundColor = originalBg;
                }
            }
        }

        private void ResetRecolorState()
        {
            if (!recolorApplied)
                recolorPreview.RevertScenePreview(recolorSession.TargetRenderer);

            recolorPreview.Cleanup();
            recolorSession.Reset();
            recolorApplied = false;
            recolorPreviewDirty = true;
        }

        private void LoadAndAnalyzeRecolor()
        {
            recolorPreview.RevertScenePreview(recolorSession.TargetRenderer);
            recolorPreview.Cleanup();

            if (!recolorSession.Load(recolorRenderer, recolorSlotIndex, recolorMaxClusters, out string error))
            {
                EditorUtility.DisplayDialog(ToolName, error, "OK");
                return;
            }

            recolorSlotIndex = recolorSession.MaterialSlotIndex;
            recolorApplied = false;
            recolorPreviewDirty = true;
            Repaint();
        }

        private void ApplyRecolor()
        {
            if (!recolorSession.IsLoaded || !recolorSession.AnyChanged)
                return;

            string fileName = Path.GetFileName(recolorSession.SourceTexturePath);
            bool isPng = string.Equals(Path.GetExtension(recolorSession.SourceTexturePath), ".png",
                System.StringComparison.OrdinalIgnoreCase);

            string message = isPng
                ? $"Overwrite \"{fileName}\" in place?\n\nEvery mesh/material that shares this texture is affected. This cannot be undone."
                : $"\"{fileName}\" is not a PNG. A new \"{Path.GetFileNameWithoutExtension(fileName)}_recolored.png\" will be exported next to it and assigned to the material.\n\nContinue?";

            if (!EditorUtility.DisplayDialog(ToolName, message, isPng ? "Overwrite" : "Export", "Cancel"))
                return;

            try
            {
                recolorPreview.RevertScenePreview(recolorSession.TargetRenderer);

                TextureRecolorApplyResult result = TextureRecolorApplyService.Apply(recolorSession, recolorMask);

                recolorApplied = true;
                recolorPreviewDirty = true;

                string action = result.ConvertedToPng ? "exported (source was not PNG)" : "overwritten in place";
                Debug.Log($"<color=green>[{ToolName}]</color> Texture recolored:\n - {action}: {result.WrittenPath}\nRenderer: {recolorSession.TargetRenderer.name}.");

                if (result.PingTarget != null)
                    EditorGUIUtility.PingObject(result.PingTarget);

                ShowNotification(new GUIContent(result.ConvertedToPng ? "Exported PNG" : "Texture overwritten"));

                // reload so the cached pixels/clusters match the rewritten texture,
                // otherwise a second Apply would recolor on top of the first one
                recolorSession.Load(recolorRenderer, recolorSlotIndex, recolorMaxClusters, out _);
            }
            catch (System.Exception ex)
            {
                recolorPreview.RevertScenePreview(recolorSession.TargetRenderer);
                Debug.LogError($"[{ToolName}] Recolor apply failed: {ex}");
                EditorUtility.DisplayDialog(ToolName, "Apply failed:\n" + ex.Message, "OK");
            }
        }
    }
}
#endif
