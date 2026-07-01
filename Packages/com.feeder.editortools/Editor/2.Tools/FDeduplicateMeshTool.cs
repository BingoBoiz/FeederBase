using System.Collections.Generic;
using Sirenix.OdinInspector;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;

namespace Feeder
{
    public sealed class FDeduplicateMeshTool : FTargetMeshesToolBase
    {
        public static readonly string LeftPreviewSlotId = "DeduplicateMeshTool_Left";
        public static readonly string RightPreviewSlotId = "DeduplicateMeshTool_Right";

        private const string GroupOriginal = "Original";
        private const string GroupUncategorized = "Uncategorized";
        private const float ContentMargin = 16f;
        private const float NavButtonWidth = 40f;
        private const float NavButtonHeight = 28f;
        private const float NavGap = 8f;
        private const float ActionButtonGap = 6f;

        [System.Serializable]
        private sealed class MeshGroup
        {
            // Back-reference to the owning tool so the per-group Odin buttons can call into it.
            // Not serialized to avoid a reference cycle; re-assigned each frame by the tool.
            [System.NonSerialized, HideInInspector]
            public FDeduplicateMeshTool Owner;

            [HorizontalGroup("Head"), LabelText("Name"), LabelWidth(40f)]
            public string GroupName;

            [HorizontalGroup("Head", Width = 66f)]
            [GUIColor("@MergeColor")]
            [Button("@MergeLabel")]
            private void MergeButton() => Owner?.HandleMergeClick(this);

            [HorizontalGroup("Head", Width = 66f)]
            [GUIColor(0.4f, 1f, 0.5f)]
            [Button("Analyze")]
            private void AnalyzeButton() => Owner?.AnalyzeGroup(this);

            [HorizontalGroup("Head", Width = 60f)]
            [GUIColor(1f, 0.45f, 0.45f)]
            [Button("Delete")]
            private void DeleteButton() => Owner?.RequestDeleteGroup(this);

            [ListDrawerSettings(ShowFoldout = true, DraggableItems = true, ShowIndexLabels = true, ShowItemCount = true)]
            public List<Mesh> Meshes = new List<Mesh>();

            private string MergeLabel => Owner != null ? Owner.GetMergeLabel(this) : "Merge";
            private Color MergeColor => Owner != null ? Owner.GetMergeColor(this) : Color.white;
        }

        [SerializeField]
        [PropertyOrder(99)]
        [PropertyRange(0f, 1f)]
        [LabelText("Find Tolerance")]
        [Tooltip("0 = giống hệt, tăng lên để FindMesh gom cả những mesh lệch nhẹ vào chung group.")]
        private float _similarityTolerance = 0.1f;

        [SerializeField]
        [PropertyOrder(101)]
        [HideIf("@this._groups.Count == 0")]
        [HideReferenceObjectPicker]
        [ListDrawerSettings(ShowFoldout = true, DraggableItems = true, ShowItemCount = true, ListElementLabelName = "GroupName")]
        private List<MeshGroup> _groups = new List<MeshGroup>();

        [System.NonSerialized] private MeshGroup _mergeSource;
        [System.NonSerialized] private MeshGroup _pendingDeleteGroup;
        [System.NonSerialized] private bool _pendingClearAll;
        [System.NonSerialized] private bool _showManualPreview;
        [System.NonSerialized] private MeshGroup _lastAnalyzedGroup;
        [System.NonSerialized] private bool _previewSavePending;

        protected override string GetDescription()
        {
            return "Gom TargetMeshes thành các nhóm mesh có khả năng trùng nhau. Chỉnh group xong bấm Analyze ở từng group để align/apply trong Scene View.";
        }

        private void OnEnable()
        {
            if (_groups == null) _groups = new List<MeshGroup>();
            EnsureGroupOwners();
            _mergeSource = null;
        }

        protected override void OnTargetMeshesChanged()
        {
            _mergeSource = null;
            RebuildToOriginal();
        }

        // Re-assign the back-reference each frame so groups created by Odin's list "+" button
        // (which bypasses our factory) can still reach the tool from their per-group buttons.
        private void EnsureGroupOwners()
        {
            for (int i = 0; i < _groups.Count; i++)
                if (_groups[i] != null)
                    _groups[i].Owner = this;
        }

        [PropertyOrder(100)]
        [OnInspectorGUI]
        private void DrawGroupsSection()
        {
            EnsureGroupOwners();

            GUILayout.Space(4f);
            EditorGUILayout.BeginHorizontal();
            Color originalBg = GUI.backgroundColor;

            GUI.backgroundColor = new Color(0.4f, 0.8f, 1f);
            if (GUILayout.Button("FindMesh", GUILayout.Height(26f)))
                DiscoverSimilarMeshGroups();

            GUI.backgroundColor = new Color(0.5f, 0.9f, 0.6f);
            if (GUILayout.Button("New Group", GUILayout.Height(26f)))
                CreateGroupFromSelection();

            GUI.backgroundColor = new Color(0.9f, 0.75f, 0.35f);
            if (GUILayout.Button(_showManualPreview ? "Hide Manual" : "Apply Manual", GUILayout.Height(26f)))
                _showManualPreview = !_showManualPreview;

            if (_groups.Count > 0)
            {
                GUI.backgroundColor = new Color(1f, 0.45f, 0.45f);
                if (GUILayout.Button("Clear All", GUILayout.Height(26f)))
                    RequestClearAllGroups();
            }

            GUI.backgroundColor = originalBg;
            EditorGUILayout.EndHorizontal();

            if (_showManualPreview)
                DrawManualPreviewSection();

            // Deferred clear-all requested by the red button (mutating the list mid-draw is unsafe).
            if (_pendingClearAll)
            {
                _groups.Clear();
                _mergeSource = null;
                _lastAnalyzedGroup = null;
                _pendingDeleteGroup = null;
                _pendingClearAll = false;
            }

            // Deferred group removal requested by a Delete button (mutating the list mid-draw is unsafe).
            if (_pendingDeleteGroup != null)
            {
                _groups.Remove(_pendingDeleteGroup);
                if (_mergeSource == _pendingDeleteGroup) _mergeSource = null;
                if (_lastAnalyzedGroup == _pendingDeleteGroup) _lastAnalyzedGroup = null;
                _pendingDeleteGroup = null;
            }

            if (_groups.Count == 0)
            {
                EditorGUILayout.HelpBox("Add Mesh assets to TargetMeshes, then click FindMesh to create editable similarity groups.", MessageType.Info);
                return;
            }

            // Reference-mesh hint shown once as a guide (green box) instead of inside every group.
            GUILayout.Space(4f);
            StylesUtils.DrawInfoBox(
                "Element 0 = reference mesh. Analyze thay các mesh còn lại trong group bằng element 0. Kéo để sắp xếp lại thứ tự.");

            if (_mergeSource != null)
            {
                EditorGUILayout.HelpBox(
                    $"Merge mode: bấm \"→ Here\" trên group đích để gộp \"{DisplayName(_mergeSource)}\" vào đó. Bấm \"Cancel\" để huỷ.",
                    MessageType.Info);
            }

            GUILayout.Space(4f);
            EditorGUILayout.LabelField($"Mesh Groups ({_groups.Count})", EditorStyles.boldLabel);
        }

        private void DiscoverSimilarMeshGroups()
        {
            List<Mesh> meshes = TargetMeshes;
            _mergeSource = null;
            _groups.Clear();

            if (meshes == null || meshes.Count == 0)
            {
                Debug.LogWarning("[Deduplicate Mesh] No TargetMeshes to analyze.");
                return;
            }

            bool[] consumed = new bool[meshes.Count];
            List<Mesh> uncategorized = new List<Mesh>();

            for (int i = 0; i < meshes.Count; i++)
            {
                if (consumed[i]) continue;
                Mesh reference = meshes[i];
                if (reference == null)
                {
                    consumed[i] = true;
                    continue;
                }

                List<Mesh> groupMeshes = new List<Mesh> { reference };
                consumed[i] = true;

                for (int j = i + 1; j < meshes.Count; j++)
                {
                    if (consumed[j]) continue;
                    Mesh candidate = meshes[j];
                    if (candidate == null)
                    {
                        consumed[j] = true;
                        continue;
                    }

                    if (CouldBeSimilar(reference, candidate) &&
                        DeduplicateMeshUtils.AreMeshesVertexSimilar(reference, candidate, _similarityTolerance))
                    {
                        consumed[j] = true;
                        groupMeshes.Add(candidate);
                    }
                }

                if (groupMeshes.Count > 1)
                    _groups.Add(new MeshGroup { Owner = this, GroupName = BuildGroupName(reference, _groups.Count), Meshes = groupMeshes });
                else
                    uncategorized.Add(reference);
            }

            if (uncategorized.Count > 0)
                _groups.Add(new MeshGroup { Owner = this, GroupName = GroupUncategorized, Meshes = uncategorized });

            if (meshes.Count > 0)
                _groups.Add(new MeshGroup { Owner = this, GroupName = GroupOriginal, Meshes = new List<Mesh>(meshes) });

            SortGroups();
            Debug.Log($"<color=cyan>[Deduplicate Mesh] Discovered {_groups.Count} groups from {meshes.Count} target meshes.</color>");
        }

        private void DrawManualPreviewSection()
        {
            var list = TargetMeshes;
            if (list == null || list.Count == 0)
            {
                EditorGUILayout.HelpBox("Add at least one Mesh to TargetMeshes to use manual preview.", MessageType.Info);
                return;
            }

            GUILayout.Space(8f);
            EditorGUILayout.LabelField("Manual Preview", EditorStyles.boldLabel);

            var data = GetDataContainer();
            int count = list.Count;
            int maxIdx = count - 1;

            int leftIdx = Mathf.Clamp(data.TargetMeshesLeftPreviewIndex, 0, maxIdx);
            int rightIdx = Mathf.Clamp(data.TargetMeshesRightPreviewIndex, 0, maxIdx);
            if (leftIdx != data.TargetMeshesLeftPreviewIndex) data.TargetMeshesLeftPreviewIndex = leftIdx;
            if (rightIdx != data.TargetMeshesRightPreviewIndex) data.TargetMeshesRightPreviewIndex = rightIdx;

            EnsureLeftRightDifferent(data, count);
            leftIdx = data.TargetMeshesLeftPreviewIndex;
            rightIdx = data.TargetMeshesRightPreviewIndex;

            Mesh meshA = list[leftIdx];
            Mesh meshB = list[rightIdx];

            float availableWidth = Mathf.Max(200f, FOdinMenuLayoutUtils.GetContentWidthWithMargin(ContentMargin));
            float halfW = (availableWidth - NavGap) * 0.5f;
            float totalBlocksWidth = halfW + NavGap + halfW;
            float blocksStartX = (availableWidth - totalBlocksWidth) * 0.5f;

            float navRowHeight = NavButtonHeight + 4f;
            Rect navRect = GUILayoutUtility.GetRect(availableWidth, navRowHeight);
            float leftNavStart = navRect.x + blocksStartX;
            float rightNavStart = navRect.x + blocksStartX + halfW + NavGap;

            DrawPreviewNavRow(navRect, leftNavStart, halfW, leftIdx, rightIdx, count, data, isLeft: true);
            DrawPreviewNavRow(navRect, rightNavStart, halfW, rightIdx, leftIdx, count, data, isLeft: false);

            float meshRowHeight = EditorGUIUtility.singleLineHeight;
            Rect meshRowRect = GUILayoutUtility.GetRect(availableWidth, meshRowHeight);
            const float indexLabelWidth = 28f;
            Rect leftIndexRect = new Rect(meshRowRect.x + blocksStartX, meshRowRect.y, indexLabelWidth, meshRowHeight);
            Rect leftFieldRect = new Rect(leftIndexRect.xMax, meshRowRect.y, halfW - indexLabelWidth, meshRowHeight);
            Rect rightIndexRect = new Rect(meshRowRect.x + blocksStartX + halfW + NavGap, meshRowRect.y, indexLabelWidth, meshRowHeight);
            Rect rightFieldRect = new Rect(rightIndexRect.xMax, meshRowRect.y, halfW - indexLabelWidth, meshRowHeight);

            GUI.Label(leftIndexRect, $"[{leftIdx}]", EditorStyles.miniLabel);
            EditorGUI.BeginChangeCheck();
            Mesh newA = (Mesh)EditorGUI.ObjectField(leftFieldRect, meshA, typeof(Mesh), false);
            if (EditorGUI.EndChangeCheck()) SetPreviewMesh(data, list, newA, isLeft: true);

            GUI.Label(rightIndexRect, $"[{rightIdx}]", EditorStyles.miniLabel);
            EditorGUI.BeginChangeCheck();
            Mesh newB = (Mesh)EditorGUI.ObjectField(rightFieldRect, meshB, typeof(Mesh), false);
            if (EditorGUI.EndChangeCheck()) SetPreviewMesh(data, list, newB, isLeft: false);

            GUILayout.Space(4f);

            float blockHeight = FMeshPreviewDrawer.BlockHeight;
            Rect rowRect = GUILayoutUtility.GetRect(availableWidth, blockHeight);
            Rect leftBlock = new Rect(rowRect.x + blocksStartX, rowRect.y, halfW, blockHeight);
            Rect rightBlock = new Rect(rowRect.x + blocksStartX + halfW + NavGap, rowRect.y, halfW, blockHeight);

            FMeshPreviewDrawer.DrawBlock(leftBlock, meshA, LeftPreviewSlotId);
            FMeshPreviewDrawer.DrawBlock(rightBlock, meshB, RightPreviewSlotId);

            HandleMeshDrop(leftBlock, data, list, isLeft: true);
            HandleMeshDrop(rightBlock, data, list, isLeft: false);

            float rotationRowHeight = EditorGUIUtility.singleLineHeight;
            Rect rotationRowRect = GUILayoutUtility.GetRect(availableWidth, rotationRowHeight);
            PreviewUtils.DrawMeshRotationFields(new Rect(rotationRowRect.x + blocksStartX, rotationRowRect.y, halfW, rotationRowHeight), LeftPreviewSlotId, "Rotation");
            PreviewUtils.DrawMeshRotationFields(new Rect(rotationRowRect.x + blocksStartX + halfW + NavGap, rotationRowRect.y, halfW, rotationRowHeight), RightPreviewSlotId, "Rotation");

            GUILayout.Space(8f);

            float actionRowHeight = NavButtonHeight;
            Rect actionRowRect = GUILayoutUtility.GetRect(availableWidth, actionRowHeight);
            float actionButtonWidth = (availableWidth - ActionButtonGap) / 2f;
            float ax = actionRowRect.x;
            float ay = actionRowRect.y;

            if (GUI.Button(new Rect(ax, ay, actionButtonWidth, actionRowHeight), "AnalyzeScene"))
                RunAnalyzeManualPair(list, leftIdx, rightIdx, autoAlign: false);
            ax += actionButtonWidth + ActionButtonGap;
            if (GUI.Button(new Rect(ax, ay, actionButtonWidth, actionRowHeight), "AlignMesh"))
                RunAnalyzeManualPair(list, leftIdx, rightIdx, autoAlign: true);

            GUILayout.Space(8f);
        }

        private bool CouldBeSimilar(Mesh a, Mesh b)
        {
            if (a == null || b == null) return false;
            if (a.vertexCount != b.vertexCount) return false;
            if (a.subMeshCount != b.subMeshCount) return false;

            Vector3 sizeA = a.bounds.size;
            Vector3 sizeB = b.bounds.size;
            float scale = Mathf.Max(0.0001f, sizeA.magnitude);
            return Mathf.Abs(sizeA.x - sizeB.x) <= scale * _similarityTolerance &&
                   Mathf.Abs(sizeA.y - sizeB.y) <= scale * _similarityTolerance &&
                   Mathf.Abs(sizeA.z - sizeB.z) <= scale * _similarityTolerance;
        }

        private static string BuildGroupName(Mesh reference, int groupIndex)
        {
            string meshName = reference != null ? reference.name : "";
            if (string.IsNullOrEmpty(meshName))
                return $"Similar Mesh {groupIndex + 1}";
            return $"Similar - {meshName}";
        }

        private void SortGroups()
        {
            MeshGroup original = FindGroup(GroupOriginal);
            MeshGroup uncategorized = FindGroup(GroupUncategorized);

            _groups.RemoveAll(g => g.GroupName == GroupOriginal || g.GroupName == GroupUncategorized);
            _groups.Sort((a, b) => b.Meshes.Count.CompareTo(a.Meshes.Count));

            if (uncategorized != null) _groups.Add(uncategorized);
            if (original != null) _groups.Add(original);
        }

        private MeshGroup FindGroup(string name)
        {
            for (int i = 0; i < _groups.Count; i++)
                if (string.Equals(_groups[i].GroupName, name, System.StringComparison.OrdinalIgnoreCase))
                    return _groups[i];
            return null;
        }

        private static string DisplayName(MeshGroup group)
        {
            if (group == null) return "";
            return string.IsNullOrEmpty(group.GroupName) ? "Group" : group.GroupName;
        }

        // Dynamic label/color for each group's Merge button (resolved by Odin per element).
        private string GetMergeLabel(MeshGroup group)
        {
            if (_mergeSource == group) return "Cancel";
            if (_mergeSource != null) return "→ Here";
            return "Merge";
        }

        private Color GetMergeColor(MeshGroup group)
        {
            if (_mergeSource == group) return new Color(1f, 0.7f, 0.2f);
            if (_mergeSource != null) return new Color(0.5f, 0.85f, 1f);
            return new Color(0.9f, 0.75f, 0.35f);
        }

        private void HandleMergeClick(MeshGroup clicked)
        {
            if (clicked == null) return;

            if (_mergeSource == null)
            {
                _mergeSource = clicked;
                return;
            }

            if (_mergeSource == clicked)
            {
                _mergeSource = null;
                return;
            }

            MeshGroup source = _mergeSource;
            MeshGroup target = clicked;
            if (target.Meshes == null) target.Meshes = new List<Mesh>();

            HashSet<Mesh> targetSet = new HashSet<Mesh>(target.Meshes);
            if (source.Meshes != null)
            {
                for (int i = 0; i < source.Meshes.Count; i++)
                {
                    Mesh mesh = source.Meshes[i];
                    if (mesh != null && targetSet.Add(mesh))
                        target.Meshes.Add(mesh);
                }
            }

            _groups.Remove(source);
            _mergeSource = null;
            SortGroups();
        }

        private void RequestDeleteGroup(MeshGroup group)
        {
            if (group == null) return;
            if (EditorUtility.DisplayDialog("Delete Group", $"Delete group \"{DisplayName(group)}\"?", "Delete", "Cancel"))
                _pendingDeleteGroup = group;
        }

        private void RequestClearAllGroups()
        {
            if (_groups.Count == 0) return;
            if (EditorUtility.DisplayDialog("Clear All Groups", $"Delete all {_groups.Count} groups? Không xoá TargetMeshes.", "Clear All", "Cancel"))
                _pendingClearAll = true;
        }

        // Quick manual group from whatever the user has selected in the Scene or Project window.
        private void CreateGroupFromSelection()
        {
            List<Mesh> meshes = CollectMeshesFromSelection();
            if (meshes.Count == 0)
            {
                EditorUtility.DisplayDialog("New Group", "Chọn Mesh trong Project hoặc GameObject trong Scene rồi bấm New Group.", "OK");
                return;
            }

            int manualCount = 0;
            for (int i = 0; i < _groups.Count; i++)
                if (_groups[i] != null && _groups[i].GroupName != null && _groups[i].GroupName.StartsWith("Manual Group"))
                    manualCount++;

            _groups.Insert(0, new MeshGroup
            {
                Owner = this,
                GroupName = $"Manual Group {manualCount + 1}",
                Meshes = meshes
            });
            Debug.Log($"<color=cyan>[Deduplicate Mesh] Created manual group with {meshes.Count} mesh(es) from selection.</color>");
        }

        private static List<Mesh> CollectMeshesFromSelection()
        {
            var result = new List<Mesh>();
            var seen = new HashSet<Mesh>();
            Object[] selection = Selection.objects;
            if (selection == null) return result;

            for (int i = 0; i < selection.Length; i++)
            {
                if (selection[i] is Mesh mesh)
                {
                    if (mesh != null && seen.Add(mesh)) result.Add(mesh);
                }
                else if (selection[i] is GameObject go)
                {
                    foreach (MeshFilter mf in go.GetComponentsInChildren<MeshFilter>(true))
                        if (mf.sharedMesh != null && seen.Add(mf.sharedMesh)) result.Add(mf.sharedMesh);
                    foreach (SkinnedMeshRenderer smr in go.GetComponentsInChildren<SkinnedMeshRenderer>(true))
                        if (smr.sharedMesh != null && seen.Add(smr.sharedMesh)) result.Add(smr.sharedMesh);
                }
            }
            return result;
        }

        private void RebuildToOriginal()
        {
            _groups.Clear();
            List<Mesh> meshes = TargetMeshes;
            if (meshes != null && meshes.Count > 0)
                _groups.Add(new MeshGroup { Owner = this, GroupName = GroupOriginal, Meshes = new List<Mesh>(meshes) });
        }

        private void AnalyzeGroup(MeshGroup group)
        {
            if (group == null || !_groups.Contains(group)) return;
            _lastAnalyzedGroup = group;
            TryAnalyzeGroup(group, showDialogs: true);
        }

        // Invoked by the "Next Group" button in the Align Mesh scene overlay: skip Original/Uncategorized
        // and any group with nothing to replace in the scene, then analyze the next usable group.
        private void AnalyzeNextGroup()
        {
            if (_groups.Count == 0) return;

            int lastIdx = _lastAnalyzedGroup != null ? _groups.IndexOf(_lastAnalyzedGroup) : -1;
            int baseIdx = lastIdx < 0 ? _groups.Count - 1 : lastIdx;
            for (int step = 1; step <= _groups.Count; step++)
            {
                int idx = (baseIdx + step) % _groups.Count;
                if (idx == lastIdx) continue;

                MeshGroup g = _groups[idx];
                if (IsSpecialGroup(g)) continue;
                if (TryAnalyzeGroup(g, showDialogs: false))
                {
                    _lastAnalyzedGroup = g;
                    return;
                }
            }

            EditorUtility.DisplayDialog("Next Group", "No other group has meshes to align in the current scene.", "OK");
        }

        private bool IsSpecialGroup(MeshGroup group)
        {
            return string.Equals(group.GroupName, GroupOriginal, System.StringComparison.OrdinalIgnoreCase)
                || string.Equals(group.GroupName, GroupUncategorized, System.StringComparison.OrdinalIgnoreCase);
        }

        private bool TryAnalyzeGroup(MeshGroup group, bool showDialogs)
        {
            if (group == null || group.Meshes == null || group.Meshes.Count == 0) return false;

            // Element 0 is the reference/standard mesh that replaces the others.
            Mesh sourceMesh = group.Meshes[0];
            if (sourceMesh == null)
            {
                sourceMesh = FindFirstValidMesh(group.Meshes);
                if (sourceMesh != null)
                    Debug.LogWarning("[Deduplicate Mesh] Element 0 is empty; using the first valid mesh as reference instead.");
            }
            if (sourceMesh == null)
            {
                if (showDialogs) EditorUtility.DisplayDialog("Analyze Group", "This group has no valid source mesh.", "OK");
                return false;
            }

            List<Mesh> otherMeshes = new List<Mesh>();
            for (int i = 0; i < group.Meshes.Count; i++)
            {
                Mesh mesh = group.Meshes[i];
                if (mesh != null && mesh != sourceMesh)
                    otherMeshes.Add(mesh);
            }

            if (otherMeshes.Count == 0)
            {
                if (showDialogs) EditorUtility.DisplayDialog("Analyze Group", "This group has no other meshes to replace — only the reference (element 0).", "OK");
                return false;
            }

            var scene = EditorSceneManager.GetActiveScene();
            if (!scene.IsValid()) return false;

            var gos = SceneMeshHighlightUtils.FindGameObjectsWithAnyMesh(scene, otherMeshes);
            if (gos.Count == 0)
            {
                if (showDialogs) EditorUtility.DisplayDialog("Analyze Group", "No GameObjects in the current scene use the other meshes in this group (element 0 excluded).", "OK");
                return false;
            }

            SceneMeshHighlightUtils.IsolateGameObjects(scene, gos);
            // Quick align only on open/switch (no slow Auto Align); expose Next Group navigation to the overlay.
            FAlignMeshSceneOverlay.OpenWithSceneCandidates(gos, sourceMesh, AnalyzeNextGroup);
            EditorWindow.focusedWindow?.Repaint();
            return true;
        }

        private static void RunAnalyzeManualPair(IReadOnlyList<Mesh> list, int leftIdx, int rightIdx, bool autoAlign)
        {
            Mesh meshRight = list[rightIdx];
            if (meshRight == null) return;

            var scene = EditorSceneManager.GetActiveScene();
            if (!scene.IsValid()) return;

            var gos = SceneMeshHighlightUtils.FindGameObjectsWithMesh(scene, meshRight);
            if (gos.Count == 0)
            {
                EditorUtility.DisplayDialog("Analyze Scene", "No GameObjects in the current scene use this mesh.", "OK");
                return;
            }

            SceneMeshHighlightUtils.IsolateGameObjects(scene, gos);
            Mesh meshLeft = list[leftIdx];
            FAlignMeshSceneOverlay.OpenWithSceneCandidates(gos, meshLeft, onNextGroup: null, autoAlignOnce: autoAlign);
            EditorWindow.focusedWindow?.Repaint();
        }

        private static void EnsureLeftRightDifferent(FDataContainer data, int count)
        {
            if (count < 2) return;
            int l = data.TargetMeshesLeftPreviewIndex;
            int r = data.TargetMeshesRightPreviewIndex;
            if (l != r) return;
            data.TargetMeshesRightPreviewIndex = (r + 1) % count;
        }

        // Assign a preview slot from an ObjectField change or a drop. Meshes not already in
        // TargetMeshes are appended so the index-based selection stays valid.
        private void SetPreviewMesh(FDataContainer data, List<Mesh> list, Mesh mesh, bool isLeft)
        {
            if (mesh == null) return;
            int idx = list.IndexOf(mesh);
            if (idx < 0)
            {
                list.Add(mesh);
                idx = list.Count - 1;
            }
            if (isLeft) data.TargetMeshesLeftPreviewIndex = idx;
            else data.TargetMeshesRightPreviewIndex = idx;
            RequestPreviewIndexSave();
            EditorWindow.focusedWindow?.Repaint();
        }

        private void HandleMeshDrop(Rect rect, FDataContainer data, List<Mesh> list, bool isLeft)
        {
            Event e = Event.current;
            if (e == null || !rect.Contains(e.mousePosition)) return;

            if (e.type == EventType.DragUpdated)
            {
                DragAndDrop.visualMode = ExtractMesh(DragAndDrop.objectReferences) != null
                    ? DragAndDropVisualMode.Copy
                    : DragAndDropVisualMode.Rejected;
                e.Use();
            }
            else if (e.type == EventType.DragPerform)
            {
                DragAndDrop.AcceptDrag();
                SetPreviewMesh(data, list, ExtractMesh(DragAndDrop.objectReferences), isLeft);
                e.Use();
            }
        }

        private static Mesh ExtractMesh(Object[] refs)
        {
            if (refs == null) return null;
            for (int i = 0; i < refs.Length; i++)
                if (refs[i] is Mesh mesh) return mesh;
            for (int i = 0; i < refs.Length; i++)
            {
                if (refs[i] is GameObject go)
                {
                    var mf = go.GetComponentInChildren<MeshFilter>();
                    if (mf != null && mf.sharedMesh != null) return mf.sharedMesh;
                }
            }
            return null;
        }

        // Coalesce preview-index persistence: many arrow clicks in a frame collapse to a single
        // disk write that runs off the GUI event (Save(true) per click was the switching hitch).
        private void RequestPreviewIndexSave()
        {
            if (_previewSavePending) return;
            _previewSavePending = true;
            EditorApplication.delayCall += FlushPreviewIndexSave;
        }

        private void FlushPreviewIndexSave()
        {
            _previewSavePending = false;
            FDataPersistenceService.SaveData(GetDataContainer());
        }

        private void DrawPreviewNavRow(Rect navRect, float startX, float halfW, int currentIndex, int otherIndex, int count, FDataContainer data, bool isLeft)
        {
            float navGroupWidth = NavButtonWidth + NavGap + NavButtonWidth;
            float groupStart = startX + Mathf.Max(0, (halfW - navGroupWidth) * 0.5f);
            Rect leftBtn = new Rect(navRect.x + groupStart, navRect.y, NavButtonWidth, NavButtonHeight);
            Rect rightBtn = new Rect(navRect.x + groupStart + NavButtonWidth + NavGap, navRect.y, NavButtonWidth, NavButtonHeight);

            Texture prevIcon = EditorGUIUtility.IconContent("d_Animation.PrevKey")?.image;
            Texture nextIcon = EditorGUIUtility.IconContent("d_Animation.NextKey")?.image;
            if (GUI.Button(leftBtn, new GUIContent(prevIcon, isLeft ? "Previous mesh (left)" : "Previous mesh (right)")))
            {
                int next = (currentIndex - 1 + count) % count;
                while (next == otherIndex && count > 1)
                    next = (next - 1 + count) % count;
                if (isLeft) data.TargetMeshesLeftPreviewIndex = next; else data.TargetMeshesRightPreviewIndex = next;
                RequestPreviewIndexSave();
                if (Event.current != null) Event.current.Use();
                GUI.changed = true;
                EditorWindow.focusedWindow?.Repaint();
            }
            if (GUI.Button(rightBtn, new GUIContent(nextIcon, isLeft ? "Next mesh (left)" : "Next mesh (right)")))
            {
                int next = (currentIndex + 1) % count;
                while (next == otherIndex && count > 1)
                    next = (next + 1) % count;
                if (isLeft) data.TargetMeshesLeftPreviewIndex = next; else data.TargetMeshesRightPreviewIndex = next;
                RequestPreviewIndexSave();
                if (Event.current != null) Event.current.Use();
                GUI.changed = true;
                EditorWindow.focusedWindow?.Repaint();
            }
        }

        private static Mesh FindFirstValidMesh(IReadOnlyList<Mesh> meshes)
        {
            for (int i = 0; i < meshes.Count; i++)
                if (meshes[i] != null)
                    return meshes[i];
            return null;
        }
    }
}
