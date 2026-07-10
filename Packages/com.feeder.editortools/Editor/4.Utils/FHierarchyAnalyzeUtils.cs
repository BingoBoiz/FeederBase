using System;
using System.Collections.Generic;
using Sirenix.OdinInspector;
using UnityEngine;

namespace Feeder
{
    public static class FHierarchyAnalyzeUtils
    {
        public readonly struct HierarchyNodeInfo
        {
            public readonly string Path;
            public readonly string ChildSignature;

            public HierarchyNodeInfo(string path, string childSignature)
            {
                Path = path ?? throw new ArgumentNullException(nameof(path));
                ChildSignature = childSignature ?? throw new ArgumentNullException(nameof(childSignature));
            }
        }

        public static List<ValueDropdownItem<Transform>> BuildTransformDropdown(Transform root)
        {
            if (root == null)
                throw new InvalidOperationException("root is null.");

            var results = new List<ValueDropdownItem<Transform>>();
            var rootName = root.name ?? throw new InvalidOperationException("root name is null.");
            AddTransformToDropdown(results, root, rootName);
            return results;
        }

        public static List<HierarchyNodeInfo> CollectHierarchyInfo(Transform root, bool includeRoot)
        {
            if (root == null)
                throw new InvalidOperationException("root is null.");

            var results = new List<HierarchyNodeInfo>();

            if (includeRoot)
            {
                AddHierarchyInfo(results, root, root.name);
                return results;
            }

            foreach (Transform child in root)
            {
                AddHierarchyInfo(results, child, child.name);
            }

            return results;
        }

        private static void AddTransformToDropdown(List<ValueDropdownItem<Transform>> list, Transform tr, string path)
        {
            list.Add(new ValueDropdownItem<Transform>(path, tr));

            foreach (Transform child in tr)
            {
                AddTransformToDropdown(list, child, path + "/" + child.name);
            }
        }

        private static void AddHierarchyInfo(List<HierarchyNodeInfo> results, Transform node, string path)
        {
            results.Add(new HierarchyNodeInfo(path, BuildChildSignature(node)));

            foreach (Transform child in node)
            {
                AddHierarchyInfo(results, child, path + "/" + child.name);
            }
        }

        private static string BuildChildSignature(Transform node)
        {
            var childNames = new List<string>();
            foreach (Transform child in node)
            {
                childNames.Add(child.name);
            }

            childNames.Sort(StringComparer.Ordinal);
            return string.Join("|", childNames);
        }
    }
}
