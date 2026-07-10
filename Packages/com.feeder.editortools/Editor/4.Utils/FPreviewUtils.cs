using System;
using System.Collections.Generic;
using UnityEditor;
using UnityEngine;

namespace Feeder
{
    public sealed class ComponentPreviewHandle
    {
        public GameObject PreviewHolder { get; private set; }
        public GameObject OriginalHolder { get; private set; }
        public Component PreviewComponent { get; private set; }
        public Component OriginalComponent { get; private set; }

        public void Reset(Type componentType, HashSet<string> modifiedPropertyPaths)
        {
            if (componentType == null)
                throw new InvalidOperationException("component type is null.");
            if (modifiedPropertyPaths == null)
                throw new InvalidOperationException("modified property paths is null.");

            Cleanup();
            modifiedPropertyPaths.Clear();

            PreviewHolder = new GameObject($"[PreviewHolder_{componentType.Name}]");
            PreviewHolder.hideFlags = HideFlags.HideInHierarchy | HideFlags.DontSave;
            PreviewComponent = PreviewHolder.AddComponent(componentType);

            OriginalHolder = new GameObject($"[Original_{componentType.Name}]");
            OriginalHolder.hideFlags = HideFlags.HideInHierarchy | HideFlags.DontSave;
            OriginalComponent = OriginalHolder.AddComponent(componentType);
        }

        public void Cleanup()
        {
            if (PreviewHolder != null)
            {
                UnityEngine.Object.DestroyImmediate(PreviewHolder);
                PreviewHolder = null;
                PreviewComponent = null;
            }
            if (OriginalHolder != null)
            {
                UnityEngine.Object.DestroyImmediate(OriginalHolder);
                OriginalHolder = null;
                OriginalComponent = null;
            }
        }
    }

    public static class FPreviewUtils
    {
        public static void DrawMeshRotationFields(Rect rect, string slotId, string label)
        {
            var euler = FMeshPreviewDrawer.GetRotationEuler(slotId);

            var labelWidth = 60f;
            var labelRect = new Rect(rect.x, rect.y, labelWidth, rect.height);
            var fieldRect = new Rect(rect.x + labelWidth, rect.y, rect.width - labelWidth, rect.height);

            EditorGUI.LabelField(labelRect, label);
            var newEuler = EditorGUI.Vector3Field(fieldRect, GUIContent.none, euler);

            if (newEuler != euler)
                FMeshPreviewDrawer.SetRotationEuler(slotId, newEuler);
        }
    }
}
