using System;
using System.Collections.Generic;
using Sirenix.OdinInspector;
using UnityEditor;
using UnityEngine;

namespace Feeder
{
    public sealed class FMissingComponentHandlerTool : FTargetPrefabsToolBase
    {
        protected override string GetDescription()
        {
            return "Chọn danh sách prefab/scene; tab Replace thay missing script bằng component được chọn, tab Delete xóa mọi missing script, tab Find liệt kê missing script theo từng nhóm script identifier.";
        }

        #region =================== TAB REPLACE ===================

        [TabGroup("Tabs", "Replace")]
        [CustomValueDrawer(nameof(DrawReplaceHeader))]
        public string replaceHeader = "";

        private string DrawReplaceHeader(string value, GUIContent label)
        {
            return DrawTabHeader(
                value,
                "REPLACE MISSING SCRIPT INFO",
                "Replaces all missing scripts with a chosen component type in target objects/prefabs hierarchy.",
                new Color(0.2f, 0.6f, 1f) // blue
            );
        }

        [TabGroup("Tabs", "Replace")]
        [LabelText("Replacement Component Type")]
        [ValueDropdown(nameof(GetComponentTypeOptions))]
        [ShowInInspector]
        public Type replacementComponentType;

        [TabGroup("Tabs", "Replace")]
        [Button(ButtonSizes.Large)]
        public void ReplaceMissingScripts()
        {
            if (replacementComponentType == null)
            {
                EditorUtility.DisplayDialog("Error", "Please choose a replacement component type!", "OK");
                return;
            }

            // validate that it's a concrete Component
            if (!typeof(Component).IsAssignableFrom(replacementComponentType) || replacementComponentType.IsAbstract)
            {
                Debug.LogError($"Selected type must be a non-abstract Component. Got: {replacementComponentType.FullName}");
                return;
            }

            // disallow Transform/RectTransform etc. (cannot be added)
            if (replacementComponentType == typeof(Transform) || replacementComponentType == typeof(RectTransform))
            {
                Debug.LogError($"Cannot add {replacementComponentType.Name} via AddComponent.");
                return;
            }

            int replacedCount = 0;

            foreach (var go in TargetPrefabs)
            {
                if (go == null) continue;

                // scene object case
                if (go.scene.IsValid())
                {
                    replacedCount += ReplaceMissingScriptsInGameObject(go, replacementComponentType, false);
                }
                // prefab asset case
                else
                {
                    string path = AssetDatabase.GetAssetPath(go);
                    if (string.IsNullOrEmpty(path)) continue;

                    GameObject prefabRoot = PrefabUtility.LoadPrefabContents(path);
                    try
                    {
                        replacedCount += ReplaceMissingScriptsInGameObject(prefabRoot, replacementComponentType, true);
                        PrefabUtility.SaveAsPrefabAsset(prefabRoot, path);
                    }
                    finally
                    {
                        PrefabUtility.UnloadPrefabContents(prefabRoot);
                    }
                }
            }

            AssetDatabase.SaveAssets();
            AssetDatabase.Refresh();
            Debug.Log($"<color=blue>Replaced {replacedCount} missing script(s) with {replacementComponentType.Name}</color>");
        }

        private int ReplaceMissingScriptsInGameObject(GameObject root, Type replacementType, bool isPrefab)
        {
            int count = 0;
            var allGameObjects = new List<GameObject>();
            CollectAllGameObjects(root, allGameObjects);

            foreach (var gameObject in allGameObjects)
            {
                count += ReplaceMissingScriptsOnGameObject(gameObject, replacementType, isPrefab);
            }

            return count;
        }

        private int ReplaceMissingScriptsOnGameObject(GameObject gameObject, Type replacementType, bool isPrefab)
        {
            int count = 0;
            SerializedObject serializedObject = new SerializedObject(gameObject);
            SerializedProperty componentsProperty = serializedObject.FindProperty("m_Component");

            if (componentsProperty == null || !componentsProperty.isArray) return 0;

            // find all missing script indices (in reverse order to maintain indices)
            List<int> missingIndices = new List<int>();
            for (int i = 0; i < componentsProperty.arraySize; i++)
            {
                SerializedProperty componentProperty = componentsProperty.GetArrayElementAtIndex(i);
                SerializedProperty scriptProperty = componentProperty.FindPropertyRelative("m_Script");

                if (scriptProperty != null && scriptProperty.objectReferenceValue == null)
                {
                    missingIndices.Add(i);
                }
            }

            // replace missing scripts (process in reverse to maintain indices)
            for (int i = missingIndices.Count - 1; i >= 0; i--)
            {
                int index = missingIndices[i];

                // remove missing script
                if (isPrefab)
                {
                    // for prefabs, we need to remove via SerializedProperty
                    componentsProperty.DeleteArrayElementAtIndex(index);
                }
                else
                {
                    // for scene objects, use Undo
                    componentsProperty.DeleteArrayElementAtIndex(index);
                }

                // add new component at the same index
                Component newComponent;
                if (isPrefab)
                {
                    newComponent = gameObject.AddComponent(replacementType);
                }
                else
                {
                    newComponent = Undo.AddComponent(gameObject, replacementType);
                }

                count++;
                string prefix = isPrefab ? "[Prefab]" : "";
                Debug.Log($"<color=blue>{prefix} Replaced missing script on {gameObject.name} with {replacementType.Name}</color>");
            }

            serializedObject.ApplyModifiedProperties();
            return count;
        }

        #endregion

        #region =================== TAB DELETE ===================

        [TabGroup("Tabs", "Delete")]
        [CustomValueDrawer(nameof(DrawDeleteHeader))]
        public string deleteHeader = "";

        private string DrawDeleteHeader(string value, GUIContent label)
        {
            return DrawTabHeader(
                value,
                "DELETE MISSING SCRIPT INFO",
                "Removes all missing scripts from target objects/prefabs hierarchy.",
                new Color(1f, 0.3f, 0.3f) // red
            );
        }

        [TabGroup("Tabs", "Delete")]
        [Button(ButtonSizes.Large)]
        public void DeleteMissingScripts()
        {
            int deletedCount = 0;

            foreach (var go in TargetPrefabs)
            {
                if (go == null) continue;

                // scene object case
                if (go.scene.IsValid())
                {
                    deletedCount += DeleteMissingScriptsInGameObject(go, false);
                }
                // prefab asset case
                else
                {
                    string path = AssetDatabase.GetAssetPath(go);
                    if (string.IsNullOrEmpty(path)) continue;

                    GameObject prefabRoot = PrefabUtility.LoadPrefabContents(path);
                    try
                    {
                        deletedCount += DeleteMissingScriptsInGameObject(prefabRoot, true);
                        PrefabUtility.SaveAsPrefabAsset(prefabRoot, path);
                    }
                    finally
                    {
                        PrefabUtility.UnloadPrefabContents(prefabRoot);
                    }
                }
            }

            AssetDatabase.SaveAssets();
            AssetDatabase.Refresh();
            Debug.Log($"<color=red>Deleted {deletedCount} missing script(s)</color>");
        }

        private int DeleteMissingScriptsInGameObject(GameObject root, bool isPrefab)
        {
            int count = 0;
            var allGameObjects = new List<GameObject>();
            CollectAllGameObjects(root, allGameObjects);

            foreach (var gameObject in allGameObjects)
            {
                count += DeleteMissingScriptsOnGameObject(gameObject, isPrefab);
            }

            return count;
        }

        private int DeleteMissingScriptsOnGameObject(GameObject gameObject, bool isPrefab)
        {
            // use Unity's built-in method for reliable missing script removal
            int count = GameObjectUtility.RemoveMonoBehavioursWithMissingScript(gameObject);

            if (count > 0)
            {
                string prefix = isPrefab ? "[Prefab]" : "";
                Debug.Log($"<color=red>{prefix} Deleted {count} missing script(s) on {gameObject.name}</color>");
            }

            return count;
        }

        #endregion

        #region =================== TAB FIND ===================

        [TabGroup("Tabs", "Find")]
        [CustomValueDrawer(nameof(DrawFindHeader))]
        public string findHeader = "";

        private string DrawFindHeader(string value, GUIContent label)
        {
            return DrawTabHeader(
                value,
                "FIND MISSING SCRIPT INFO",
                "Finds and displays all missing scripts in target objects/prefabs hierarchy, grouped by script identifier.",
                new Color(1f, 0.8f, 0.2f) // yellow
            );
        }

        [TabGroup("Tabs", "Find")]
        [Button(ButtonSizes.Large)]
        public void FindMissingScripts()
        {
            missingScriptGroups.Clear();
            int totalScanned = 0;

            foreach (var go in TargetPrefabs)
            {
                if (go == null) continue;

                // scene object case
                if (go.scene.IsValid())
                {
                    int count = CountGameObjectsInHierarchy(go);
                    totalScanned += count;
                    FindMissingScriptsInGameObject(go, false, null, go);
                }
                // prefab asset case
                else
                {
                    string path = AssetDatabase.GetAssetPath(go);
                    if (string.IsNullOrEmpty(path)) continue;

                    GameObject prefabRoot = PrefabUtility.LoadPrefabContents(path);
                    try
                    {
                        int count = CountGameObjectsInHierarchy(prefabRoot);
                        totalScanned += count;
                        FindMissingScriptsInGameObject(prefabRoot, true, path, go);
                    }
                    finally
                    {
                        PrefabUtility.UnloadPrefabContents(prefabRoot);
                    }
                }
            }

            int totalFound = GetTotalMissingScriptCount();
            Debug.Log($"<color=yellow>Scanned {totalScanned} GameObject(s), Found {totalFound} missing script(s) in {missingScriptGroups.Count} group(s)</color>");

            if (totalFound == 0 && totalScanned > 0)
            {
                Debug.LogWarning("No missing scripts found. Make sure the target objects actually have missing scripts visible in the Inspector.");
            }
        }

        private int CountGameObjectsInHierarchy(GameObject root)
        {
            int count = 1;
            foreach (Transform child in root.transform)
            {
                count += CountGameObjectsInHierarchy(child.gameObject);
            }
            return count;
        }

        [TabGroup("Tabs", "Find")]
        [ShowIf("@missingScriptGroups.Count > 0")]
        [InfoBox("Missing scripts are grouped by their script identifier.")]
        [ListDrawerSettings(ShowFoldout = true, DraggableItems = false, ShowIndexLabels = false, NumberOfItemsPerPage = 5)]
        public List<MissingScriptGroup> missingScriptGroups = new List<MissingScriptGroup>();

        [TabGroup("Tabs", "Find")]
        [HideIf("@missingScriptGroups.Count > 0")]
        [InfoBox("No missing scripts found. Click 'Find Missing Scripts' button to scan target objects.")]
        public string noResultsMessage = "";

        private void FindMissingScriptsInGameObject(GameObject root, bool isPrefab, string prefabPath, GameObject targetGameObject)
        {
            var allGameObjects = new List<GameObject>();
            CollectAllGameObjects(root, allGameObjects);

            foreach (var gameObject in allGameObjects)
            {
                FindMissingScriptsOnGameObject(gameObject, isPrefab, prefabPath, targetGameObject);
            }
        }

        private void FindMissingScriptsOnGameObject(GameObject gameObject, bool isPrefab, string prefabPath, GameObject targetGameObject)
        {
            SerializedObject so = new SerializedObject(gameObject);
            so.Update();
            SerializedProperty components = so.FindProperty("m_Component");

            if (components == null || !components.isArray) return;

            // iterate through all component entries
            for (int i = 0; i < components.arraySize; i++)
            {
                SerializedProperty componentEntry = components.GetArrayElementAtIndex(i);
                SerializedProperty componentRef = componentEntry.FindPropertyRelative("component");

                // Unity serialize component pointers trực tiếp vào m_Component
                // Nếu script mất → entry vẫn tồn tại nhưng objectReferenceValue = null
                // Transform, Collider, ... không bao giờ null → missing script chỉ xảy ra cho MonoBehaviour
                Component comp = componentRef?.objectReferenceValue as Component;

                if (comp == null)
                {
                    // found missing script - get script identifier and add to list
                    SerializedProperty scriptProperty = componentEntry.FindPropertyRelative("m_Script");
                    string scriptIdentifier = GetScriptIdentifier(componentEntry, scriptProperty);

                    AddMissingScriptEntry(gameObject, i, isPrefab, prefabPath, scriptIdentifier, targetGameObject);
                }
            }
        }

        private void AddMissingScriptEntry(GameObject gameObject, int componentIndex, bool isPrefab, string prefabPath, string scriptIdentifier, GameObject targetGameObject)
        {
            // find or create group for this script identifier
            MissingScriptGroup group = missingScriptGroups.Find(g => g.scriptIdentifier == scriptIdentifier);
            if (group == null)
            {
                group = new MissingScriptGroup
                {
                    scriptIdentifier = scriptIdentifier,
                    displayName = FormatScriptIdentifier(scriptIdentifier)
                };
                missingScriptGroups.Add(group);
            }

            // create entry for this missing script
            MissingScriptEntry entry = new MissingScriptEntry
            {
                gameObject = gameObject,
                componentIndex = componentIndex,
                targetGameObject = targetGameObject
            };

            group.entries.Add(entry);
        }

        private string GetScriptIdentifier(SerializedProperty componentProperty, SerializedProperty scriptProperty)
        {
            // Unity stores missing script info with fileID and GUID
            // try to get the fileID which uniquely identifies the missing script
            long fileID = 0;
            string guid = "";

            // get fileID from the component property (this is the local fileID)
            SerializedProperty fileIDProperty = componentProperty.FindPropertyRelative("m_FileID");
            if (fileIDProperty != null && fileIDProperty.propertyType == SerializedPropertyType.Integer)
            {
                fileID = fileIDProperty.longValue;
            }

            // try to get GUID from the script property
            // for missing scripts, Unity stores the GUID in the object reference
            if (scriptProperty != null)
            {
                // iterate through the script property to find GUID
                var scriptCopy = scriptProperty.Copy();
                var scriptEnd = scriptProperty.GetEndProperty();

                while (scriptCopy.Next(true) && !SerializedProperty.EqualContents(scriptCopy, scriptEnd))
                {
                    if (scriptCopy.name == "m_GUID" && scriptCopy.propertyType == SerializedPropertyType.String)
                    {
                        guid = scriptCopy.stringValue;
                        break;
                    }
                }

                // alternative: try to get from the serialized object directly
                if (string.IsNullOrEmpty(guid))
                {
                    var so = scriptProperty.serializedObject;
                    var iterator = so.GetIterator();
                    bool enterChildren = true;

                    while (iterator.NextVisible(enterChildren))
                    {
                        enterChildren = false;
                        if (iterator.propertyPath == scriptProperty.propertyPath)
                        {
                            var guidProp = iterator.FindPropertyRelative("m_GUID");
                            if (guidProp != null && guidProp.propertyType == SerializedPropertyType.String)
                            {
                                guid = guidProp.stringValue;
                                break;
                            }
                        }
                    }
                }
            }

            // create identifier from fileID and GUID
            if (fileID != 0 && !string.IsNullOrEmpty(guid))
            {
                return $"{guid}_{fileID}";
            }
            else if (fileID != 0)
            {
                return $"FileID_{fileID}";
            }
            else if (!string.IsNullOrEmpty(guid))
            {
                return $"GUID_{guid}";
            }

            // fallback: use component index and property path hash
            return $"Unknown_{componentProperty.propertyPath.GetHashCode()}";
        }

        private string FormatScriptIdentifier(string identifier)
        {
            if (string.IsNullOrEmpty(identifier)) return "Unknown Script";

            if (identifier.StartsWith("FileID_"))
            {
                return $"Missing Script (FileID: {identifier.Substring(7)})";
            }
            else if (identifier.StartsWith("GUID_"))
            {
                string guid = identifier.Substring(5);
                // try to find script name from GUID
                string scriptPath = AssetDatabase.GUIDToAssetPath(guid);
                if (!string.IsNullOrEmpty(scriptPath))
                {
                    return $"Missing Script: {System.IO.Path.GetFileNameWithoutExtension(scriptPath)}";
                }
                return $"Missing Script (GUID: {guid.Substring(0, 8)}...)";
            }
            else if (identifier.Contains("_"))
            {
                // format GUID_FileID
                string[] parts = identifier.Split('_');
                if (parts.Length >= 2)
                {
                    string guid = parts[0];
                    string scriptPath = AssetDatabase.GUIDToAssetPath(guid);
                    if (!string.IsNullOrEmpty(scriptPath))
                    {
                        return $"Missing Script: {System.IO.Path.GetFileNameWithoutExtension(scriptPath)}";
                    }
                    return $"Missing Script (GUID: ...)";
                }
            }

            return $"Missing Script ({identifier})";
        }

        private int GetTotalMissingScriptCount()
        {
            int total = 0;
            foreach (var group in missingScriptGroups)
            {
                total += group.entries.Count;
            }
            return total;
        }

        [System.Serializable]
        public class MissingScriptGroup
        {
            [HideLabel]
            [Title("$displayName", "$GetGroupInfo", TitleAlignments.Centered)]
            [TableList(ShowIndexLabels = false, AlwaysExpanded = true, IsReadOnly = true)]
            public List<MissingScriptEntry> entries = new List<MissingScriptEntry>();

            [HideInInspector]
            public string scriptIdentifier;

            [HideInInspector]
            public string displayName;

            private string GetGroupInfo()
            {
                return $"{entries.Count} missing script(s) found";
            }
        }

        [System.Serializable]
        public class MissingScriptEntry
        {
            [TableColumnWidth(250)]
            [LabelText("GameObject")]
            [ReadOnly]
            public GameObject gameObject;

            [TableColumnWidth(250)]
            [LabelText("Target GameObject")]
            [ReadOnly]
            public GameObject targetGameObject;

            [TableColumnWidth(100)]
            [LabelText("Component Index")]
            [ReadOnly]
            public int componentIndex;
        }

        #endregion

        // helper method to collect all GameObjects in hierarchy
        private void CollectAllGameObjects(GameObject root, List<GameObject> results)
        {
            if (root == null) return;
            results.Add(root);

            foreach (Transform child in root.transform)
            {
                CollectAllGameObjects(child.gameObject, results);
            }
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

        private IEnumerable<ValueDropdownItem<Type>> GetComponentTypeOptions()
        {
            return FComponentTypeOptionsProvider.GetComponentTypeOptions();
        }
    }
}
