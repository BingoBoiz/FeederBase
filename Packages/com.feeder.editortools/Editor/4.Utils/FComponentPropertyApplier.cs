using System;
using System.Collections.Generic;
using UnityEditor;
using UnityEngine;

namespace Feeder
{
    public static class FComponentPropertyApplier
    {
        public static void ApplyDifferences(
            Component modified,
            Component dst,
            IReadOnlyCollection<string> modifiedPropertyPaths,
            bool incrementChanges,
            int incrementRate,
            int index)
        {
            if (modified == null)
                throw new InvalidOperationException("modified component is null.");
            if (dst == null)
                throw new InvalidOperationException("destination component is null.");
            if (modifiedPropertyPaths == null || modifiedPropertyPaths.Count == 0)
                throw new InvalidOperationException("no modified properties.");

            var soModified = new SerializedObject(modified);
            var soDst = new SerializedObject(dst);

            foreach (string propertyPath in modifiedPropertyPaths)
            {
                var srcProp = soModified.FindProperty(propertyPath);
                var dstProp = soDst.FindProperty(propertyPath);

                if (srcProp == null || dstProp == null)
                    throw new InvalidOperationException($"property '{propertyPath}' is invalid.");

                if (incrementChanges)
                {
                    ApplyIncrementedValue(srcProp, dstProp, index, incrementRate);
                }
                else
                {
                    dstProp.serializedObject.CopyFromSerializedProperty(srcProp);
                }
            }

            soDst.ApplyModifiedProperties();
        }

        private static void ApplyIncrementedValue(SerializedProperty srcProp, SerializedProperty dstProp, int index, int incrementRate)
        {
            switch (srcProp.propertyType)
            {
                case SerializedPropertyType.Integer:
                    int baseInt = srcProp.intValue;
                    dstProp.intValue = baseInt + (index * incrementRate);
                    break;

                case SerializedPropertyType.Float:
                    float baseFloat = srcProp.floatValue;
                    dstProp.floatValue = baseFloat + (index * incrementRate);
                    break;

                case SerializedPropertyType.Enum:
                    int enumCount = srcProp.enumDisplayNames.Length;
                    int baseEnum = srcProp.enumValueIndex;
                    dstProp.enumValueIndex = (baseEnum + (index * incrementRate)) % enumCount;
                    break;

                default:
                    if (!SerializedProperty.DataEquals(srcProp, dstProp))
                    {
                        dstProp.serializedObject.CopyFromSerializedProperty(srcProp);
                    }
                    break;
            }
        }
    }
}
