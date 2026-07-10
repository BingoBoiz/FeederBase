using System;
using System.Collections.Generic;
using Sirenix.OdinInspector;
using UnityEngine;

namespace Feeder
{
    public sealed class HierarchyOptionsResult
    {
        public List<ValueDropdownItem<string>> Options { get; }
        public HashSet<string> ConflictPaths { get; }
        public int PrefabCount { get; }

        public HierarchyOptionsResult(
            List<ValueDropdownItem<string>> options,
            HashSet<string> conflictPaths,
            int prefabCount)
        {
            Options = options ?? throw new InvalidOperationException("options is null.");
            ConflictPaths = conflictPaths ?? throw new InvalidOperationException("conflict paths is null.");
            PrefabCount = prefabCount;
        }
    }

    public static class FHierarchyOptionsBuilder
    {
        public static HierarchyOptionsResult Build(IReadOnlyList<GameObject> targetPrefabs)
        {
            if (!(targetPrefabs?.Count > 0))
                throw new InvalidOperationException("target objects is empty.");

            var options = new List<ValueDropdownItem<string>>();
            var conflictPaths = new HashSet<string>();
            int cachedPrefabCount = 0;

            var pathPresence = new Dictionary<string, int>();
            var pathSignatures = new Dictionary<string, HashSet<string>>();
            var allPaths = new HashSet<string>();

            for (int i = 0; i < targetPrefabs.Count; i++)
            {
                var go = targetPrefabs[i];
                if (go == null)
                {
                    Debug.LogWarning($"[FHierarchyOptionsBuilder] Skipping null at targetPrefabs[{i}].");
                    continue;
                }

                var rootTransform = FPrefabRootResolver.GetRootTransform(go, out GameObject prefabRoot, out bool shouldUnload);
                cachedPrefabCount++;

                try
                {
                    var infos = FHierarchyAnalyzeUtils.CollectHierarchyInfo(rootTransform, false);
                    var prefabPaths = new HashSet<string>();

                    for (int infoIndex = 0; infoIndex < infos.Count; infoIndex++)
                    {
                        var info = infos[infoIndex];
                        if (string.IsNullOrEmpty(info.Path))
                            throw new InvalidOperationException("hierarchy path is empty.");

                        allPaths.Add(info.Path);
                        prefabPaths.Add(info.Path);

                        if (!pathSignatures.TryGetValue(info.Path, out var signatureSet))
                        {
                            signatureSet = new HashSet<string>();
                            pathSignatures[info.Path] = signatureSet;
                        }

                        signatureSet.Add(info.ChildSignature);
                    }

                    foreach (var path in prefabPaths)
                    {
                        if (!pathPresence.ContainsKey(path))
                        {
                            pathPresence[path] = 0;
                        }
                        pathPresence[path]++;
                    }
                }
                finally
                {
                    if (shouldUnload && prefabRoot != null)
                    {
                        UnityEditor.PrefabUtility.UnloadPrefabContents(prefabRoot);
                    }
                }
            }

            foreach (var path in allPaths)
            {
                pathPresence.TryGetValue(path, out int count);
                bool missingInAny = cachedPrefabCount > 0 && count != cachedPrefabCount;

                bool signatureConflict = false;
                if (pathSignatures.TryGetValue(path, out var signatures))
                {
                    signatureConflict = signatures.Count > 1;
                }

                if (missingInAny || signatureConflict)
                {
                    conflictPaths.Add(path);
                }
            }

            var sortedPaths = new List<string>(allPaths);
            sortedPaths.Sort(StringComparer.Ordinal);

            for (int i = 0; i < sortedPaths.Count; i++)
            {
                string path = sortedPaths[i];
                string label = conflictPaths.Contains(path) ? "[Conflict] " + path : path;
                options.Add(new ValueDropdownItem<string>(label, path));
            }

            return new HierarchyOptionsResult(options, conflictPaths, cachedPrefabCount);
        }
    }
}
