using System.Collections.Generic;
using System.Linq;
using Sirenix.OdinInspector;
using UnityEditor;
using UnityEngine;

namespace Feeder
{
    public sealed class FCharacterMeshUpdateTool : FBaseTool
    {
        public enum CollectMode
        {
            [LabelText("Skinned Mesh Only")]
            SkinnedMeshOnly,
            [LabelText("Mesh Renderer Only")]
            MeshRendererOnly,
            [LabelText("Both")]
            Both
        }

        public enum ActiveScope
        {
            [LabelText("All (include inactive)")]
            All,
            [LabelText("Active Only")]
            ActiveOnly
        }

        public enum DuplicateNameHandling
        {
            [LabelText("Keep All")]
            KeepAll,
            [LabelText("Exclude Duplicate Names")]
            ExcludeDuplicates
        }

        protected override string GetDescription()
        {
            return "Chuyển SkinnedMeshRenderer / MeshRenderer từ armature cũ sang armature mới, giữ nguyên hierarchy gốc. Kéo GameObject nguồn vào Source, chọn Armature đích và Parent, bấm Preview rồi Transfer.";
        }

        private const string PrefsOwner = nameof(FCharacterMeshUpdateTool);

        // ── Source ──────────────────────────────────────────────────────────

        [Title("Source")]
        [LabelText("Source GameObjects")]
        [Tooltip("Kéo và thả các GameObject chứa renderer cần chuyển vào đây")]
        [ListDrawerSettings(ShowFoldout = true, DraggableItems = true, ShowIndexLabels = true, NumberOfItemsPerPage = 10)]
        [ShowInInspector]
        public List<GameObject> sourceGameObjects
        {
            get => FDataPersistenceService.GetOrCreateDataContainer().CharacterMeshSources;
            set
            {
                var c = FDataPersistenceService.GetOrCreateDataContainer();
                c.CharacterMeshSources.Clear();
                if (value != null)
                    c.CharacterMeshSources.AddRange(value);
                FDataPersistenceService.SaveData(c);
            }
        }

        // ── Find Options ─────────────────────────────────────────────────────

        [PropertySpace(SpaceBefore = 4)]
        [Title("Find Options")]
        [LabelText("Collect Mode")]
        [Tooltip("Loại renderer cần thu thập từ Source")]
        [ShowInInspector]
        public CollectMode collectMode
        {
            get => FToolPrefs.GetEnum(PrefsOwner, nameof(collectMode), CollectMode.Both);
            set => FToolPrefs.SetEnum(PrefsOwner, nameof(collectMode), value);
        }

        [LabelText("Active Scope")]
        [Tooltip("All = lấy cả inactive; Active Only = chỉ GameObject đang active trong hierarchy")]
        [ShowInInspector]
        public ActiveScope activeScope
        {
            get => FToolPrefs.GetEnum(PrefsOwner, nameof(activeScope), ActiveScope.All);
            set => FToolPrefs.SetEnum(PrefsOwner, nameof(activeScope), value);
        }

        [LabelText("Duplicate Names")]
        [Tooltip("Exclude Duplicate Names = nhiều renderer cùng tên thì chỉ giữ cái đầu tiên")]
        [ShowInInspector]
        public DuplicateNameHandling duplicateNameHandling
        {
            get => FToolPrefs.GetEnum(PrefsOwner, nameof(duplicateNameHandling), DuplicateNameHandling.KeepAll);
            set => FToolPrefs.SetEnum(PrefsOwner, nameof(duplicateNameHandling), value);
        }

        // ── Transfer Settings ────────────────────────────────────────────────

        [PropertySpace(SpaceBefore = 4)]
        [Title("Transfer Settings")]
        [LabelText("New Armature (Hips)")]
        [Tooltip("Root bone của armature đích — thường là bone Hips")]
        [Required]
        [InlineButton(nameof(UseSelectionAsNewArmature), "Use Selection")]
        [ShowInInspector]
        public Transform newArmature
        {
            get => FDataPersistenceService.GetOrCreateDataContainer().CharacterMeshNewArmature;
            set
            {
                var c = FDataPersistenceService.GetOrCreateDataContainer();
                c.CharacterMeshNewArmature = value;
                FDataPersistenceService.SaveData(c);
            }
        }

        [PropertySpace(SpaceBefore = 2)]
        [LabelText("New Parent")]
        [Tooltip("Transform sẽ trở thành parent của các renderer sau khi chuyển")]
        [Required]
        [InlineButton(nameof(UseSelectionAsNewParent), "Use Selection")]
        [ShowInInspector]
        public Transform newParent
        {
            get => FDataPersistenceService.GetOrCreateDataContainer().CharacterMeshNewParent;
            set
            {
                var c = FDataPersistenceService.GetOrCreateDataContainer();
                c.CharacterMeshNewParent = value;
                FDataPersistenceService.SaveData(c);
            }
        }

        private void UseSelectionAsNewArmature()
        {
            if (Selection.activeTransform != null)
                newArmature = Selection.activeTransform;
            else
                Debug.LogWarning("[FCharacterMeshUpdateTool] Chọn một Transform trong Hierarchy trước.");
        }

        private void UseSelectionAsNewParent()
        {
            if (Selection.activeTransform != null)
                newParent = Selection.activeTransform;
            else
                Debug.LogWarning("[FCharacterMeshUpdateTool] Chọn một Transform trong Hierarchy trước.");
        }

        // ── Guide ────────────────────────────────────────────────────────────

        [OnInspectorGUI]
        private void DrawGuide()
        {
            GUILayout.Space(6);
            FStylesUtils.DrawInfoBox(
                "Source GameObjects   root chứa renderer cần chuyển\n" +
                "Collect Mode         Skinned Only / Mesh Renderer Only / Both\n" +
                "Active Scope         All hoặc chỉ GameObject đang active\n" +
                "Duplicate Names      giữ hết hay bỏ trùng tên (giữ cái đầu)\n" +
                "New Armature         bone Hips của armature đích\n" +
                "New Parent           transform parent sau khi chuyển\n" +
                "Hierarchy            giữ nguyên cấu trúc cha-con từ source\n" +
                "Preview → Transfer   xem trước rồi thực hiện"
            );
            GUILayout.Space(4);
        }

        // ── Preview ──────────────────────────────────────────────────────────

        [Title("Preview")]
        [LabelText("Found Skinned Mesh Renderers")]
        [ReadOnly]
        [ListDrawerSettings(ShowFoldout = true, DraggableItems = false, NumberOfItemsPerPage = 10)]
        public SkinnedMeshRenderer[] foundSkinnedMeshRenderers = new SkinnedMeshRenderer[0];

        [LabelText("Found Mesh Renderers")]
        [ReadOnly]
        [ListDrawerSettings(ShowFoldout = true, DraggableItems = false, NumberOfItemsPerPage = 10)]
        public MeshRenderer[] foundMeshRenderers = new MeshRenderer[0];

        // ── Actions ──────────────────────────────────────────────────────────

        [PropertySpace(SpaceBefore = 6)]
        [Button("Preview Find Mesh", ButtonSizes.Medium)]
        [GUIColor(0.6f, 1f, 0.6f)]
        private void PreviewFindMesh()
        {
            CollectRenderersFromSources();
            Debug.Log($"[CharacterMeshUpdate] Preview: {foundSkinnedMeshRenderers.Length} SkinnedMeshRenderer(s), {foundMeshRenderers.Length} MeshRenderer(s).");
        }

        [PropertySpace(SpaceBefore = 2)]
        [Button("Transfer Meshes", ButtonSizes.Large)]
        [GUIColor(0.4f, 0.8f, 1f)]
        [EnableIf("@newArmature != null && newParent != null && sourceGameObjects != null && sourceGameObjects.Count > 0")]
        private void TransferMeshes()
        {
            if (sourceGameObjects == null || sourceGameObjects.Count == 0)
                throw new System.InvalidOperationException("Source GameObjects is empty.");
            if (newArmature == null)
                throw new System.InvalidOperationException("New Armature is not assigned.");
            if (newParent == null)
                throw new System.InvalidOperationException("New Parent is not assigned.");

            CollectRenderersFromSources();

            Transform[] armatureMap = BuildArmatureMap(newArmature);
            int successCount = 0;
            int failCount = 0;

            Undo.SetCurrentGroupName("Transfer Meshes");
            int undoGroup = Undo.GetCurrentGroup();

            HashSet<Transform> resolvedMoveRoots = new HashSet<Transform>();
            List<SkinnedMeshRenderer> skinnedRenderersToRemap = new List<SkinnedMeshRenderer>();

            foreach (SkinnedMeshRenderer smr in foundSkinnedMeshRenderers)
            {
                if (smr == null) continue;
                try
                {
                    Transform moveRoot = ResolveHierarchyMoveRoot(smr.transform);
                    resolvedMoveRoots.Add(moveRoot);
                    skinnedRenderersToRemap.Add(smr);
                }
                catch (System.Exception e)
                {
                    Debug.LogError($"[CharacterMeshUpdate] Failed to resolve hierarchy for '{smr.name}': {e.Message}");
                    failCount++;
                }
            }

            List<Transform> outerMoveRoots = FilterOutNestedMoveRoots(resolvedMoveRoots);
            foreach (Transform moveRoot in outerMoveRoots)
            {
                Undo.RecordObject(moveRoot, "Transfer Hierarchy");
                moveRoot.SetParent(newParent, false);
                moveRoot.localPosition = Vector3.zero;
            }

            foreach (SkinnedMeshRenderer smr in skinnedRenderersToRemap)
            {
                try
                {
                    RemapSkinnedMeshRendererBones(smr, armatureMap);
                    successCount++;
                }
                catch (System.Exception e)
                {
                    Debug.LogError($"[CharacterMeshUpdate] Failed to transfer '{smr.name}': {e.Message}");
                    failCount++;
                }
            }

            foreach (MeshRenderer mr in foundMeshRenderers)
            {
                if (mr == null) continue;
                try
                {
                    TransferMeshRenderer(mr, armatureMap);
                    successCount++;
                }
                catch (System.Exception e)
                {
                    Debug.LogError($"[CharacterMeshUpdate] Failed to transfer '{mr.name}': {e.Message}");
                    failCount++;
                }
            }

            Undo.CollapseUndoOperations(undoGroup);
            EditorUtility.SetDirty(newParent);

            if (failCount == 0)
                Debug.Log($"<color=green>[CharacterMeshUpdate] Transferred {successCount} renderer(s), moved {outerMoveRoots.Count} hierarchy subtree(s).</color>");
            else
                Debug.LogWarning($"[CharacterMeshUpdate] Transferred {successCount} renderer(s). Failed: {failCount}. See above errors for details.");
        }

        [PropertySpace(SpaceBefore = 2)]
        [Button("Clear All", ButtonSizes.Medium)]
        [GUIColor(1f, 0.6f, 0.6f)]
        private void ClearAll()
        {
            sourceGameObjects = new List<GameObject>();
            collectMode = CollectMode.Both;
            activeScope = ActiveScope.All;
            duplicateNameHandling = DuplicateNameHandling.KeepAll;
            foundSkinnedMeshRenderers = new SkinnedMeshRenderer[0];
            foundMeshRenderers = new MeshRenderer[0];
            newArmature = null;
            newParent = null;
        }

        // ── Private helpers ──────────────────────────────────────────────────

        private void CollectRenderersFromSources()
        {
            IEnumerable<GameObject> sources = sourceGameObjects.Where(s => s != null);

            SkinnedMeshRenderer[] skinnedRenderers;
            MeshRenderer[] meshRenderers;

            skinnedRenderers = collectMode == CollectMode.MeshRendererOnly
                ? new SkinnedMeshRenderer[0]
                : sources.SelectMany(s => s.GetComponentsInChildren<SkinnedMeshRenderer>(true)).Distinct().ToArray();

            meshRenderers = collectMode == CollectMode.SkinnedMeshOnly
                ? new MeshRenderer[0]
                : sources.SelectMany(s => s.GetComponentsInChildren<MeshRenderer>(true)).Distinct().ToArray();

            if (activeScope == ActiveScope.ActiveOnly)
            {
                skinnedRenderers = skinnedRenderers.Where(r => r?.gameObject.activeInHierarchy == true).ToArray();
                meshRenderers = meshRenderers.Where(r => r?.gameObject.activeInHierarchy == true).ToArray();
            }

            if (duplicateNameHandling == DuplicateNameHandling.ExcludeDuplicates)
            {
                skinnedRenderers = RemoveDuplicatesByName(skinnedRenderers);
                meshRenderers = RemoveDuplicatesByName(meshRenderers);
            }

            foundSkinnedMeshRenderers = skinnedRenderers;
            foundMeshRenderers = meshRenderers;
        }

        private static T[] RemoveDuplicatesByName<T>(T[] renderers) where T : Renderer
        {
            HashSet<string> seen = new HashSet<string>();
            return renderers.Where(r => r != null && seen.Add(r.name)).ToArray();
        }

        private static Transform[] BuildArmatureMap(Transform armatureRoot)
        {
            return armatureRoot.GetComponentsInChildren<Transform>(true);
        }

        private Transform ResolveHierarchyMoveRoot(Transform rendererTransform)
        {
            foreach (GameObject source in sourceGameObjects)
            {
                if (source == null) continue;
                if (!rendererTransform.IsChildOf(source.transform)) continue;

                if (rendererTransform == source.transform)
                    return rendererTransform;

                return FHierarchyPathResolver.FindDirectChildOfAncestor(source.transform, rendererTransform);
            }

            throw new System.InvalidOperationException(
                $"Renderer '{rendererTransform.name}' not found in any source.");
        }

        private static List<Transform> FilterOutNestedMoveRoots(HashSet<Transform> moveRoots)
        {
            List<Transform> outerRoots = new List<Transform>(moveRoots.Count);
            foreach (Transform candidate in moveRoots)
            {
                bool nestedUnderAnotherMoveRoot = false;
                foreach (Transform other in moveRoots)
                {
                    if (other == candidate) continue;
                    if (candidate.IsChildOf(other))
                    {
                        nestedUnderAnotherMoveRoot = true;
                        break;
                    }
                }

                if (!nestedUnderAnotherMoveRoot)
                    outerRoots.Add(candidate);
            }

            return outerRoots;
        }

        private static void RemapSkinnedMeshRendererBones(
            SkinnedMeshRenderer smr,
            Transform[] armatureMap)
        {
            string rootBoneName = smr.rootBone?.name;
            if (string.IsNullOrEmpty(rootBoneName))
                throw new System.Exception("Root bone is missing.");

            Transform[] newBones = new Transform[smr.bones.Length];
            for (int i = 0; i < smr.bones.Length; i++)
            {
                string boneName = smr.bones[i]?.name;
                if (string.IsNullOrEmpty(boneName))
                    throw new System.Exception($"Bone at index {i} is missing.");

                Transform newBone = armatureMap.FirstOrDefault(t => t.name == boneName);
                if (newBone == null)
                    throw new System.Exception($"Bone not found in new armature: '{boneName}'");

                newBones[i] = newBone;
            }

            Transform matchingRootBone = armatureMap.FirstOrDefault(t => t.name == rootBoneName);
            if (matchingRootBone == null)
                throw new System.Exception($"Root bone not found in new armature: '{rootBoneName}'");

            Undo.RecordObject(smr, "Remap Bones");
            smr.rootBone = matchingRootBone;
            smr.bones = newBones;
        }

        private static void TransferMeshRenderer(MeshRenderer mr, Transform[] armatureMap)
        {
            string parentName = mr.transform.parent?.name;
            if (string.IsNullOrEmpty(parentName))
                throw new System.Exception("Parent bone is missing.");

            Transform targetParent = armatureMap.FirstOrDefault(t => t.name == parentName);
            if (targetParent == null)
                throw new System.Exception($"Parent bone not found in new armature: '{parentName}'");

            Undo.RecordObject(mr.transform, "Transfer MeshRenderer");
            mr.transform.SetParent(targetParent, false);
        }
    }
}
