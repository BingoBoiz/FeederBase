using System;
using System.Collections.Generic;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;

namespace Feeder
{
    public readonly struct ComponentReplaceResult
    {
        public readonly int ReplacedCount;
        public readonly int ModifiedPrefabs;
        public readonly int ModifiedSceneObjects;

        public ComponentReplaceResult(int replacedCount, int modifiedPrefabs, int modifiedSceneObjects)
        {
            ReplacedCount = replacedCount;
            ModifiedPrefabs = modifiedPrefabs;
            ModifiedSceneObjects = modifiedSceneObjects;
        }
    }

    public static class FComponentReplaceService
    {
        public static ComponentReplaceResult ReplaceComponents(
            Type replaceWithType,
            Type findType,
            IReadOnlyList<GameObject> targetPrefabs)
        {
            ValidateComponentTypes(replaceWithType, findType);
            if (!(targetPrefabs?.Count > 0))
                throw new InvalidOperationException("target objects is empty.");

            int replacedCount = 0;
            int modifiedPrefabs = 0;
            int modifiedSceneObjects = 0;

            for (int i = 0; i < targetPrefabs.Count; i++)
            {
                var target = targetPrefabs[i];
                if (target == null)
                {
                    Debug.LogWarning($"[FComponentReplaceService] Skipping null at targetPrefabs[{i}].");
                    continue;
                }

                if (target.scene.IsValid())
                {
                    var instanceRoot = PrefabUtility.GetNearestPrefabInstanceRoot(target) ?? target;
                    int replacedInScene = ReplaceOnSceneRoot(instanceRoot, replaceWithType, findType, target.name);
                    if (replacedInScene > 0)
                    {
                        modifiedSceneObjects++;
                    }
                    replacedCount += replacedInScene;
                    continue;
                }

                var path = AssetDatabase.GetAssetPath(target);
                if (string.IsNullOrEmpty(path))
                {
                    Debug.LogWarning($"[FComponentReplaceService] Skipping targetPrefabs[{i}] (no asset path).");
                    continue;
                }

                var prefabRoot = PrefabUtility.LoadPrefabContents(path);
                bool modified = false;

                try
                {
                    int replacedInPrefab = ReplaceOnRoot(prefabRoot, replaceWithType, findType, target.name, null);
                    if (replacedInPrefab > 0)
                        modified = true;
                    replacedCount += replacedInPrefab;

                    if (modified)
                    {
                        PrefabUtility.SaveAsPrefabAsset(prefabRoot, path);
                        AssetDatabase.ImportAsset(path, ImportAssetOptions.ForceUpdate);
                        modifiedPrefabs++;
                    }
                }
                finally
                {
                    PrefabUtility.UnloadPrefabContents(prefabRoot);
                }
            }

            AssetDatabase.SaveAssets();
            AssetDatabase.Refresh();
            return new ComponentReplaceResult(replacedCount, modifiedPrefabs, modifiedSceneObjects);
        }

        private static void ValidateComponentTypes(Type replaceWithType, Type findType)
        {
            ValidateMonoBehaviourType(replaceWithType, "replace");
            ValidateMonoBehaviourType(findType, "find");

            if (replaceWithType == findType)
                throw new InvalidOperationException("replace type must be different from find type.");
        }

        private static void ValidateMonoBehaviourType(Type type, string label)
        {
            if (type == null)
                throw new InvalidOperationException($"{label} type is null.");
            if (!typeof(MonoBehaviour).IsAssignableFrom(type))
                throw new InvalidOperationException($"{label} type must be a MonoBehaviour.");
            if (type.IsAbstract)
                throw new InvalidOperationException($"{label} type is abstract.");
        }

        private static int ReplaceOnSceneRoot(GameObject root, Type replaceType, Type findType, string contextName)
        {
            if (root == null)
                throw new InvalidOperationException("root is null.");

            int replaced = ReplaceOnRoot(root, replaceType, findType, contextName, (target) =>
            {
                if (target == null)
                    throw new InvalidOperationException("target is null.");

                EditorUtility.SetDirty(target);
                if (target is Component componentTarget && PrefabUtility.IsPartOfPrefabInstance(componentTarget))
                {
                    PrefabUtility.RecordPrefabInstancePropertyModifications(componentTarget);
                }
            });

            if (replaced <= 0)
                return 0;

            EditorUtility.SetDirty(root);
            if (root.scene.IsValid())
            {
                EditorSceneManager.MarkSceneDirty(root.scene);
            }

            return replaced;
        }

        private static int ReplaceOnRoot(
            GameObject root,
            Type replaceType,
            Type findType,
            string contextName,
            Action<UnityEngine.Object> onChanged)
        {
            if (root == null)
                throw new InvalidOperationException("root is null.");
            if (replaceType == null)
                throw new InvalidOperationException("replace type is null.");
            if (findType == null)
                throw new InvalidOperationException("find type is null.");

            int replaced = 0;
            var targets = root.GetComponentsInChildren(findType, true);
            var existingComponentsByObject = new Dictionary<GameObject, Component[]>();
            var nextReplaceIndexByObject = new Dictionary<GameObject, int>();

            for (int i = 0; i < targets.Length; i++)
            {
                var sourceComponent = targets[i] as Component ?? throw new InvalidOperationException("source component is null.");
                var targetGameObject = sourceComponent.gameObject ?? throw new InvalidOperationException("target GameObject is null.");

                if (!existingComponentsByObject.TryGetValue(targetGameObject, out var existingTargets))
                {
                    existingTargets = targetGameObject.GetComponents(replaceType);
                    existingComponentsByObject[targetGameObject] = existingTargets;
                }

                if (!nextReplaceIndexByObject.TryGetValue(targetGameObject, out var replaceIndex))
                {
                    replaceIndex = 0;
                }
                nextReplaceIndexByObject[targetGameObject] = replaceIndex + 1;

                var targetComponent = (existingTargets != null && replaceIndex < existingTargets.Length)
                    ? existingTargets[replaceIndex]
                    : targetGameObject.AddComponent(replaceType) as Component;

                if (targetComponent == null)
                    throw new InvalidOperationException($"failed to add component {replaceType.FullName} in {contextName}.");

                CopySerializedProperties(sourceComponent, targetComponent, contextName);
                onChanged?.Invoke(targetComponent);

                UnityEngine.Object.DestroyImmediate(sourceComponent, true);
                replaced++;
            }

            return replaced;
        }

        private static void CopySerializedProperties(Component source, Component target, string contextName)
        {
            if (source == null)
                throw new InvalidOperationException("source component is null.");
            if (target == null)
                throw new InvalidOperationException("target component is null.");

            var soSource = new SerializedObject(source);
            var soTarget = new SerializedObject(target);

            EnsureArraySizes(soSource, soTarget, contextName);

            var prop = soSource.GetIterator();
            bool enterChildren = true;

            while (prop.NextVisible(enterChildren))
            {
                enterChildren = true;
                if (IsEngineInternalProperty(prop)) continue;

                var targetProp = soTarget.FindProperty(prop.propertyPath);
                if (targetProp == null)
                    throw new InvalidOperationException($"missing target property: {prop.propertyPath} | source: {source.GetType().Name} | context: {contextName}");

                if (prop.propertyType == SerializedPropertyType.ObjectReference)
                {
                    targetProp.objectReferenceValue = prop.objectReferenceValue;
                    continue;
                }

                TryCopyValue(prop, targetProp);
            }

            soTarget.ApplyModifiedProperties();
        }

        private static void EnsureArraySizes(SerializedObject soSource, SerializedObject soTarget, string contextName)
        {
            if (soSource == null || soTarget == null)
                throw new InvalidOperationException("serialized object is null.");

            var prop = soSource.GetIterator();
            bool enterChildren = true;

            while (prop.NextVisible(enterChildren))
            {
                enterChildren = true;
                if (IsEngineInternalProperty(prop)) continue;
                if (!prop.isArray || prop.propertyType == SerializedPropertyType.String) continue;

                var targetProp = soTarget.FindProperty(prop.propertyPath);
                if (targetProp == null)
                    throw new InvalidOperationException($"missing target array: {prop.propertyPath} | context: {contextName}");

                if (targetProp.arraySize != prop.arraySize)
                {
                    targetProp.arraySize = prop.arraySize;
                }
            }

            soTarget.ApplyModifiedPropertiesWithoutUndo();
        }

        private static bool TryCopyValue(SerializedProperty source, SerializedProperty target)
        {
            switch (source.propertyType)
            {
                case SerializedPropertyType.Integer:
                    target.intValue = source.intValue;
                    return true;
                case SerializedPropertyType.Boolean:
                    target.boolValue = source.boolValue;
                    return true;
                case SerializedPropertyType.Float:
                    target.floatValue = source.floatValue;
                    return true;
                case SerializedPropertyType.String:
                    target.stringValue = source.stringValue;
                    return true;
                case SerializedPropertyType.Color:
                    target.colorValue = source.colorValue;
                    return true;
                case SerializedPropertyType.Enum:
                    target.enumValueIndex = source.enumValueIndex;
                    return true;
                case SerializedPropertyType.Vector2:
                    target.vector2Value = source.vector2Value;
                    return true;
                case SerializedPropertyType.Vector3:
                    target.vector3Value = source.vector3Value;
                    return true;
                case SerializedPropertyType.Vector4:
                    target.vector4Value = source.vector4Value;
                    return true;
                case SerializedPropertyType.Rect:
                    target.rectValue = source.rectValue;
                    return true;
                case SerializedPropertyType.Bounds:
                    target.boundsValue = source.boundsValue;
                    return true;
                case SerializedPropertyType.Quaternion:
                    target.quaternionValue = source.quaternionValue;
                    return true;
                case SerializedPropertyType.Vector2Int:
                    target.vector2IntValue = source.vector2IntValue;
                    return true;
                case SerializedPropertyType.Vector3Int:
                    target.vector3IntValue = source.vector3IntValue;
                    return true;
                case SerializedPropertyType.RectInt:
                    target.rectIntValue = source.rectIntValue;
                    return true;
                case SerializedPropertyType.BoundsInt:
                    target.boundsIntValue = source.boundsIntValue;
                    return true;
                case SerializedPropertyType.AnimationCurve:
                    target.animationCurveValue = source.animationCurveValue;
                    return true;
                case SerializedPropertyType.ManagedReference:
                    target.managedReferenceValue = source.managedReferenceValue;
                    return true;
            }

            return false;
        }

        private static bool IsEngineInternalProperty(SerializedProperty property)
        {
            if (property == null)
                throw new InvalidOperationException("property is null.");
            if (property.depth != 0)
                return false;

            string path = property.propertyPath;
            return path == "m_Script"
                   || path == "m_GameObject"
                   || path == "m_PrefabInstance"
                   || path == "m_PrefabInternal"
                   || path == "m_Father"
                   || path == "m_Children";
        }
    }
}
