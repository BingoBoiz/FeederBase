using System;
using System.Collections.Generic;
using UnityEngine;
using Object = UnityEngine.Object;

namespace Feeder
{
    /// <summary>
    /// Shared "Add Selection / Replace With Selection / Clear" row for target lists.
    /// Call from an [OnInspectorGUI] method so any tool/panel can embed the same row.
    /// </summary>
    public static class FSelectionButtonRow
    {
        public static void Draw<T>(List<T> list, Func<List<T>> collectSelection, Action onChanged, string emptyWarning)
            where T : Object
        {
            GUILayout.BeginHorizontal();
            bool add     = GUILayout.Button("Add Selection");
            bool replace = GUILayout.Button("Replace With Selection");
            bool clear   = GUILayout.Button("Clear");
            GUILayout.EndHorizontal();

            if (clear)
            {
                list.Clear();
                onChanged?.Invoke();
                return;
            }

            if (!add && !replace)
                return;

            List<T> picked = collectSelection();
            if (picked.Count == 0)
            {
                Debug.LogWarning(emptyWarning);
                return;
            }

            if (replace)
                list.Clear();
            foreach (T item in picked)
                if (!list.Contains(item))
                    list.Add(item);
            onChanged?.Invoke();
        }
    }
}
