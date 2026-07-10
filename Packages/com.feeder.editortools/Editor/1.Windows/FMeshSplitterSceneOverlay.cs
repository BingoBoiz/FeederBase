#if UNITY_EDITOR
using System.Collections.Generic;
using UnityEditor;
using UnityEngine;

namespace Feeder
{
    /// <summary>
    /// Floating Scene View panel for the Mesh Splitter tab (FMeshModifierWindow).
    /// Simple: hover/click UV-island parts. Manual: pick/brush vertices. Advanced: position
    /// a cutting volume via a hidden helper GameObject and Unity's native transform gizmos.
    /// Shares the session/settings objects with the window by reference.
    /// </summary>
    public static class FMeshSplitterSceneOverlay
    {
        private const int WindowId = 999998; // 999999 is owned by FAlignMeshSceneOverlay
        private const float WindowWidth = 300f;
        private const float HeaderHeight = 24f;
        private const float CloseButtonSize = 18f;
        private const string VolumeObjectName = "__Feeder_MeshSplit_Volume";

        private static bool _showWindow;
        private static Rect _windowRect = new Rect(0, 0, WindowWidth, 100f);
        private static bool _initializedPosition;
        private static Rect _headerRect;

        private static FMeshSplitSession _session;
        private static FMeshSplitSettings _settings;
        private static System.Action _onChanged;
        private static System.Action _onSplitSelected;
        private static System.Action _onSplitAllParts;

        private static bool _pickingEnabled = true;
        private static bool _showAllParts = true;
        private static int _hoveredPartId = -1;
        private static Vector3 _lastHoverWorldPoint;
        private static bool _hasHoverPoint;
        private static bool _selectLinkedArmed;
        private static bool _brushStrokeActive;
        private static FMeshSplitMode _lastMode;
        private static GameObject _volumeObject;
        private static bool _syncingVolumeObject;
        private static int _selectedTriangleCache = -1;
        private static readonly List<int> BrushBuffer = new List<int>();

        private static GUIStyle _headerStyle;
        private static GUIStyle _bodyStyle;
        private static GUIStyle _statStyle;

        public static bool IsOpen => _showWindow;

        [InitializeOnLoadMethod]
        private static void Init()
        {
            SceneView.duringSceneGui += OnSceneGUI;
            AssemblyReloadEvents.afterAssemblyReload += OnAfterAssemblyReload;
        }

        public static void Open(FMeshSplitSession session, FMeshSplitSettings settings,
            System.Action onChanged, System.Action onSplitSelected, System.Action onSplitAllParts)
        {
            if (session == null || settings == null || !session.IsAnalyzed)
                return;

            _session = session;
            _settings = settings;
            _onChanged = onChanged;
            _onSplitSelected = onSplitSelected;
            _onSplitAllParts = onSplitAllParts;
            _showWindow = true;
            _hoveredPartId = -1;
            _selectLinkedArmed = false;
            _lastMode = settings.mode;
            _selectedTriangleCache = -1;

            // Always rebuild: part ids may have changed via Remerge while the overlay was closed.
            FMeshSplitOverlayDrawer.BuildPartsMesh(session.Analysis);
            RefreshSelectionVisuals();
            SyncVolumeObjectForMode();

            if (session.Target != null)
            {
                Selection.activeGameObject = session.Target;
                SceneView.lastActiveSceneView?.FrameSelected();
            }
            SceneView.RepaintAll();
        }

        public static void CloseOverlay()
        {
            _showWindow = false;
            _selectLinkedArmed = false;
            DestroyVolumeObject();
            SceneView.RepaintAll();
        }

        /// <summary>Called by the window when it mutates the selection (e.g. part checkboxes).</summary>
        public static void NotifySelectionChanged()
        {
            if (!_showWindow)
                return;
            RefreshSelectionVisuals();
            SceneView.RepaintAll();
        }

        /// <summary>Called by the window after a new Analyze while the overlay is open.</summary>
        public static void NotifyAnalysisChanged()
        {
            if (!_showWindow)
                return;
            if (_session == null || !_session.IsAnalyzed)
            {
                CloseOverlay();
                return;
            }
            FMeshSplitOverlayDrawer.BuildPartsMesh(_session.Analysis);
            _hoveredPartId = -1;
            RefreshSelectionVisuals();
            SceneView.RepaintAll();
        }

        private static void OnAfterAssemblyReload()
        {
            _showWindow = false;
            DestroyVolumeObject();
            FMeshSplitOverlayDrawer.Clear();
        }

        // ================================================================ scene gui

        private static void OnSceneGUI(SceneView sceneView)
        {
            if (!_showWindow)
                return;

            if (_session == null || _session.Target == null || !_session.IsAnalyzed ||
                _session.Analysis.IsStale)
            {
                CloseOverlay();
                return;
            }

            HandleModeSwitch();
            SyncPrimitiveFromVolumeObjectIfNeeded();

            Event evt = Event.current;
            bool mouseOverPanel = _windowRect.Contains(evt.mousePosition);

            if (_pickingEnabled && !mouseOverPanel && _settings.mode != FMeshSplitMode.Advanced)
                HandlePickingInput(evt);

            if (evt.type == EventType.Repaint)
                DrawOverlays();

            InitStyles();
            if (!_initializedPosition)
            {
                var viewRect = sceneView.position;
                _windowRect.x = viewRect.width - _windowRect.width - 10f;
                _windowRect.y = viewRect.height - 360f;
                _initializedPosition = true;
            }

            Handles.BeginGUI();
            _windowRect = GUILayout.Window(WindowId, _windowRect, DrawWindow, "", GUIStyle.none);
            Handles.EndGUI();
        }

        private static void HandleModeSwitch()
        {
            if (_settings.mode == _lastMode)
                return;
            _lastMode = _settings.mode;
            _hoveredPartId = -1;
            _selectLinkedArmed = false;
            SyncVolumeObjectForMode();
            RefreshSelectionVisuals();
        }

        private static void HandlePickingInput(Event evt)
        {
            // Keep Unity from box-selecting / picking scene objects while our tool is active.
            int controlId = GUIUtility.GetControlID(FocusType.Passive);
            if (evt.type == EventType.Layout)
            {
                HandleUtility.AddDefaultControl(controlId);
                return;
            }

            if (evt.alt)
                return; // camera navigation

            switch (_settings.mode)
            {
                case FMeshSplitMode.Simple:
                    HandleSimpleInput(evt);
                    break;
                case FMeshSplitMode.Manual:
                    HandleManualInput(evt);
                    break;
            }
        }

        private static void HandleSimpleInput(Event evt)
        {
            if (evt.type == EventType.MouseMove)
            {
                int hovered = -1;
                Ray ray = HandleUtility.GUIPointToWorldRay(evt.mousePosition);
                if (FMeshSplitPicking.RaycastTriangle(_session.Analysis, _session.Target.transform, ray,
                        out int tri, out Vector3 hitPoint))
                {
                    hovered = _session.Analysis.TrianglePartId[tri];
                    _lastHoverWorldPoint = hitPoint;
                    _hasHoverPoint = true;
                }
                else
                {
                    _hasHoverPoint = false;
                }

                if (hovered != _hoveredPartId)
                {
                    _hoveredPartId = hovered;
                    SceneView.RepaintAll();
                }
            }
            else if (evt.type == EventType.MouseDown && evt.button == 0)
            {
                Ray ray = HandleUtility.GUIPointToWorldRay(evt.mousePosition);
                if (FMeshSplitPicking.RaycastTriangle(_session.Analysis, _session.Target.transform, ray,
                        out int tri, out _))
                {
                    int part = _session.Analysis.TrianglePartId[tri];
                    if (!_session.SelectedPartIds.Add(part))
                        _session.SelectedPartIds.Remove(part);
                    OnSelectionMutated();
                }
                evt.Use();
            }
        }

        private static void HandleManualInput(Event evt)
        {
            FMeshSplitAnalysis a = _session.Analysis;
            Transform targetT = _session.Target.transform;

            if (evt.type == EventType.MouseMove)
            {
                Ray ray = HandleUtility.GUIPointToWorldRay(evt.mousePosition);
                _hasHoverPoint = FMeshSplitPicking.RaycastTriangle(a, targetT, ray, out _, out _lastHoverWorldPoint);
                SceneView.RepaintAll();
            }
            else if (evt.type == EventType.MouseDown && evt.button == 0)
            {
                if (_selectLinkedArmed)
                {
                    int picked = FMeshSplitPicking.FindNearestCanonicalVertex(a, targetT,
                        evt.mousePosition, _settings.pickRadiusPixels * 4f);
                    if (picked >= 0)
                    {
                        int root = a.SpatialIslandOfCanonical[picked];
                        for (int c = 0; c < a.CanonicalCount; c++)
                            if (a.SpatialIslandOfCanonical[c] == root)
                                _session.SelectedCanonicalVerts.Add(c);
                        OnSelectionMutated();
                    }
                    _selectLinkedArmed = false;
                }
                else
                {
                    int picked = FMeshSplitPicking.FindNearestCanonicalVertex(a, targetT,
                        evt.mousePosition, _settings.pickRadiusPixels);
                    if (picked >= 0)
                    {
                        if (evt.shift)
                            _session.SelectedCanonicalVerts.Add(picked);
                        else if (evt.control)
                            _session.SelectedCanonicalVerts.Remove(picked);
                        else if (!_session.SelectedCanonicalVerts.Add(picked))
                            _session.SelectedCanonicalVerts.Remove(picked);
                        OnSelectionMutated();
                    }
                    _brushStrokeActive = true;
                }
                evt.Use();
            }
            else if (evt.type == EventType.MouseDrag && evt.button == 0 && _brushStrokeActive)
            {
                FMeshSplitPicking.CollectCanonicalVerticesInGuiRadius(a, targetT,
                    evt.mousePosition, _settings.brushRadiusPixels, BrushBuffer);
                if (BrushBuffer.Count > 0)
                {
                    bool changed = false;
                    foreach (int c in BrushBuffer)
                        changed |= evt.control
                            ? _session.SelectedCanonicalVerts.Remove(c)
                            : _session.SelectedCanonicalVerts.Add(c);
                    if (changed)
                        OnSelectionMutated();
                }

                Ray ray = HandleUtility.GUIPointToWorldRay(evt.mousePosition);
                _hasHoverPoint = FMeshSplitPicking.RaycastTriangle(a, targetT, ray, out _, out _lastHoverWorldPoint);
                evt.Use();
            }
            else if (evt.type == EventType.MouseUp && evt.button == 0)
            {
                _brushStrokeActive = false;
            }
        }

        // ================================================================ drawing

        private static void DrawOverlays()
        {
            Matrix4x4 localToWorld = _session.Target.transform.localToWorldMatrix;

            if (_settings.mode == FMeshSplitMode.Simple)
                FMeshSplitOverlayDrawer.DrawParts(localToWorld, _hoveredPartId, _showAllParts);

            FMeshSplitOverlayDrawer.DrawSelection(localToWorld);

            if (_settings.mode == FMeshSplitMode.Manual)
            {
                FMeshSplitOverlayDrawer.DrawVertexDots();
                if (_hasHoverPoint && _pickingEnabled)
                    DrawBrushCircle();
            }
            else if (_settings.mode == FMeshSplitMode.Advanced)
            {
                DrawPrimitiveWireframe();
            }
        }

        private static void DrawBrushCircle()
        {
            Camera cam = SceneView.lastActiveSceneView != null ? SceneView.lastActiveSceneView.camera : null;
            if (cam == null)
                return;

            // Convert the pixel brush radius to world size at the hover depth.
            float worldPerPixel = HandleUtility.GetHandleSize(_lastHoverWorldPoint) / 80f;
            float radius = _settings.brushRadiusPixels * worldPerPixel;

            Color old = Handles.color;
            Handles.color = new Color(1f, 0.8f, 0.2f, 0.9f);
            Handles.DrawWireDisc(_lastHoverWorldPoint, -cam.transform.forward, radius);
            Handles.color = old;
        }

        private static void DrawPrimitiveWireframe()
        {
            FSplitPrimitive prim = _session.Primitive;
            Color old = Handles.color;
            Handles.color = new Color(0.3f, 0.9f, 1f, 0.9f);
            Matrix4x4 oldMatrix = Handles.matrix;

            switch (prim.Kind)
            {
                case FSplitPrimitiveKind.Box:
                    Handles.matrix = Matrix4x4.TRS(prim.Position, prim.Rotation, prim.Scale);
                    Handles.DrawWireCube(Vector3.zero, Vector3.one);
                    break;

                case FSplitPrimitiveKind.Sphere:
                {
                    float r = prim.SphereRadius;
                    Vector3 x = prim.Rotation * Vector3.right;
                    Vector3 y = prim.Rotation * Vector3.up;
                    Vector3 z = prim.Rotation * Vector3.forward;
                    Handles.DrawWireDisc(prim.Position, x, r);
                    Handles.DrawWireDisc(prim.Position, y, r);
                    Handles.DrawWireDisc(prim.Position, z, r);
                    break;
                }

                case FSplitPrimitiveKind.Plane:
                {
                    Bounds bounds = GetTargetWorldBounds();
                    float size = Mathf.Max(bounds.size.magnitude, 1f);
                    Vector3 up = prim.Rotation * Vector3.up;
                    Vector3 right = prim.Rotation * Vector3.right * (size * 0.6f);
                    Vector3 forward = prim.Rotation * Vector3.forward * (size * 0.6f);
                    Vector3 p = prim.Position;
                    var corners = new[]
                    {
                        p - right - forward, p + right - forward,
                        p + right + forward, p - right + forward, p - right - forward
                    };
                    Handles.DrawPolyLine(corners);
                    Handles.DrawLine(p - right - forward, p + right + forward);
                    Handles.DrawLine(p + right - forward, p - right + forward);
                    Handles.ArrowHandleCap(0, p, Quaternion.LookRotation(up), size * 0.25f, EventType.Repaint);
                    break;
                }

                case FSplitPrimitiveKind.Circle:
                {
                    float r = prim.CircleRadius;
                    Vector3 axis = prim.Rotation * Vector3.up;
                    float half = Mathf.Max(Mathf.Abs(prim.Scale.y) * 0.5f, 0.25f);
                    Handles.DrawWireDisc(prim.Position, axis, r);
                    Handles.DrawWireDisc(prim.Position + axis * half, axis, r);
                    Handles.DrawWireDisc(prim.Position - axis * half, axis, r);
                    Handles.DrawLine(prim.Position - axis * half * 1.5f, prim.Position + axis * half * 1.5f);
                    break;
                }
            }

            Handles.matrix = oldMatrix;
            Handles.color = old;
        }

        // ================================================================ panel

        private static void InitStyles()
        {
            if (_headerStyle != null)
                return;
            _headerStyle = new GUIStyle(EditorStyles.boldLabel)
            {
                fontSize = 13,
                alignment = TextAnchor.MiddleCenter
            };
            _bodyStyle = new GUIStyle(EditorStyles.helpBox) { padding = new RectOffset(8, 8, 8, 8) };
            _statStyle = new GUIStyle(EditorStyles.wordWrappedMiniLabel) { padding = new RectOffset(4, 4, 2, 2) };
        }

        private static void DrawWindow(int id)
        {
            GUI.Box(new Rect(0, 0, _windowRect.width, _windowRect.height), GUIContent.none);

            _headerRect = new Rect(0, 0, _windowRect.width, HeaderHeight);
            EditorGUI.DrawRect(_headerRect, new Color(0.22f, 0.22f, 0.22f));
            GUI.Label(_headerRect, "Mesh Splitter", _headerStyle);
            if (GUI.Button(new Rect(_windowRect.width - 22f, 3f, CloseButtonSize, CloseButtonSize), "X"))
            {
                CloseOverlay();
                return;
            }

            GUILayout.Space(26f);
            GUILayout.BeginVertical(_bodyStyle);

            GUILayout.Label(_session.Target != null ? _session.Target.name : "(mất target)", EditorStyles.miniBoldLabel);

            EditorGUI.BeginChangeCheck();
            int newMode = GUILayout.Toolbar((int)_settings.mode,
                new[] { "Đơn giản", "Thủ công", "Khối" }, GUILayout.Height(20f));
            if (EditorGUI.EndChangeCheck() && newMode != (int)_settings.mode)
            {
                _settings.mode = (FMeshSplitMode)newMode;
                _onChanged?.Invoke();
            }

            GUILayout.Space(4f);
            if (_settings.mode != FMeshSplitMode.Advanced)
                _pickingEnabled = GUILayout.Toggle(_pickingEnabled,
                    new GUIContent(" Picking (chọn bằng chuột)",
                        "Bật: click/kéo chuột trong Scene View để chọn. Tắt: thao tác scene bình thường."));

            switch (_settings.mode)
            {
                case FMeshSplitMode.Simple: DrawSimplePanel(); break;
                case FMeshSplitMode.Manual: DrawManualPanel(); break;
                case FMeshSplitMode.Advanced: DrawAdvancedPanel(); break;
            }

            GUILayout.Space(6f);
            DrawActionButtons();
            GUILayout.EndVertical();
            GUI.DragWindow(_headerRect);
        }

        private static void DrawSimplePanel()
        {
            _showAllParts = GUILayout.Toggle(_showAllParts, " Hiện tất cả part (màu)");

            int partCount = _session.Analysis.Parts.Count;
            GUILayout.Label($"{partCount} part — {_session.SelectedPartIds.Count} đã chọn " +
                            $"({GetSelectedTriangleCount()} tam giác)", _statStyle);

            GUILayout.BeginHorizontal();
            if (GUILayout.Button("Chọn hết"))
            {
                foreach (FMeshPartInfo part in _session.Analysis.Parts)
                    _session.SelectedPartIds.Add(part.PartId);
                OnSelectionMutated();
            }
            if (GUILayout.Button("Bỏ chọn"))
            {
                _session.SelectedPartIds.Clear();
                OnSelectionMutated();
            }
            GUILayout.EndHorizontal();

            GUILayout.Label("Hover để xem part, click để chọn/bỏ chọn.", _statStyle);
        }

        private static void DrawManualPanel()
        {
            GUILayout.Label($"{_session.SelectedCanonicalVerts.Count} đỉnh đã chọn " +
                            $"({GetSelectedTriangleCount()} tam giác)", _statStyle);

            GUILayout.BeginHorizontal();
            GUILayout.Label("Brush", GUILayout.Width(40f));
            _settings.brushRadiusPixels = GUILayout.HorizontalSlider(_settings.brushRadiusPixels, 5f, 120f);
            GUILayout.Label(((int)_settings.brushRadiusPixels).ToString(), GUILayout.Width(28f));
            GUILayout.EndHorizontal();

            GUILayout.BeginHorizontal();
            if (GUILayout.Button("Grow"))
            {
                GrowSelection();
                OnSelectionMutated();
            }
            Color oldBg = GUI.backgroundColor;
            if (_selectLinkedArmed)
                GUI.backgroundColor = new Color(1f, 0.7f, 0.2f);
            if (GUILayout.Button(_selectLinkedArmed ? "Click vào mesh..." : "Select Linked"))
                _selectLinkedArmed = !_selectLinkedArmed;
            GUI.backgroundColor = oldBg;
            GUILayout.EndHorizontal();

            GUILayout.BeginHorizontal();
            if (GUILayout.Button("Invert"))
            {
                InvertSelection();
                OnSelectionMutated();
            }
            if (GUILayout.Button("Clear"))
            {
                _session.SelectedCanonicalVerts.Clear();
                OnSelectionMutated();
            }
            GUILayout.EndHorizontal();

            GUILayout.Label("Click = chọn đỉnh (Shift thêm, Ctrl bỏ). Kéo = brush. " +
                            "Tam giác được tách khi cả 3 đỉnh được chọn.", _statStyle);
        }

        private static void DrawAdvancedPanel()
        {
            EditorGUI.BeginChangeCheck();
            var newKind = (FSplitPrimitiveKind)EditorGUILayout.EnumPopup("Khối cắt", _settings.primitiveKind);
            if (EditorGUI.EndChangeCheck())
            {
                _settings.primitiveKind = newKind;
                _session.Primitive.Kind = newKind;
                RefreshSelectionVisuals();
                _onChanged?.Invoke();
            }

            GUILayout.Label($"{GetSelectedTriangleCount()} tam giác bên trong khối", _statStyle);

            GUILayout.BeginHorizontal();
            if (GUILayout.Button(_volumeObject == null ? "Hiện khối cắt" : "Chọn khối cắt"))
            {
                SyncVolumeObjectForMode();
                if (_volumeObject != null)
                    Selection.activeGameObject = _volumeObject;
            }
            if (GUILayout.Button("Reset Volume"))
            {
                ResetPrimitiveToTargetBounds();
                RefreshSelectionVisuals();
            }
            GUILayout.EndHorizontal();

            GUILayout.Label("Di chuyển/xoay/scale khối bằng gizmo của Unity. " +
                            "Plane tách theo mặt (mũi tên = phía A); Circle là trụ quanh trục Y.", _statStyle);
        }

        private static void DrawActionButtons()
        {
            Color oldBg = GUI.backgroundColor;

            using (new EditorGUI.DisabledScope(GetSelectedTriangleCount() == 0))
            {
                GUI.backgroundColor = new Color(0.4f, 1f, 0.5f);
                if (GUILayout.Button("Tách phần đã chọn", GUILayout.Height(24f)))
                    _onSplitSelected?.Invoke();
            }

            if (_settings.mode == FMeshSplitMode.Simple)
            {
                GUI.backgroundColor = new Color(0.55f, 0.75f, 1f);
                if (GUILayout.Button("Tách tất cả part", GUILayout.Height(22f)))
                    _onSplitAllParts?.Invoke();
            }

            GUI.backgroundColor = oldBg;
        }

        // ================================================================ selection helpers

        private static void OnSelectionMutated()
        {
            _selectedTriangleCache = -1;
            RefreshSelectionVisuals();
            _onChanged?.Invoke();
            SceneView.RepaintAll();
        }

        private static void RefreshSelectionVisuals()
        {
            if (_session == null || !_session.IsAnalyzed)
                return;

            _selectedTriangleCache = -1;
            bool[] mask = _session.GetSelectedTriangleMask(_settings);
            FMeshSplitOverlayDrawer.BuildSelectionMesh(_session.Analysis, mask);

            if (_settings.mode == FMeshSplitMode.Manual && _session.Target != null)
                FMeshSplitOverlayDrawer.BuildVertexDots(_session.Analysis, _session.Target.transform,
                    _session.SelectedCanonicalVerts);
        }

        private static int GetSelectedTriangleCount()
        {
            if (_selectedTriangleCache < 0)
                _selectedTriangleCache = _session.CountSelectedTriangles(_settings);
            return _selectedTriangleCache;
        }

        private static void GrowSelection()
        {
            FMeshSplitAnalysis a = _session.Analysis;
            FMeshSplitAnalyzer.EnsureAdjacency(a);

            var toAdd = new List<int>();
            foreach (int c in _session.SelectedCanonicalVerts)
                foreach (int n in a.CanonicalNeighbors[c])
                    if (!_session.SelectedCanonicalVerts.Contains(n))
                        toAdd.Add(n);
            foreach (int c in toAdd)
                _session.SelectedCanonicalVerts.Add(c);
        }

        private static void InvertSelection()
        {
            FMeshSplitAnalysis a = _session.Analysis;
            var inverted = new HashSet<int>();
            for (int c = 0; c < a.CanonicalCount; c++)
                if (!_session.SelectedCanonicalVerts.Contains(c))
                    inverted.Add(c);
            _session.SelectedCanonicalVerts.Clear();
            _session.SelectedCanonicalVerts.UnionWith(inverted);
        }

        // ================================================================ advanced volume object

        // The volume pose is edited through a hidden helper GameObject so the user gets
        // Unity's native Move/Rotate/Scale gizmos (same trick as FAlignMeshSceneOverlay).
        private static void SyncVolumeObjectForMode()
        {
            if (_settings == null)
                return;

            if (_settings.mode != FMeshSplitMode.Advanced)
            {
                DestroyVolumeObject();
                return;
            }

            _session.Primitive.Kind = _settings.primitiveKind;
            if (_volumeObject == null)
            {
                if (_session.Primitive.Scale == Vector3.one && _session.Primitive.Position == Vector3.zero)
                    ResetPrimitiveToTargetBounds();

                _volumeObject = new GameObject(VolumeObjectName) { hideFlags = HideFlags.DontSave };
                SyncVolumeObjectFromPrimitive();
                Selection.activeGameObject = _volumeObject;
            }
        }

        private static void ResetPrimitiveToTargetBounds()
        {
            Bounds bounds = GetTargetWorldBounds();
            FSplitPrimitive prim = _session.Primitive;
            prim.Position = bounds.center;
            prim.Rotation = Quaternion.identity;
            prim.Scale = prim.Kind == FSplitPrimitiveKind.Box
                ? bounds.size
                : Vector3.one * Mathf.Max(bounds.extents.magnitude, 0.1f);
            SyncVolumeObjectFromPrimitive();
        }

        private static Bounds GetTargetWorldBounds()
        {
            Renderer renderer = _session.Target != null ? _session.Target.GetComponent<Renderer>() : null;
            if (renderer != null)
                return renderer.bounds;
            return new Bounds(_session.Target != null ? _session.Target.transform.position : Vector3.zero, Vector3.one);
        }

        private static void SyncVolumeObjectFromPrimitive()
        {
            if (_volumeObject == null)
                return;
            _syncingVolumeObject = true;
            Transform t = _volumeObject.transform;
            t.SetPositionAndRotation(_session.Primitive.Position, _session.Primitive.Rotation);
            t.localScale = _session.Primitive.Scale;
            t.hasChanged = false;
            _syncingVolumeObject = false;
        }

        private static void SyncPrimitiveFromVolumeObjectIfNeeded()
        {
            if (_volumeObject == null || _syncingVolumeObject || _settings.mode != FMeshSplitMode.Advanced)
                return;

            Transform t = _volumeObject.transform;
            if (!t.hasChanged)
                return;

            _session.Primitive.Position = t.position;
            _session.Primitive.Rotation = t.rotation;
            _session.Primitive.Scale = t.lossyScale;
            t.hasChanged = false;
            RefreshSelectionVisuals();
            _onChanged?.Invoke();
        }

        private static void DestroyVolumeObject()
        {
            if (_volumeObject == null)
                return;
            Object.DestroyImmediate(_volumeObject);
            _volumeObject = null;
        }
    }
}
#endif
