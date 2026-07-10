using System;
using System.Collections.Generic;
using System.IO;
using Sirenix.OdinInspector;
using Sirenix.Serialization;
using UnityEditor;
using UnityEditor.Animations;
using UnityEngine;

namespace Feeder
{
    public sealed class FUnpackAnimationTool : FTargetPrefabsToolBase
    {
        private const string CommonFolderName = "_Common";
        private const string AnimationsFolderName = "Animations";
        private const string ControllersFolderName = "Controllers";

        protected override string GetDescription()
        {
            return "Tách AnimationClip và AnimatorController/Override ra thư mục riêng rồi gán controller mới ngược lại vào Animator. Sau khi unpack có thể chỉnh/xóa nguồn gốc (vd clip trong FBX) mà scene không thay đổi.";
        }

        [Title("Settings")]
        [LabelText("Save Folder")]
        [FolderPath(AbsolutePath = false, RequireExistingPath = true)]
        [ShowInInspector, OdinSerialize]
        private string saveFolderPath;

        [LabelText("Include Child Animators")]
        [ShowInInspector, OdinSerialize]
        private bool includeChildAnimators = true;

        [LabelText("Skip Disabled GameObjects")]
        [ShowInInspector, OdinSerialize]
        private bool skipDisabledGameObjects = false;

        [OnInspectorGUI]
        private void DrawGuide()
        {
            GUILayout.Space(2);
            FStylesUtils.DrawInfoBox(
                "TargetPrefabs            root chứa Animator cần unpack\n" +
                "Include Child Animators  tìm Animator ở cả object con\n" +
                "mỗi target → Animations, Controllers (clip + controller dùng chung → _Common)\n" +
                "controller mới trỏ tới clip mới và được gán lại vào Animator"
            );
            GUILayout.Space(4);
        }

        private struct AnimatorEntry
        {
            public Animator Animator;
            public string TargetRootName;
            public RuntimeAnimatorController SourceController;
        }

        [Button(ButtonSizes.Large), GUIColor(0.3f, 0.8f, 1f)]
        public void Unpack()
        {
            ValidateInput();

            var clipToTargetNames = new Dictionary<AnimationClip, HashSet<string>>();
            var controllerToTargetNames = new Dictionary<RuntimeAnimatorController, HashSet<string>>();

            List<AnimatorEntry> entries = CollectEntries(clipToTargetNames, controllerToTargetNames);
            if (entries.Count == 0)
            {
                Debug.LogWarning("[FUnpackAnimationTool] No Animator with a controller found to unpack.");
                return;
            }

            EnsureFolderStructure(CollectAllTargetNames());

            // ── Phase 1: clips ─────────────────────────────────────────────
            // Create/Copy → refresh → load by path.
            var clipCopyPaths = new Dictionary<string, AnimationClip>(); // newPath → source (CopyAsset)
            var clipMap = new Dictionary<AnimationClip, AnimationClip>(); // source → new

            AssetDatabase.StartAssetEditing();
            try { CreateAllClipAssets(clipToTargetNames, clipMap, clipCopyPaths); }
            finally { AssetDatabase.StopAssetEditing(); AssetDatabase.SaveAssets(); AssetDatabase.Refresh(); }

            LoadCopiedClipsAfterRefresh(clipCopyPaths, clipMap);

            // ── Phase 2: controllers ───────────────────────────────────────
            // Copy → refresh → load by path, then remap clip references on the copies.
            var controllerPaths = new Dictionary<RuntimeAnimatorController, string>(); // source → newPath

            AssetDatabase.StartAssetEditing();
            try { CreateAllControllerAssets(controllerToTargetNames, controllerPaths); }
            finally { AssetDatabase.StopAssetEditing(); AssetDatabase.SaveAssets(); AssetDatabase.Refresh(); }

            var controllerMap = LoadControllersAfterRefresh(controllerPaths);
            RemapAllControllers(controllerMap, clipMap);
            AssetDatabase.SaveAssets();

            // ── Phase 3: assign to animators ───────────────────────────────
            // Done OUTSIDE any StartAssetEditing so scene modifications apply immediately
            // (also fixes assignment on inactive GameObjects).
            AssignToAnimators(entries, controllerMap);
            AssetDatabase.SaveAssets();

            Debug.Log($"<color=green>[FUnpackAnimationTool] Done. {entries.Count} animator(s), {clipMap.Count} clip(s), {controllerMap.Count} controller(s) unpacked.</color>");
        }

        // ══════════════════════════════════════════════════════════════════
        // Collect
        // ══════════════════════════════════════════════════════════════════

        private HashSet<string> CollectAllTargetNames()
        {
            var names = new HashSet<string>();
            foreach (GameObject target in TargetPrefabs)
                if (target != null)
                    names.Add(ValidateAssetName(target.name));
            return names;
        }

        private List<AnimatorEntry> CollectEntries(
            Dictionary<AnimationClip, HashSet<string>> clipToTargetNames,
            Dictionary<RuntimeAnimatorController, HashSet<string>> controllerToTargetNames)
        {
            var entries = new List<AnimatorEntry>();

            for (int i = 0; i < TargetPrefabs.Count; i++)
            {
                GameObject target = TargetPrefabs[i];
                if (target == null)
                {
                    Debug.LogWarning($"[FUnpackAnimationTool] Skipping null at TargetPrefabs[{i}].");
                    continue;
                }

                if (PrefabUtility.IsPartOfPrefabAsset(target))
                    throw new InvalidOperationException($"prefab asset is not supported: {target.name}");

                string targetName = ValidateAssetName(target.name);

                Animator[] animators = includeChildAnimators
                    ? target.GetComponentsInChildren<Animator>(true)
                    : target.GetComponents<Animator>();

                if (target.GetComponent<Animation>() != null || target.GetComponentInChildren<Animation>(true) != null)
                    Debug.Log($"[FUnpackAnimationTool] '{target.name}' has a legacy Animation component which is not supported (Animator only).");

                foreach (Animator animator in animators)
                {
                    GameObject child = animator.gameObject;
                    if (skipDisabledGameObjects && !child.activeInHierarchy) continue;

                    RuntimeAnimatorController controller = animator.runtimeAnimatorController;
                    if (controller == null)
                    {
                        Debug.LogWarning($"[FUnpackAnimationTool] Animator on '{child.name}' has no controller, skipping.");
                        continue;
                    }

                    AddToSet(controllerToTargetNames, controller, targetName);

                    // animationClips returns the effective clips for both AnimatorController and AnimatorOverrideController.
                    foreach (AnimationClip clip in controller.animationClips)
                        AddToSet(clipToTargetNames, clip, targetName);

                    entries.Add(new AnimatorEntry
                    {
                        Animator = animator,
                        TargetRootName = targetName,
                        SourceController = controller
                    });
                }
            }

            return entries;
        }

        // ══════════════════════════════════════════════════════════════════
        // Phase 1 – clips
        // ══════════════════════════════════════════════════════════════════

        // Fills clipMap immediately for the CreateAsset path; fills clipCopyPaths for the
        // CopyAsset path (objects not available until after Refresh).
        private void CreateAllClipAssets(
            Dictionary<AnimationClip, HashSet<string>> clipToTargetNames,
            Dictionary<AnimationClip, AnimationClip> clipMap,
            Dictionary<string, AnimationClip> clipCopyPaths)
        {
            foreach (AnimationClip sourceClip in clipToTargetNames.Keys)
            {
                if (sourceClip == null) continue;

                HashSet<string> targetNames = clipToTargetNames[sourceClip];
                string firstTarget = GetFirstTargetName(targetNames);
                string folder = GetAssetFolder(firstTarget, targetNames, AnimationsFolderName);
                string assetName = ValidateAssetName(string.IsNullOrWhiteSpace(sourceClip.name) ? "Clip" : sourceClip.name);

                string sourcePath = AssetDatabase.GetAssetPath(sourceClip);
                bool isMainAsset = !string.IsNullOrEmpty(sourcePath)
                    && Path.GetExtension(sourcePath).Equals(".anim", StringComparison.OrdinalIgnoreCase)
                    && AssetDatabase.LoadAssetAtPath<AnimationClip>(sourcePath) == sourceClip;

                if (isMainAsset)
                {
                    string destPath = AssetDatabase.GenerateUniqueAssetPath($"{folder}/{assetName}.anim");
                    if (AssetDatabase.CopyAsset(sourcePath, destPath))
                    {
                        clipCopyPaths[destPath] = sourceClip;
                        continue;
                    }
                }

                // Sub-asset (e.g. clip embedded in an FBX) or CopyAsset failed: deep-copy into a standalone .anim.
                var newClip = new AnimationClip();
                EditorUtility.CopySerialized(sourceClip, newClip);
                newClip.name = assetName;
                string clipPath = AssetDatabase.GenerateUniqueAssetPath($"{folder}/{assetName}.anim");
                AssetDatabase.CreateAsset(newClip, clipPath);
                clipMap[sourceClip] = newClip;
            }
        }

        private static void LoadCopiedClipsAfterRefresh(
            Dictionary<string, AnimationClip> clipCopyPaths,
            Dictionary<AnimationClip, AnimationClip> clipMap)
        {
            foreach (KeyValuePair<string, AnimationClip> kv in clipCopyPaths)
            {
                AnimationClip loaded = AssetDatabase.LoadAssetAtPath<AnimationClip>(kv.Key);
                if (loaded == null)
                {
                    Debug.LogWarning($"[FUnpackAnimationTool] Failed to load clip after refresh: {kv.Key}");
                    continue;
                }
                clipMap[kv.Value] = loaded;
            }
        }

        // ══════════════════════════════════════════════════════════════════
        // Phase 2 – controllers
        // ══════════════════════════════════════════════════════════════════

        private void CreateAllControllerAssets(
            Dictionary<RuntimeAnimatorController, HashSet<string>> controllerToTargetNames,
            Dictionary<RuntimeAnimatorController, string> controllerPaths)
        {
            foreach (RuntimeAnimatorController sourceController in controllerToTargetNames.Keys)
            {
                if (sourceController == null) continue;

                HashSet<string> targetNames = controllerToTargetNames[sourceController];
                string firstTarget = GetFirstTargetName(targetNames);
                string folder = GetAssetFolder(firstTarget, targetNames, ControllersFolderName);
                string assetName = ValidateAssetName(string.IsNullOrWhiteSpace(sourceController.name) ? "Controller" : sourceController.name);

                string sourcePath = AssetDatabase.GetAssetPath(sourceController);
                if (string.IsNullOrEmpty(sourcePath))
                {
                    Debug.LogWarning($"[FUnpackAnimationTool] Controller '{sourceController.name}' is not an asset, skipping.");
                    continue;
                }

                string ext = Path.GetExtension(sourcePath); // .controller or .overrideController
                string destPath = AssetDatabase.GenerateUniqueAssetPath($"{folder}/{assetName}{ext}");
                if (AssetDatabase.CopyAsset(sourcePath, destPath))
                    controllerPaths[sourceController] = destPath;
                else
                    Debug.LogWarning($"[FUnpackAnimationTool] Failed to copy controller: {sourcePath}");
            }
        }

        private static Dictionary<RuntimeAnimatorController, RuntimeAnimatorController> LoadControllersAfterRefresh(
            Dictionary<RuntimeAnimatorController, string> controllerPaths)
        {
            var map = new Dictionary<RuntimeAnimatorController, RuntimeAnimatorController>();
            foreach (KeyValuePair<RuntimeAnimatorController, string> kv in controllerPaths)
            {
                var loaded = AssetDatabase.LoadAssetAtPath<RuntimeAnimatorController>(kv.Value);
                if (loaded == null)
                    Debug.LogWarning($"[FUnpackAnimationTool] Failed to load controller after refresh: {kv.Value}");
                else
                    map[kv.Key] = loaded;
            }
            return map;
        }

        private static void RemapAllControllers(
            Dictionary<RuntimeAnimatorController, RuntimeAnimatorController> controllerMap,
            Dictionary<AnimationClip, AnimationClip> clipMap)
        {
            foreach (RuntimeAnimatorController copy in controllerMap.Values)
            {
                switch (copy)
                {
                    case AnimatorController animatorController:
                        RemapAnimatorController(animatorController, clipMap);
                        break;
                    case AnimatorOverrideController overrideController:
                        RemapOverrideController(overrideController, clipMap);
                        break;
                }
            }
        }

        private static void RemapAnimatorController(AnimatorController controller, Dictionary<AnimationClip, AnimationClip> clipMap)
        {
            bool changed = false;
            foreach (AnimatorControllerLayer layer in controller.layers)
                changed |= RemapStateMachine(layer.stateMachine, clipMap);
            if (changed)
                EditorUtility.SetDirty(controller);
        }

        private static bool RemapStateMachine(AnimatorStateMachine stateMachine, Dictionary<AnimationClip, AnimationClip> clipMap)
        {
            if (stateMachine == null) return false;
            bool changed = false;

            foreach (ChildAnimatorState childState in stateMachine.states)
            {
                Motion remapped = RemapMotion(childState.state.motion, clipMap, ref changed);
                if (!ReferenceEquals(remapped, childState.state.motion))
                {
                    childState.state.motion = remapped;
                    changed = true;
                }
            }

            foreach (ChildAnimatorStateMachine childSm in stateMachine.stateMachines)
                changed |= RemapStateMachine(childSm.stateMachine, clipMap);

            return changed;
        }

        private static Motion RemapMotion(Motion motion, Dictionary<AnimationClip, AnimationClip> clipMap, ref bool changed)
        {
            switch (motion)
            {
                case AnimationClip clip when clipMap.TryGetValue(clip, out AnimationClip newClip) && newClip != null:
                    return newClip;
                case BlendTree tree:
                    ChildMotion[] children = tree.children;
                    bool treeChanged = false;
                    for (int i = 0; i < children.Length; i++)
                    {
                        Motion remapped = RemapMotion(children[i].motion, clipMap, ref changed);
                        if (!ReferenceEquals(remapped, children[i].motion))
                        {
                            children[i].motion = remapped;
                            treeChanged = true;
                        }
                    }
                    if (treeChanged)
                    {
                        tree.children = children;
                        changed = true;
                    }
                    return tree;
                default:
                    return motion;
            }
        }

        private static void RemapOverrideController(AnimatorOverrideController controller, Dictionary<AnimationClip, AnimationClip> clipMap)
        {
            var overrides = new List<KeyValuePair<AnimationClip, AnimationClip>>(controller.overridesCount);
            controller.GetOverrides(overrides);

            bool changed = false;
            for (int i = 0; i < overrides.Count; i++)
            {
                AnimationClip key = overrides[i].Key;
                AnimationClip current = overrides[i].Value;
                // The effective clip is the override value if present, otherwise the base clip (key).
                AnimationClip effective = current != null ? current : key;
                if (effective != null && clipMap.TryGetValue(effective, out AnimationClip newClip) && newClip != null)
                {
                    overrides[i] = new KeyValuePair<AnimationClip, AnimationClip>(key, newClip);
                    changed = true;
                }
            }

            if (changed)
            {
                controller.ApplyOverrides(overrides);
                EditorUtility.SetDirty(controller);
            }
        }

        // ══════════════════════════════════════════════════════════════════
        // Phase 3 – assign (outside any asset-editing batch)
        // ══════════════════════════════════════════════════════════════════

        private void AssignToAnimators(
            List<AnimatorEntry> entries,
            Dictionary<RuntimeAnimatorController, RuntimeAnimatorController> controllerMap)
        {
            var processedAnimators = new HashSet<Animator>();

            foreach (AnimatorEntry entry in entries)
            {
                if (!processedAnimators.Add(entry.Animator)) continue;

                if (!controllerMap.TryGetValue(entry.SourceController, out RuntimeAnimatorController newController) || newController == null)
                {
                    Debug.LogWarning($"[FUnpackAnimationTool] No new controller for '{entry.Animator.name}', skipping assignment.");
                    continue;
                }

                Undo.RecordObject(entry.Animator, "Unpack Animation");
                entry.Animator.runtimeAnimatorController = newController;
                if (PrefabUtility.IsPartOfPrefabInstance(entry.Animator.gameObject))
                    PrefabUtility.RecordPrefabInstancePropertyModifications(entry.Animator);
                EditorUtility.SetDirty(entry.Animator);
            }
        }

        // ══════════════════════════════════════════════════════════════════
        // Folder structure
        // ══════════════════════════════════════════════════════════════════

        private void EnsureFolderStructure(HashSet<string> allTargetNames)
        {
            EnsureFolder(saveFolderPath, CommonFolderName);
            string commonPath = $"{saveFolderPath}/{CommonFolderName}";
            EnsureFolder(commonPath, AnimationsFolderName);
            EnsureFolder(commonPath, ControllersFolderName);

            foreach (string targetName in allTargetNames)
            {
                EnsureFolder(saveFolderPath, targetName);
                string targetPath = $"{saveFolderPath}/{targetName}";
                EnsureFolder(targetPath, AnimationsFolderName);
                EnsureFolder(targetPath, ControllersFolderName);
            }
        }

        private static void EnsureFolder(string parentPath, string folderName)
        {
            string childPath = $"{parentPath}/{folderName}";
            if (AssetDatabase.IsValidFolder(childPath)) return;
            string guid = AssetDatabase.CreateFolder(parentPath, folderName);
            if (string.IsNullOrEmpty(guid))
                throw new InvalidOperationException($"failed to create folder: {childPath}");
        }

        // ══════════════════════════════════════════════════════════════════
        // Generic helpers
        // ══════════════════════════════════════════════════════════════════

        private static void AddToSet<T>(Dictionary<T, HashSet<string>> map, T key, string targetName)
            where T : UnityEngine.Object
        {
            if (key == null) return;
            if (!map.TryGetValue(key, out HashSet<string> set))
            {
                set = new HashSet<string>();
                map.Add(key, set);
            }
            set.Add(targetName);
        }

        private string GetAssetFolder(string assetTargetName, HashSet<string> targetNames, string subFolder)
        {
            if (targetNames.Count > 1)
                return $"{saveFolderPath}/{CommonFolderName}/{subFolder}";
            return $"{saveFolderPath}/{assetTargetName}/{subFolder}";
        }

        private static string GetFirstTargetName(HashSet<string> targetNames)
        {
            foreach (string n in targetNames) return n;
            throw new InvalidOperationException("target names set is empty.");
        }

        private void ValidateInput()
        {
            if (TargetPrefabs == null || TargetPrefabs.Count == 0)
                throw new InvalidOperationException("target objects is empty.");
            if (string.IsNullOrWhiteSpace(saveFolderPath) || !AssetDatabase.IsValidFolder(saveFolderPath))
                throw new InvalidOperationException("save folder path is invalid.");
        }

        private static string ValidateAssetName(string name)
        {
            if (string.IsNullOrWhiteSpace(name))
                return "Unknown";
            foreach (char ch in Path.GetInvalidFileNameChars())
                name = name.Replace(ch, '_');
            name = name.Trim();
            return string.IsNullOrWhiteSpace(name) ? "Unknown" : name;
        }
    }
}
