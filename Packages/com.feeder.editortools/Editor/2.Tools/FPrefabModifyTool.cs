using System;
using System.Collections.Generic;
using Sirenix.OdinInspector;
using UnityEditor;
using UnityEngine;

namespace Feeder
{
    public sealed class FPrefabModifyTool : FTargetPrefabsToolBase
    {
        protected override string GetDescription()
        {
            return "Chọn prefab/scene object, chọn node theo Hierarchy Path; tab Modify sửa Active/Layer/Tag/Transform của node rồi Apply cho mọi target, tab Delete xóa node khỏi mọi target.";
        }

        [LabelText("Hierarchy Path")]
        [ValueDropdown(nameof(GetHierarchyOptions), IsUniqueList = false, DropdownWidth = 400, DropdownHeight = 300, DrawDropdownForListElements = false)]
        [OnValueChanged(nameof(OnSelectedPathChanged))]
        public string SelectedHierarchyPath;

        [OnInspectorGUI]
        private void DrawHierarchyInfo()
        {
            if (!(TargetPrefabs?.Count > 0))
            {
                EditorGUILayout.HelpBox("Add prefabs to show hierarchy.", MessageType.Info);
                return;
            }

            if (hierarchyOptions.Count == 0)
            {
                EditorGUILayout.HelpBox("No hierarchy data for current targets.", MessageType.Info);
                return;
            }

            if (cachedPrefabCount < 2)
            {
                EditorGUILayout.HelpBox("Add at least two prefabs to show conflicts.", MessageType.Info);
            }

            if (conflictPaths.Count > 0)
            {
                EditorGUILayout.HelpBox("Items marked [Conflict] differ between prefabs; targets missing the node are skipped on Apply/Delete.", MessageType.Warning);
            }
        }

        [TabGroup("Tabs", "Modify")]
        [CustomValueDrawer(nameof(DrawModifyHeader))]
        public string ModifyHeader = "";

        private readonly HashSet<string> modifiedPropertyPaths = new HashSet<string>();
        private readonly PrefabNodePreviewHandle previewHandle = new PrefabNodePreviewHandle();
        private Vector3 previewEuler;

        [TabGroup("Tabs", "Modify")]
        [OnInspectorGUI]
        private void DrawNodeStateWithTracking()
        {
            if (string.IsNullOrEmpty(SelectedHierarchyPath))
            {
                EditorGUILayout.HelpBox("Select a hierarchy path above to begin editing.", MessageType.Info);
                return;
            }

            if (!previewHandle.IsReady)
            {
                EditorGUILayout.HelpBox("Preview chưa load được node (path không tồn tại trong target nào?).", MessageType.Warning);
                return;
            }

            EditorGUILayout.BeginHorizontal();
            EditorGUILayout.LabelField($"Modified Fields: {modifiedPropertyPaths.Count}", EditorStyles.boldLabel);
            if (modifiedPropertyPaths.Count > 0 && GUILayout.Button("Clear All", GUILayout.Width(80)))
            {
                previewHandle.RestoreFromOriginal(modifiedPropertyPaths);
                previewEuler = previewHandle.PreviewGo.transform.localEulerAngles;
            }
            EditorGUILayout.EndHorizontal();

            EditorGUILayout.Space(5);

            SerializedObject goSo = new SerializedObject(previewHandle.PreviewGo);
            SerializedObject trSo = new SerializedObject(previewHandle.PreviewGo.transform);
            goSo.Update();
            trSo.Update();

            DrawTrackedRow(FPrefabBatchOperations.KeyIsActive, () =>
            {
                SerializedProperty p = goSo.FindProperty(FPrefabBatchOperations.KeyIsActive);
                p.boolValue = EditorGUILayout.Toggle("Active", p.boolValue);
            });

            DrawTrackedRow(FPrefabBatchOperations.KeyLayer, () =>
            {
                SerializedProperty p = goSo.FindProperty(FPrefabBatchOperations.KeyLayer);
                p.intValue = EditorGUILayout.LayerField("Layer", p.intValue);
            });

            DrawTrackedRow(FPrefabBatchOperations.KeyTag, () =>
            {
                SerializedProperty p = goSo.FindProperty(FPrefabBatchOperations.KeyTag);
                p.stringValue = EditorGUILayout.TagField("Tag", p.stringValue);
            });

            DrawTrackedRow(FPrefabBatchOperations.KeyLocalPosition, () =>
            {
                SerializedProperty p = trSo.FindProperty(FPrefabBatchOperations.KeyLocalPosition);
                p.vector3Value = EditorGUILayout.Vector3Field("Local Position", p.vector3Value);
            });

            // rotation edits go through an euler cache; round-tripping quaternion->euler each frame
            // causes value jumps (180 -> 179.9999) and phantom dirty marks
            DrawTrackedRow(FPrefabBatchOperations.KeyLocalRotation, () =>
            {
                Vector3 newEuler = EditorGUILayout.Vector3Field("Local Rotation", previewEuler);
                if (newEuler != previewEuler)
                {
                    previewEuler = newEuler;
                    trSo.FindProperty(FPrefabBatchOperations.KeyLocalRotation).quaternionValue = Quaternion.Euler(newEuler);
                }
            });

            DrawTrackedRow(FPrefabBatchOperations.KeyLocalScale, () =>
            {
                SerializedProperty p = trSo.FindProperty(FPrefabBatchOperations.KeyLocalScale);
                p.vector3Value = EditorGUILayout.Vector3Field("Local Scale", p.vector3Value);
            });

            goSo.ApplyModifiedProperties();
            trSo.ApplyModifiedProperties();
        }

        private void DrawTrackedRow(string key, Action drawField)
        {
            bool wasModified = modifiedPropertyPaths.Contains(key);

            EditorGUILayout.BeginHorizontal();

            Color originalColor = GUI.color;
            if (wasModified)
            {
                GUI.color = Color.cyan;
                EditorGUILayout.LabelField("●", GUILayout.Width(14));
                GUI.color = originalColor;
            }
            else
            {
                GUILayout.Space(18);
            }

            EditorGUI.BeginChangeCheck();
            drawField();
            if (EditorGUI.EndChangeCheck())
            {
                modifiedPropertyPaths.Add(key);
            }

            EditorGUILayout.EndHorizontal();
        }

        [TabGroup("Tabs", "Modify")]
        [Button(ButtonSizes.Large)]
        public void ApplyModify()
        {
            if (!previewHandle.IsReady)
                throw new InvalidOperationException("preview is not ready; select a hierarchy path first.");

            if (modifiedPropertyPaths.Count == 0)
            {
                Debug.LogWarning("[FPrefabModifyTool] Chưa sửa field nào, không có gì để apply.");
                return;
            }

            int appliedCount = FPrefabBatchOperations.ApplyNodeStateToTargets(
                previewHandle.PreviewGo,
                SelectedHierarchyPath,
                modifiedPropertyPaths,
                TargetPrefabs);
            Debug.Log($"<color=cyan>Applied {modifiedPropertyPaths.Count} field(s) to node '{SelectedHierarchyPath}' on {appliedCount} target(s)</color>");
        }

        [TabGroup("Tabs", "Delete")]
        [CustomValueDrawer(nameof(DrawDeleteHeader))]
        public string DeleteHeader = "";

        [TabGroup("Tabs", "Delete")]
        [Button(ButtonSizes.Large)]
        public void DeleteNode()
        {
            if (string.IsNullOrEmpty(SelectedHierarchyPath))
                throw new InvalidOperationException("selected hierarchy path is empty.");
            if (!(TargetPrefabs?.Count > 0))
                throw new InvalidOperationException("target objects is empty.");

            bool confirmed = EditorUtility.DisplayDialog(
                "Delete Node",
                $"Xóa node '{SelectedHierarchyPath}' khỏi {TargetPrefabs.Count} target? Prefab asset sẽ bị sửa trực tiếp (không undo được).",
                "Delete",
                "Cancel");
            if (!confirmed)
                return;

            int deletedCount = FPrefabBatchOperations.DeleteNodeFromTargets(SelectedHierarchyPath, TargetPrefabs);
            Debug.Log($"<color=green>Deleted node '{SelectedHierarchyPath}' from {deletedCount} target(s)</color>");

            SelectedHierarchyPath = null;
            previewHandle.Cleanup();
            modifiedPropertyPaths.Clear();
            RebuildHierarchyOptions();
        }

        private string DrawModifyHeader(string value, GUIContent label)
        {
            return DrawTabHeader(
                value,
                "MODIFY NODE INFO",
                "Sửa Active, Layer, Tag, local Transform của node rồi Apply; chỉ field có chấm ● được ghi.",
                Color.cyan
            );
        }

        private string DrawDeleteHeader(string value, GUIContent label)
        {
            return DrawTabHeader(
                value,
                "DELETE NODE INFO",
                "Xóa node ở path đã chọn khỏi tất cả target (có confirm).",
                new Color(1f, 0.5f, 0f)
            );
        }

        private string DrawTabHeader(string value, string title, string description, Color color)
        {
            GUIStyle style = new GUIStyle(EditorStyles.label)
            {
                fontSize = 18,
                fontStyle = FontStyle.Bold
            };
            style.normal.textColor = color;

            GUILayout.Space(10);
            GUILayout.Label(title, style);

            GUIStyle subStyle = new GUIStyle(EditorStyles.wordWrappedLabel)
            {
                fontSize = 12
            };
            subStyle.normal.textColor = Color.gray;
            GUILayout.Label(description, subStyle);

            GUILayout.Space(10);
            return value;
        }

        private List<ValueDropdownItem<string>> hierarchyOptions = new List<ValueDropdownItem<string>>();
        private HashSet<string> conflictPaths = new HashSet<string>();
        private int cachedPrefabCount;

        private IEnumerable<ValueDropdownItem<string>> GetHierarchyOptions()
        {
            return hierarchyOptions;
        }

        private void RebuildHierarchyOptions()
        {
            if (!(TargetPrefabs?.Count > 0))
            {
                hierarchyOptions.Clear();
                conflictPaths.Clear();
                cachedPrefabCount = 0;
                return;
            }

            HierarchyOptionsResult result = FHierarchyOptionsBuilder.Build(TargetPrefabs);
            hierarchyOptions = result.Options;
            conflictPaths = result.ConflictPaths;
            cachedPrefabCount = result.PrefabCount;
        }

        protected override void OnTargetPrefabsChanged()
        {
            RebuildHierarchyOptions();
            ReseedPreviewForSelectedPath();
        }

        private void OnSelectedPathChanged()
        {
            ReseedPreviewForSelectedPath();
        }

        [OnInspectorInit]
        private void OnInspectorInit()
        {
            RebuildHierarchyOptions();
            ReseedPreviewForSelectedPath();
        }

        [OnInspectorDispose]
        private void OnInspectorDispose()
        {
            previewHandle.Cleanup();
        }

        // seed from the first target that has the node — conflict paths may be missing in the first target
        private void ReseedPreviewForSelectedPath()
        {
            if (string.IsNullOrEmpty(SelectedHierarchyPath) || !(TargetPrefabs?.Count > 0))
            {
                previewHandle.Cleanup();
                modifiedPropertyPaths.Clear();
                return;
            }

            for (int i = 0; i < TargetPrefabs.Count; i++)
            {
                GameObject go = TargetPrefabs[i];
                if (go == null)
                    continue;

                Transform rootTransform = FPrefabRootResolver.GetRootTransform(go, out GameObject prefabRoot, out bool shouldUnload);
                try
                {
                    Transform node = FHierarchyPathResolver.TryResolveTargetByPath(rootTransform, SelectedHierarchyPath);
                    if (node == null)
                    {
                        if (i == 0)
                            Debug.LogWarning($"[FPrefabModifyTool] Target đầu '{go.name}' không có node '{SelectedHierarchyPath}', seed từ target kế tiếp.");
                        continue;
                    }

                    previewHandle.ResetFrom(node.gameObject, modifiedPropertyPaths);
                    previewEuler = node.localEulerAngles;
                    return;
                }
                finally
                {
                    if (shouldUnload && prefabRoot != null)
                        PrefabUtility.UnloadPrefabContents(prefabRoot);
                }
            }

            previewHandle.Cleanup();
            modifiedPropertyPaths.Clear();
            Debug.LogWarning($"[FPrefabModifyTool] Không target nào có node '{SelectedHierarchyPath}'.");
        }
    }
}
