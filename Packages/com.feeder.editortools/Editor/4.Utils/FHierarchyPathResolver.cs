using System;
using System.Collections.Generic;
using UnityEngine;

namespace Feeder
{
    public static class FHierarchyPathResolver
    {
        public static string BuildRelativePathFromAncestorTransform(Transform ancestor, Transform descendant)
        {
            if (ancestor == null)
                throw new InvalidOperationException("ancestor is null.");
            if (descendant == null)
                throw new InvalidOperationException("descendant is null.");
            if (descendant == ancestor)
                return "";

            List<string> segments = new List<string>();
            Transform walk = descendant;
            while (walk != null && walk != ancestor)
            {
                segments.Add(walk.name);
                walk = walk.parent;
            }

            if (walk != ancestor)
                throw new InvalidOperationException("descendant is not under ancestor.");

            segments.Reverse();
            return string.Join("/", segments);
        }

        public static Transform FindDirectChildOfAncestor(Transform ancestor, Transform descendant)
        {
            if (descendant == ancestor)
                throw new InvalidOperationException("descendant is the ancestor itself.");
            if (!descendant.IsChildOf(ancestor))
                throw new InvalidOperationException($"'{descendant.name}' is not under '{ancestor.name}'.");

            Transform walk = descendant;
            while (walk.parent != ancestor)
                walk = walk.parent;

            return walk;
        }

        public static Transform ResolveTargetByPath(Transform root, string path)
        {
            if (root == null)
                throw new InvalidOperationException("root is null.");
            if (string.IsNullOrEmpty(path))
                throw new InvalidOperationException("path is empty.");

            var current = root;
            var parts = path.Split('/');
            for (int i = 0; i < parts.Length; i++)
            {
                if (string.IsNullOrEmpty(parts[i])) continue;
                current = current?.Find(parts[i]);
                if (current == null)
                    throw new InvalidOperationException($"path '{path}' not found.");
            }

            return current;
        }
    }
}
