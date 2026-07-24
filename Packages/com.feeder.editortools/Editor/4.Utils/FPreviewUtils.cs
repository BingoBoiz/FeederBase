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

    public sealed class PrefabNodePreviewHandle
    {
        public GameObject PreviewGo { get; private set; }
        public GameObject OriginalGo { get; private set; }

        public bool IsReady => PreviewGo != null && OriginalGo != null;

        /// <summary>Source node may live inside LoadPrefabContents — values are copied immediately, caller may unload right after.</summary>
        public void ResetFrom(GameObject sourceNode, HashSet<string> modifiedPropertyPaths)
        {
            if (sourceNode == null)
                throw new InvalidOperationException("source node is null.");
            if (modifiedPropertyPaths == null)
                throw new InvalidOperationException("modified property paths is null.");

            Cleanup();
            modifiedPropertyPaths.Clear();

            PreviewGo = new GameObject("[PreviewNode]");
            PreviewGo.hideFlags = HideFlags.HideInHierarchy | HideFlags.DontSave;

            OriginalGo = new GameObject("[OriginalNode]");
            OriginalGo.hideFlags = HideFlags.HideInHierarchy | HideFlags.DontSave;

            CopyNodeState(sourceNode, PreviewGo);
            CopyNodeState(sourceNode, OriginalGo);
        }

        public void RestoreFromOriginal(HashSet<string> modifiedPropertyPaths)
        {
            if (!IsReady)
                throw new InvalidOperationException("preview handle is not ready.");
            if (modifiedPropertyPaths == null)
                throw new InvalidOperationException("modified property paths is null.");

            CopyNodeState(OriginalGo, PreviewGo);
            modifiedPropertyPaths.Clear();
        }

        public void Cleanup()
        {
            if (PreviewGo != null)
            {
                UnityEngine.Object.DestroyImmediate(PreviewGo);
                PreviewGo = null;
            }
            if (OriginalGo != null)
            {
                UnityEngine.Object.DestroyImmediate(OriginalGo);
                OriginalGo = null;
            }
        }

        // dst.tag = src.tag throws for tags missing from Tag Manager; CopySerialized on GameObjects
        // would copy the component list — per-property copy only.
        public static void CopyNodeState(GameObject src, GameObject dst)
        {
            var srcSo = new SerializedObject(src);
            var dstSo = new SerializedObject(dst);
            dstSo.FindProperty("m_IsActive").boolValue = srcSo.FindProperty("m_IsActive").boolValue;
            dstSo.FindProperty("m_Layer").intValue = srcSo.FindProperty("m_Layer").intValue;
            dstSo.FindProperty("m_TagString").stringValue = srcSo.FindProperty("m_TagString").stringValue;
            dstSo.ApplyModifiedPropertiesWithoutUndo();

            dst.transform.localPosition = src.transform.localPosition;
            dst.transform.localRotation = src.transform.localRotation;
            dst.transform.localScale = src.transform.localScale;
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
