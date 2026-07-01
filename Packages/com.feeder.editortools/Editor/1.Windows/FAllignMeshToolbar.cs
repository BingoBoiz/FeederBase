using System.Collections.Generic;
using UnityEditor;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace Feeder
{
    // scene overlay: opened by FDeduplicateMeshTool Analyze Scene
    public static class FAlignMeshSceneOverlay
    {
        private const int WindowId = 999999;
        private const float WindowWidth = 320f;
        private const float WindowHeight = 240f;
        private const float HeaderHeight = 24f;
        private const float NavButtonWidth = 30f;
        private const float CloseButtonSize = 18f;
        private const string GizmoObjectName = "__Feeder_AlignMesh_Gizmo";

        private static bool _showWindow;
        private static Rect _windowRect = new Rect(0, 0, WindowWidth, WindowHeight);
        private static bool _initializedPosition;
        private static Rect _headerRect;

        private static readonly List<GameObject> SceneMeshCandidates = new List<GameObject>();
        private static GameObject _compareMeshObject;
        private static int _currentIndex = -1;
        private static string _autoAlignStatus = "Auto Align has not run.";
        private static bool _autoAlignStatusIsError;
        private static System.Action _onNextGroup;
        private static GameObject _gizmoObject;
        private static bool _syncingGizmoObject;

        private static GUIStyle _headerStyle;
        private static GUIStyle _bodyStyle;
        private static GUIStyle _statusStyle;

        [InitializeOnLoadMethod]
        private static void Init()
        {
            SceneView.duringSceneGui += OnSceneGUI;
            AssemblyReloadEvents.afterAssemblyReload += OnAfterAssemblyReload;
        }

        public static void OpenWithSceneCandidates(List<GameObject> candidates, Mesh leftPreviewMesh, System.Action onNextGroup = null, bool autoAlignOnce = false)
        {
            if (candidates == null) return;
            DestroyGizmoObject();
            SceneMeshCandidates.Clear();
            SceneMeshCandidates.AddRange(candidates);
            _showWindow = true;
            _onNextGroup = onNextGroup;
            FAlignMeshSceneGizmoDrawer.SetSharedMesh(leftPreviewMesh);
            FAlignMeshSceneGizmoDrawer.SetDrawingEnabled(true);
            _currentIndex = SceneMeshCandidates.Count > 0 ? 0 : -1;
            _compareMeshObject = (_currentIndex >= 0 && _currentIndex < SceneMeshCandidates.Count) ? SceneMeshCandidates[_currentIndex] : null;
            SetAutoAlignStatus("Ready. Use Auto Align to estimate the pose, then Apply Mesh.", false);
            SyncGizmoPoseFromCompareObject(_compareMeshObject);
            if (autoAlignOnce)
                ApplyAutoAlignToHighlightDrawer();
            FocusSceneViewOn(_compareMeshObject);
            SceneView.RepaintAll();
        }

        private static void OnAfterAssemblyReload()
        {
            DestroyLegacyMeshHighlightDrawerHoldersInLoadedScenes();
            DestroyGizmoObject();
            FAlignMeshSceneGizmoDrawer.Clear();
            _showWindow = false;
        }

        private static void DestroyLegacyMeshHighlightDrawerHoldersInLoadedScenes()
        {
            for (int i = 0; i < SceneManager.sceneCount; i++)
            {
                Scene scene = SceneManager.GetSceneAt(i);
                if (!scene.IsValid()) continue;
                GameObject[] roots = scene.GetRootGameObjects();
                for (int r = 0; r < roots.Length; r++)
                {
                    MeshHighlightDrawer[] drawers = roots[r].GetComponentsInChildren<MeshHighlightDrawer>(true);
                    for (int d = 0; d < drawers.Length; d++)
                        Object.DestroyImmediate(drawers[d].gameObject);
                }
            }
        }

        private static void CloseOverlay()
        {
            _showWindow = false;
            DestroyGizmoObject();
            FAlignMeshSceneGizmoDrawer.Clear();
        }

        private static void SyncGizmoPoseFromCompareObject(GameObject source)
        {
            FAlignMeshSceneGizmoDrawer.CopyPoseFrom(source);
            SyncGizmoObjectFromDrawer();
        }

        private static void FocusSceneViewOn(GameObject go)
        {
            if (go == null) return;
            Selection.activeGameObject = go;
            SceneView.lastActiveSceneView?.FrameSelected();
        }

        private static void InitStyles()
        {
            if (_headerStyle != null) return;
            _headerStyle = new GUIStyle(EditorStyles.boldLabel)
            {
                fontSize = 14,
                alignment = TextAnchor.MiddleCenter
            };
            _bodyStyle = new GUIStyle(EditorStyles.helpBox) { padding = new RectOffset(8, 8, 8, 8) };
            _statusStyle = new GUIStyle(EditorStyles.wordWrappedMiniLabel)
            {
                padding = new RectOffset(4, 4, 2, 2)
            };
        }

        private static void OnSceneGUI(SceneView sceneView)
        {
            if (!_showWindow) return;
            SyncDrawerFromGizmoObjectIfNeeded();
            InitStyles();
            if (!_initializedPosition)
            {
                var viewRect = sceneView.position;
                _windowRect.x = viewRect.width - _windowRect.width - 10f;
                _windowRect.y = viewRect.height - _windowRect.height - 30f;
                _initializedPosition = true;
            }
            Handles.BeginGUI();
            _windowRect = GUILayout.Window(WindowId, _windowRect, DrawWindow, "", GUIStyle.none);
            Handles.EndGUI();
        }

        private static void DrawWindow(int id)
        {
            DrawBackground();
            DrawHeader();
            GUILayout.Space(26f);
            GUILayout.BeginVertical(_bodyStyle);
            DrawSceneMeshCandidatesList();
            DrawCompareControls();
            DrawActionButtons();
            DrawAutoAlignStatus();
            GUILayout.EndVertical();
            GUI.DragWindow(_headerRect);
        }

        private static void DrawBackground()
        {
            GUI.Box(new Rect(0, 0, _windowRect.width, _windowRect.height), GUIContent.none);
        }

        private static void DrawHeader()
        {
            _headerRect = new Rect(0, 0, _windowRect.width, HeaderHeight);
            EditorGUI.DrawRect(_headerRect, new Color(0.22f, 0.22f, 0.22f));
            GUI.Label(_headerRect, "Align Mesh Tool", _headerStyle);
            var closeRect = new Rect(_windowRect.width - 22f, 3f, CloseButtonSize, CloseButtonSize);
            if (GUI.Button(closeRect, "X"))
                CloseOverlay();
        }

        private static void DrawSceneMeshCandidatesList()
        {
            GUILayout.Label("Scene mesh candidates");
            var newCount = Mathf.Max(0, EditorGUILayout.IntField("Size", SceneMeshCandidates.Count));
            while (newCount > SceneMeshCandidates.Count)
                SceneMeshCandidates.Add(null);
            while (newCount < SceneMeshCandidates.Count)
                SceneMeshCandidates.RemoveAt(SceneMeshCandidates.Count - 1);
            for (var i = 0; i < SceneMeshCandidates.Count; i++)
                SceneMeshCandidates[i] = (GameObject)EditorGUILayout.ObjectField(SceneMeshCandidates[i], typeof(GameObject), true);
        }

        private static void DrawCompareControls()
        {
            GUILayout.Space(4f);
            GUILayout.BeginHorizontal();
            if (GUILayout.Button("<", GUILayout.Width(NavButtonWidth)))
                SelectPrevCandidate();
            EditorGUI.BeginChangeCheck();
            GameObject selected = (GameObject)EditorGUILayout.ObjectField(_compareMeshObject, typeof(GameObject), true);
            if (EditorGUI.EndChangeCheck())
                SelectCandidateObject(selected);
            if (GUILayout.Button(">", GUILayout.Width(NavButtonWidth)))
                SelectNextCandidate();
            GUILayout.EndHorizontal();
        }

        private static void DrawActionButtons()
        {
            GUILayout.Space(4f);
            GUILayout.BeginHorizontal();
            if (GUILayout.Button("Auto Align"))
            {
                ApplyAutoAlignToHighlightDrawer();
            }
            if (GUILayout.Button("ICP"))
            {
                ApplyTrimIcpToHighlightDrawer();
            }
            GUILayout.EndHorizontal();

            GUILayout.BeginHorizontal();
            string gizmoButtonLabel = _gizmoObject == null ? "Show Gizmo Object" : "Hide Gizmo Object";
            if (GUILayout.Button(gizmoButtonLabel))
                ToggleGizmoObject();
            if (GUILayout.Button("ApplyMesh"))
                ApplyMeshToCompareObject();
            GUILayout.EndHorizontal();

            Color oldBg = GUI.backgroundColor;
            GUI.backgroundColor = new Color(0.4f, 1f, 0.5f);
            if (GUILayout.Button("Auto (Align + Apply All)", GUILayout.Height(24f)))
                RunAutoAlignApplyAll();

            using (new EditorGUI.DisabledScope(_onNextGroup == null))
            {
                GUI.backgroundColor = new Color(0.55f, 0.75f, 1f);
                if (GUILayout.Button("Next Group ⏭", GUILayout.Height(22f)))
                    _onNextGroup?.Invoke();
            }
            GUI.backgroundColor = oldBg;
        }

        private static void DrawAutoAlignStatus()
        {
            Color oldColor = GUI.contentColor;
            GUI.contentColor = _autoAlignStatusIsError ? new Color(1f, 0.46f, 0.35f) : new Color(0.72f, 1f, 0.82f);
            GUILayout.Label(_autoAlignStatus, _statusStyle);
            GUI.contentColor = oldColor;
        }

        private static void ApplyMeshToCompareObject()
        {
            if (!FAlignMeshSceneGizmoDrawer.DrawingEnabled || _compareMeshObject == null) return;

            Mesh newMesh = FAlignMeshSceneGizmoDrawer.SharedMesh;
            if (newMesh == null) return;

            MeshFilter mf = _compareMeshObject.GetComponent<MeshFilter>();
            if (mf == null) return;

            Mesh oldMesh = mf.sharedMesh;
            Transform compareT = _compareMeshObject.transform;
            Transform parent = compareT.parent;
            Vector3 gizmoPosition = FAlignMeshSceneGizmoDrawer.Position;
            Quaternion gizmoRotation = FAlignMeshSceneGizmoDrawer.Rotation;
            Vector3 gizmoLossyScale = FAlignMeshSceneGizmoDrawer.LossyScale;

            Undo.RecordObject(mf, "Apply Mesh");
            Undo.RecordObject(compareT, "Apply Mesh Transform");

            mf.sharedMesh = newMesh;

            if (parent != null)
            {
                compareT.localPosition = parent.InverseTransformPoint(gizmoPosition);
                compareT.localRotation = Quaternion.Inverse(parent.rotation) * gizmoRotation;
                Vector3 parentScale = parent.lossyScale;
                compareT.localScale = new Vector3(
                    parentScale.x != 0f ? gizmoLossyScale.x / parentScale.x : 0f,
                    parentScale.y != 0f ? gizmoLossyScale.y / parentScale.y : 0f,
                    parentScale.z != 0f ? gizmoLossyScale.z / parentScale.z : 0f);
            }
            else
            {
                compareT.SetPositionAndRotation(gizmoPosition, gizmoRotation);
                compareT.localScale = gizmoLossyScale;
            }

            EditorUtility.SetDirty(_compareMeshObject);

            var idx = SceneMeshCandidates.IndexOf(_compareMeshObject);
            if (idx >= 0)
            {
                SceneMeshCandidates.RemoveAt(idx);
                if (idx < _currentIndex) _currentIndex--;
                if (SceneMeshCandidates.Count == 0) _currentIndex = -1;
                else _currentIndex = Mathf.Clamp(_currentIndex, 0, SceneMeshCandidates.Count - 1);
            }

            RemoveMeshFromTargetsIfNoCandidatesRemain(oldMesh);

            if (SceneMeshCandidates.Count > 0 && _currentIndex >= 0)
            {
                _compareMeshObject = SceneMeshCandidates[_currentIndex];
                SyncGizmoPoseFromCompareObject(_compareMeshObject);
                OnCandidateChanged();
                FocusSceneViewOn(_compareMeshObject);
            }
            else
            {
                _compareMeshObject = null;
                DestroyGizmoObject();
            }

            SceneView.RepaintAll();
        }

        private static void RemoveMeshFromTargetsIfNoCandidatesRemain(Mesh mesh)
        {
            if (mesh == null) return;
            if (AnyCandidateUsesMesh(mesh)) return;

            FDataContainer data = FDataPersistenceService.GetOrCreateDataContainer();
            int removedCount = data.TargetMeshes.RemoveAll(targetMesh => targetMesh == mesh);
            if (removedCount <= 0) return;

            FDataPersistenceService.SaveData(data);
            Debug.Log($"<color=cyan>[AlignMesh] Removed applied target mesh '{mesh.name}' from TargetMeshes because no scene candidates remain.</color>");
            EditorWindow.focusedWindow?.Repaint();
        }

        private static bool AnyCandidateUsesMesh(Mesh mesh)
        {
            for (int i = 0; i < SceneMeshCandidates.Count; i++)
            {
                GameObject candidate = SceneMeshCandidates[i];
                if (candidate == null) continue;
                MeshFilter filter = candidate.GetComponent<MeshFilter>();
                if (filter != null && filter.sharedMesh == mesh)
                    return true;
            }
            return false;
        }

        private static bool ApplyAutoAlignToHighlightDrawer()
        {
            if (!FAlignMeshSceneGizmoDrawer.DrawingEnabled || _compareMeshObject == null)
            {
                SetAutoAlignStatus("Auto Align failed: no active compare object.", true);
                return false;
            }

            Mesh meshGizmo = FAlignMeshSceneGizmoDrawer.SharedMesh;
            MeshFilter mfB = _compareMeshObject.GetComponent<MeshFilter>();
            Mesh meshB = mfB != null ? mfB.sharedMesh : null;
            if (meshGizmo == null || meshB == null)
            {
                SetAutoAlignStatus("Auto Align failed: missing source or target mesh.", true);
                return false;
            }

            Transform tB = _compareMeshObject.transform;
            MeshAutoAlignUtils.AutoAlignOptions options = MeshAutoAlignUtils.AutoAlignOptions.Default;
            bool success = MeshAutoAlignUtils.TryAutoAlign(
                meshGizmo,
                meshB,
                tB,
                tB.lossyScale,
                options,
                out MeshAutoAlignUtils.AutoAlignResult result);

            if (!success)
            {
                SetAutoAlignStatus($"Auto Align rejected: {result.FailureReason}", true);
                Debug.LogWarning($"[AlignMesh Auto] {result.FailureReason}");
                return false;
            }

            FAlignMeshSceneGizmoDrawer.SetPositionAndRotation(result.Position, result.Rotation);
            FAlignMeshSceneGizmoDrawer.SetLossyScale(result.LossyScale);
            SyncGizmoObjectFromDrawer();
            SetAutoAlignStatus(
                $"Auto Align OK ({result.BestStage}). Score {result.Score:0.#####}, coverage {result.Coverage:P0}, candidates {result.RefinedCandidateCount}/{result.CandidateCount}.",
                false);
            return true;
        }

        private static void RunAutoAlignApplyAll()
        {
            if (!FAlignMeshSceneGizmoDrawer.DrawingEnabled || SceneMeshCandidates.Count == 0)
            {
                SetAutoAlignStatus("Auto (all): no candidates to process.", true);
                return;
            }

            var snapshot = new List<GameObject>(SceneMeshCandidates);
            int applied = 0;
            int skipped = 0;

            for (int i = 0; i < snapshot.Count; i++)
            {
                GameObject go = snapshot[i];
                if (go == null || !SceneMeshCandidates.Contains(go)) continue;

                _compareMeshObject = go;
                _currentIndex = SceneMeshCandidates.IndexOf(go);
                SyncGizmoPoseFromCompareObject(go);

                if (ApplyAutoAlignToHighlightDrawer())
                {
                    // Applies mesh + transform, removes the GO from the live list, and advances.
                    ApplyMeshToCompareObject();
                    applied++;
                }
                else
                {
                    // Leave it in the list for manual handling.
                    skipped++;
                }
            }

            SetAutoAlignStatus(
                $"Auto done. Applied {applied}, skipped {skipped}. Remaining {SceneMeshCandidates.Count}.",
                skipped > 0);
            SceneView.RepaintAll();
        }

        // ICP (not Trimmed): same logic as MeshKabschAlignMathNetTool.AlignOnce ? nearest-point then Kabsch, apply delta each iter
        private static void ApplyTrimIcpToHighlightDrawer()
        {
            if (!FAlignMeshSceneGizmoDrawer.DrawingEnabled || _compareMeshObject == null) return;

            Mesh meshGizmo = FAlignMeshSceneGizmoDrawer.SharedMesh;
            MeshFilter mfB = _compareMeshObject.GetComponent<MeshFilter>();
            Mesh meshB = mfB.sharedMesh;
            if (meshGizmo.vertexCount < 3 || meshB.vertexCount < 3) return;

            Transform tB = _compareMeshObject.transform;
            Vector3[] vertsGizmo = meshGizmo.vertices;
            Vector3[] vertsB = meshB.vertices;
            int nGizmo = vertsGizmo.Length;
            int nB = vertsB.Length;

            Vector3[] worldPointsB = new Vector3[nB];
            for (int j = 0; j < nB; j++)
                worldPointsB[j] = tB.TransformPoint(vertsB[j]);

            Vector3[] worldSource = new Vector3[nGizmo];
            Vector3[] pairedTarget = new Vector3[nGizmo];

            const int icpMaxIterations = 20;
            const float convergencePos = 1e-5f;
            const float convergenceDeg = 0.001f;

            Matrix4x4 gizmoMatrix = Matrix4x4.TRS(
                FAlignMeshSceneGizmoDrawer.Position,
                FAlignMeshSceneGizmoDrawer.Rotation,
                FAlignMeshSceneGizmoDrawer.LossyScale);

            for (int iter = 0; iter < icpMaxIterations; iter++)
            {
                for (int i = 0; i < nGizmo; i++)
                    worldSource[i] = gizmoMatrix.MultiplyPoint3x4(vertsGizmo[i]);

                for (int i = 0; i < nGizmo; i++)
                {
                    float bestSq = float.MaxValue;
                    int bestJ = 0;
                    for (int j = 0; j < nB; j++)
                    {
                        float sq = (worldSource[i] - worldPointsB[j]).sqrMagnitude;
                        if (sq < bestSq) { bestSq = sq; bestJ = j; }
                    }
                    pairedTarget[i] = worldPointsB[bestJ];
                }

                if (!MeshMatchTransformUtils.ComputeRigidTransform(worldSource, pairedTarget, out Matrix4x4 Rt))
                {
                    Debug.LogWarning("[AlignMesh ICP] ComputeRigidTransform failed.");
                    break;
                }

                FAlignMeshSceneGizmoDrawer.ApplyRigidTransformDelta(Rt);
                gizmoMatrix = Matrix4x4.TRS(
                    FAlignMeshSceneGizmoDrawer.Position,
                    FAlignMeshSceneGizmoDrawer.Rotation,
                    FAlignMeshSceneGizmoDrawer.LossyScale);

                if (IsIcpConverged(Rt, convergencePos, convergenceDeg))
                    break;
            }

            FAlignMeshSceneGizmoDrawer.SetLossyScale(tB.lossyScale);
            SyncGizmoObjectFromDrawer();
            SetAutoAlignStatus("ICP refinement applied to gizmo.", false);
        }

        private static bool IsIcpConverged(Matrix4x4 Rt, float convergencePos, float convergenceDeg)
        {
            Vector3 trans = Rt.GetColumn(3);
            if (trans.sqrMagnitude > convergencePos * convergencePos) return false;
            return Quaternion.Angle(Quaternion.identity, Rt.rotation) <= convergenceDeg;
        }

        private static void SelectPrevCandidate()
        {
            if (SceneMeshCandidates.Count == 0) return;
            _currentIndex = (_currentIndex - 1 + SceneMeshCandidates.Count) % SceneMeshCandidates.Count;
            _compareMeshObject = SceneMeshCandidates[_currentIndex];
            SyncGizmoPoseFromCompareObject(_compareMeshObject);
            OnCandidateChanged();
            FocusSceneViewOn(_compareMeshObject);
        }

        private static void SelectNextCandidate()
        {
            if (SceneMeshCandidates.Count == 0) return;
            _currentIndex = (_currentIndex + 1) % SceneMeshCandidates.Count;
            _compareMeshObject = SceneMeshCandidates[_currentIndex];
            SyncGizmoPoseFromCompareObject(_compareMeshObject);
            OnCandidateChanged();
            FocusSceneViewOn(_compareMeshObject);
        }

        private static void SelectCandidateObject(GameObject selected)
        {
            _compareMeshObject = selected;
            _currentIndex = SceneMeshCandidates.IndexOf(selected);
            SyncGizmoPoseFromCompareObject(_compareMeshObject);
            OnCandidateChanged();
            FocusSceneViewOn(_compareMeshObject);
        }

        // Candidate switching does a fast snap (gizmo pose copied from the object) only —
        // no expensive Auto Align, so browsing objects stays responsive.
        private static void OnCandidateChanged()
        {
            SetAutoAlignStatus("Candidate changed (quick aligned). Use Auto Align to refine, then Apply Mesh.", false);
        }

        private static void ToggleGizmoObject()
        {
            if (_gizmoObject != null)
            {
                DestroyGizmoObject();
                return;
            }

            Mesh mesh = FAlignMeshSceneGizmoDrawer.SharedMesh;
            if (mesh == null)
            {
                SetAutoAlignStatus("Cannot show gizmo object: missing source mesh.", true);
                return;
            }

            _gizmoObject = new GameObject(GizmoObjectName);
            _gizmoObject.hideFlags = HideFlags.DontSave;
            MeshFilter filter = _gizmoObject.AddComponent<MeshFilter>();
            filter.sharedMesh = mesh;
            _gizmoObject.AddComponent<MeshRenderer>().enabled = false;
            SyncGizmoObjectFromDrawer();
            Selection.activeGameObject = _gizmoObject;
            SetAutoAlignStatus("Gizmo object shown. Move/rotate/scale it to adjust the wire gizmo.", false);
        }

        private static void SyncGizmoObjectFromDrawer()
        {
            if (_gizmoObject == null) return;
            _syncingGizmoObject = true;
            Transform t = _gizmoObject.transform;
            t.SetPositionAndRotation(FAlignMeshSceneGizmoDrawer.Position, FAlignMeshSceneGizmoDrawer.Rotation);
            t.localScale = FAlignMeshSceneGizmoDrawer.LossyScale;
            MeshFilter filter = _gizmoObject.GetComponent<MeshFilter>();
            if (filter != null)
                filter.sharedMesh = FAlignMeshSceneGizmoDrawer.SharedMesh;
            _syncingGizmoObject = false;
        }

        private static void SyncDrawerFromGizmoObjectIfNeeded()
        {
            if (_gizmoObject == null || _syncingGizmoObject) return;
            Transform t = _gizmoObject.transform;
            if (t.hasChanged)
            {
                FAlignMeshSceneGizmoDrawer.SetPositionAndRotation(t.position, t.rotation);
                FAlignMeshSceneGizmoDrawer.SetLossyScale(t.lossyScale);
                t.hasChanged = false;
            }
        }

        private static void DestroyGizmoObject()
        {
            if (_gizmoObject == null) return;
            Object.DestroyImmediate(_gizmoObject);
            _gizmoObject = null;
        }

        private static void SetAutoAlignStatus(string message, bool isError)
        {
            _autoAlignStatus = message;
            _autoAlignStatusIsError = isError;
            SceneView.RepaintAll();
        }
    }
}
