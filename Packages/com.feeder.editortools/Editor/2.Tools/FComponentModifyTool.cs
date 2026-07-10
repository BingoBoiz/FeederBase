using System;
using System.Collections.Generic;
using Sirenix.OdinInspector;
using Sirenix.Serialization;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;

namespace Feeder
{
    public sealed class FComponentModifyTool : FTargetPrefabsToolBase
    {
        protected override string GetDescription()
        {
            return "Chọn prefab/scene, chọn loại component; tab Add thêm component theo path, tab Find liệt kê mọi instance và nhảy tới Hierarchy, tab Modify sửa giá trị và Apply, tab Remove xóa component khỏi tất cả target.";
        }

        [LabelText("Target Component Type")]
        [ValueDropdown(nameof(GetComponentTypeOptions))]
        [OnValueChanged(nameof(OnComponentTypeChanged))]
        [ShowInInspector]
        private Type componentType;

        [TabGroup("Tabs", "Add")]
        [CustomValueDrawer(nameof(DrawAddHeader))]
        public string AddHeader = "";

        [TabGroup("Tabs", "Add")]
        [LabelText("Hierarchy Path")]
        [ValueDropdown(nameof(GetHierarchyOptions), IsUniqueList = false, DropdownWidth = 400, DropdownHeight = 300, DrawDropdownForListElements = false)]
        public string SelectedHierarchyPath;

        [TabGroup("Tabs", "Add")]
        [OnInspectorGUI]
        private void DrawPrefabHierarchy()
        {
            EditorGUILayout.Space(6);
            EditorGUILayout.LabelField("Prefab Hierarchy", EditorStyles.boldLabel);

            if (componentType == null)
            {
                EditorGUILayout.HelpBox("Select a component type above to build hierarchy paths.", MessageType.Info);
                return;
            }

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
                EditorGUILayout.HelpBox("Items marked [Conflict] differ between prefabs.", MessageType.Warning);
            }
        }

        [TabGroup("Tabs", "Add")]
        [Button(ButtonSizes.Large)]
        public void AddComponent()
        {
            Type selectedType = ComponentType;
            int addedCount = FComponentBatchOperations.AddComponentToTargets(selectedType, SelectedHierarchyPath, TargetPrefabs);
            Debug.Log($"<color=green>Added {addedCount} component(s) of type {selectedType.Name}</color>");
            RebuildComponentInstanceCache();
        }

        [TabGroup("Tabs", "Find")]
        [CustomValueDrawer(nameof(DrawFindHeader))]
        public string FindHeader = "";

        private readonly List<ComponentFindHit> cachedComponentHits = new List<ComponentFindHit>();
        private Vector2 findScroll;

        [TabGroup("Tabs", "Find")]
        [OnInspectorGUI]
        private void DrawFindTab()
        {
            if (componentType == null)
            {
                EditorGUILayout.HelpBox("Select a component type above to list instances.", MessageType.Info);
                return;
            }

            if (!(TargetPrefabs?.Count > 0))
            {
                EditorGUILayout.HelpBox("Add target prefabs or scene roots first.", MessageType.Info);
                return;
            }

            EditorGUILayout.LabelField($"Find: {cachedComponentHits.Count}", EditorStyles.boldLabel);

            if (cachedComponentHits.Count == 0)
            {
                EditorGUILayout.HelpBox("No matching components on targets.", MessageType.Info);
                return;
            }

            EditorGUILayout.Space(4);
            GUIStyle headerStyle = new GUIStyle(EditorStyles.miniBoldLabel);
            const float indexColumnWidth = 44f;

            findScroll = EditorGUILayout.BeginScrollView(findScroll, GUILayout.MinHeight(220f), GUILayout.MaxHeight(480f));
            EditorGUILayout.BeginHorizontal();
            GUILayout.Label("Index", headerStyle, GUILayout.Width(indexColumnWidth));
            GUILayout.Label("Target", headerStyle, GUILayout.ExpandWidth(true));
            EditorGUILayout.EndHorizontal();

            for (int i = 0; i < cachedComponentHits.Count; i++)
            {
                ComponentFindHit hit = cachedComponentHits[i];
                EditorGUILayout.BeginHorizontal();
                GUILayout.Label((i + 1).ToString(), GUILayout.Width(indexColumnWidth));
                DrawFindTargetObjectField(hit);
                EditorGUILayout.EndHorizontal();
            }

            EditorGUILayout.EndScrollView();
        }

        [TabGroup("Tabs", "Modify")]
        [CustomValueDrawer(nameof(DrawModifyHeader))]
        public string ModifyHeader = "";

        private HashSet<string> modifiedPropertyPaths = new HashSet<string>();
        private readonly ComponentPreviewHandle previewHandle = new ComponentPreviewHandle();

        [TabGroup("Tabs", "Modify")]
        [OnInspectorGUI]
        private void DrawPreviewComponentWithTracking()
        {
            Component previewComponent = previewHandle.PreviewComponent;
            Component originalComponent = previewHandle.OriginalComponent;

            if (previewComponent == null)
            {
                EditorGUILayout.HelpBox("Select a component type above to begin editing.", MessageType.Info);
                return;
            }

            EditorGUILayout.BeginHorizontal();
            EditorGUILayout.LabelField($"Modified Fields: {modifiedPropertyPaths.Count}", EditorStyles.boldLabel);
            if (modifiedPropertyPaths.Count > 0 && GUILayout.Button("Clear All", GUILayout.Width(80)))
            {
                modifiedPropertyPaths.Clear();
                if (originalComponent == null)
                    throw new InvalidOperationException("original component is null.");

                EditorUtility.CopySerialized(originalComponent, previewComponent);
            }
            EditorGUILayout.EndHorizontal();

            EditorGUILayout.Space(5);

            SerializedObject so = new SerializedObject(previewComponent);
            so.Update();

            SerializedProperty prop = so.GetIterator();
            bool enterChildren = true;

            while (prop.NextVisible(enterChildren))
            {
                enterChildren = false;
                if (prop.propertyPath == "m_Script") continue;

                bool wasModified = modifiedPropertyPaths.Contains(prop.propertyPath);

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
                EditorGUILayout.PropertyField(prop, true);
                if (EditorGUI.EndChangeCheck())
                {
                    modifiedPropertyPaths.Add(prop.propertyPath);
                }

                EditorGUILayout.EndHorizontal();
            }

            so.ApplyModifiedProperties();
        }

        [TabGroup("Tabs", "Modify")]
        [Button(ButtonSizes.Large)]
        public void ApplyModify()
        {
            int modifiedCount = FComponentBatchOperations.ApplyModifyToCachedComponentHits(
                ComponentType,
                previewHandle.PreviewComponent,
                modifiedPropertyPaths,
                IncrementChanges,
                IncrementRate,
                cachedComponentHits);
            Debug.Log($"<color=cyan>Modified {modifiedCount} component(s) of type {ComponentType.Name}</color>");
            RebuildComponentInstanceCache();
        }

        [TabGroup("Tabs", "Modify")]
        [BoxGroup("Tabs/Modify/Modify Settings", LabelText = "Modify Settings")]
        public bool IncrementChanges;

        [TabGroup("Tabs", "Modify")]
        [BoxGroup("Tabs/Modify/Modify Settings", LabelText = "Modify Settings")]
        [ShowIf(nameof(IncrementChanges))]
        public int IncrementRate = 1;

        [TabGroup("Tabs", "Remove")]
        [CustomValueDrawer(nameof(DrawRemoveHeader))]
        public string RemoveHeader = "";

        [TabGroup("Tabs", "Remove")]
        [Button(ButtonSizes.Large)]
        public void RemoveComponent()
        {
            int removedCount = FComponentBatchOperations.RemoveComponentFromCachedComponentHits(ComponentType, cachedComponentHits);
            Debug.Log($"<color=green>Removed {removedCount} components of type {ComponentType.Name}</color>");
            RebuildComponentInstanceCache();
        }

        private string DrawAddHeader(string value, GUIContent label)
        {
            return DrawTabHeader(
                value,
                "ADD COMPONENT INFO",
                "Hierarchy paths refresh when component type and targets are both set. Pick a path, then add.",
                new Color(0.2f, 0.8f, 0.2f)
            );
        }

        private string DrawFindHeader(string value, GUIContent label)
        {
            return DrawTabHeader(
                value,
                "FIND COMPONENT INSTANCES",
                "Cache rebuilds when component type or targets change (not when switching tabs). Click Target to ping/select.",
                new Color(0.75f, 0.55f, 1f)
            );
        }

        private string DrawModifyHeader(string value, GUIContent label)
        {
            return DrawTabHeader(
                value,
                "MODIFY COMPONENT INFO",
                "Change your component data here and click apply.",
                Color.cyan
            );
        }

        private string DrawRemoveHeader(string value, GUIContent label)
        {
            return DrawTabHeader(
                value,
                "REMOVE COMPONENT INFO",
                "Removes all components of the chosen type from target objects/prefabs hierachy.",
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

        private void OnComponentTypeChanged()
        {
            if (componentType == null)
            {
                cachedComponentHits.Clear();
                previewHandle.Cleanup();
                modifiedPropertyPaths.Clear();
                RebuildHierarchyOptionsIfReady();
                return;
            }

            previewHandle.Reset(ComponentType, modifiedPropertyPaths);
            RebuildHierarchyOptionsIfReady();
            RebuildComponentInstanceCache();
        }

        [OnInspectorInit]
        private void OnInspectorInit()
        {
            if (componentType != null)
                previewHandle.Reset(ComponentType, modifiedPropertyPaths);

            RebuildHierarchyOptionsIfReady();
            RebuildComponentInstanceCache();
        }

        [OnInspectorDispose]
        private void OnInspectorDispose()
        {
            previewHandle.Cleanup();
        }

        private IEnumerable<ValueDropdownItem<Type>> GetComponentTypeOptions()
        {
            return FComponentTypeOptionsProvider.GetComponentTypeOptions();
        }

        private List<ValueDropdownItem<string>> hierarchyOptions = new List<ValueDropdownItem<string>>();
        private HashSet<string> conflictPaths = new HashSet<string>();
        private int cachedPrefabCount;

        private IEnumerable<ValueDropdownItem<string>> GetHierarchyOptions()
        {
            return hierarchyOptions;
        }

        protected override void OnTargetPrefabsChanged()
        {
            if (!(TargetPrefabs?.Count > 0))
            {
                cachedComponentHits.Clear();
                RebuildHierarchyOptionsIfReady();
                return;
            }

            RebuildHierarchyOptionsIfReady();
            RebuildComponentInstanceCache();
        }

        private void RebuildHierarchyOptionsIfReady()
        {
            if (componentType == null || !(TargetPrefabs?.Count > 0))
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

        private void RebuildComponentInstanceCache()
        {
            cachedComponentHits.Clear();
            if (componentType == null)
                return;
            if (!(TargetPrefabs?.Count > 0))
                return;

            FComponentBatchOperations.CollectComponentFindHits(ComponentType, TargetPrefabs, cachedComponentHits);
        }

        private void DrawFindTargetObjectField(ComponentFindHit hit)
        {
            string displayName = GetFindHitHostDisplayName(hit);
            Rect rect = EditorGUILayout.GetControlRect(false, EditorGUIUtility.singleLineHeight, GUILayout.ExpandWidth(true));
            GUIContent content = EditorGUIUtility.ObjectContent(null, typeof(GameObject));
            content.text = displayName;
            EditorGUI.LabelField(rect, content, EditorStyles.objectField);
            if (GUI.Button(rect, GUIContent.none, GUIStyle.none))
                NavigateToComponentFindHit(hit);
        }

        private static string GetFindHitHostDisplayName(ComponentFindHit hit)
        {
            if (hit.IsScene)
                return hit.SceneHostComponent.gameObject.name;

            if (string.IsNullOrEmpty(hit.RelativeHierarchyPath))
                return hit.TargetRootName;

            int slash = hit.RelativeHierarchyPath.LastIndexOf('/');
            return slash >= 0 ? hit.RelativeHierarchyPath.Substring(slash + 1) : hit.RelativeHierarchyPath;
        }

        private void NavigateToComponentFindHit(ComponentFindHit hit)
        {
            if (hit.IsScene)
            {
                GameObject sceneGo = hit.SceneHostComponent.gameObject;
                Selection.activeGameObject = sceneGo;
                EditorGUIUtility.PingObject(sceneGo);
                return;
            }

            string assetPath = hit.PrefabAssetPath;
            PrefabStageUtility.OpenPrefab(assetPath);
            string relativePath = hit.RelativeHierarchyPath;
            EditorApplication.delayCall += () => CompleteNavigatePrefabComponentFindHit(assetPath, relativePath);
        }

        private static void CompleteNavigatePrefabComponentFindHit(string assetPath, string relativeHierarchyPath)
        {
            PrefabStage stage = PrefabStageUtility.GetCurrentPrefabStage();
            if (stage == null)
                throw new InvalidOperationException("prefab stage is null after OpenPrefab.");

            if (stage.assetPath != assetPath)
                throw new InvalidOperationException("prefab stage path mismatch; wrong window focused?");

            Transform root = stage.prefabContentsRoot.transform;
            Transform targetTransform = string.IsNullOrEmpty(relativeHierarchyPath)
                ? root
                : FHierarchyPathResolver.ResolveTargetByPath(root, relativeHierarchyPath);

            GameObject targetGameObject = targetTransform.gameObject;
            Selection.activeGameObject = targetGameObject;
            EditorGUIUtility.PingObject(targetGameObject);
        }

        private Type ComponentType
        {
            get
            {
                return componentType ?? throw new InvalidOperationException("component type is null.");
            }
        }
    }
}
