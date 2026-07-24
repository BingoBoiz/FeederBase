using System;
using System.Collections.Generic;
using Sirenix.OdinInspector;
using UnityEngine;

namespace Feeder
{
    public sealed class HierarchyOptionsResult
    {
        public List<ValueDropdownItem<string>> Options { get; }
        public HashSet<string> PartialPaths { get; }
        public int PrefabCount { get; }
        public bool HasVariants { get; }

        private readonly Dictionary<string, string[]> concreteByMergedPath;

        public HierarchyOptionsResult(
            List<ValueDropdownItem<string>> options,
            HashSet<string> partialPaths,
            int prefabCount,
            bool hasVariants,
            Dictionary<string, string[]> concreteByMergedPath)
        {
            Options = options ?? throw new InvalidOperationException("options is null.");
            PartialPaths = partialPaths ?? throw new InvalidOperationException("partial paths is null.");
            PrefabCount = prefabCount;
            HasVariants = hasVariants;
            this.concreteByMergedPath = concreteByMergedPath ?? throw new InvalidOperationException("concrete path map is null.");
        }

        // per-target concrete name-path (parallel to the target list used at Build time);
        // null result = merged path unknown (stale selection), null element = node absent in that target
        public string[] GetConcretePathsOrNull(string mergedPath)
        {
            if (string.IsNullOrEmpty(mergedPath))
                return null;
            return concreteByMergedPath.TryGetValue(mergedPath, out string[] paths) ? paths : null;
        }
    }

    // Merges the hierarchies of all targets STRUCTURALLY: children matching by name merge into
    // one entry; leftover (name-mismatched) children at the same level are clustered by
    // similarity (subtree shape + name affinity + "named like its own prefab") and each cluster
    // becomes a single variant entry, so N prefabs that each contain a uniquely-named model
    // child produce one selectable option instead of N conflicts. Sibling order is deliberately
    // NOT used for pairing — stray extra nodes (e.g. a junk "4 Variant" sibling before the real
    // model) would shift every index-based pair.
    public static class FHierarchyOptionsBuilder
    {
        // similarity scoring — tuned against Assets\_GameBase\Prefabs\Game\_PetPlayer (107 pets)
        private const float ClusterThreshold = 0.45f;
        private const float WeightChildSet = 0.55f;
        private const float WeightName = 0.15f;
        private const float WeightSize = 0.10f;
        private const float WeightRootAffinity = 0.20f;
        private const float MainBranchFallbackWeight = 0.75f;
        private const float ChildNameMatchThreshold = 0.6f;
        private const int MaxClusterEntries = 200;

        private sealed class NameTreeNode
        {
            public string Name;
            public string RootName;
            public int DescendantCount;
            public readonly List<NameTreeNode> Children = new List<NameTreeNode>();
        }

        private sealed class MergedNode
        {
            public string SegmentKey;
            public bool IsVariant;
            public readonly List<string> DistinctNames = new List<string>();
            public readonly List<(int targetIdx, NameTreeNode node)> Contributors = new List<(int, NameTreeNode)>();
            public List<MergedNode> Children;
        }

        public static HierarchyOptionsResult Build(IReadOnlyList<GameObject> targetPrefabs)
        {
            if (!(targetPrefabs?.Count > 0))
                throw new InvalidOperationException("target objects is empty.");

            int targetCount = targetPrefabs.Count;
            var snapshots = new NameTreeNode[targetCount];
            int prefabCount = 0;

            for (int i = 0; i < targetCount; i++)
            {
                var go = targetPrefabs[i];
                if (go == null)
                {
                    Debug.LogWarning($"[FHierarchyOptionsBuilder] Skipping null at targetPrefabs[{i}].");
                    continue;
                }

                var rootTransform = FPrefabRootResolver.GetRootTransform(go, out GameObject prefabRoot, out bool shouldUnload);
                prefabCount++;

                try
                {
                    snapshots[i] = Snapshot(rootTransform, rootTransform.name);
                }
                finally
                {
                    if (shouldUnload && prefabRoot != null)
                    {
                        UnityEditor.PrefabUtility.UnloadPrefabContents(prefabRoot);
                    }
                }
            }

            var rootContributors = new List<(int targetIdx, NameTreeNode node)>();
            for (int i = 0; i < targetCount; i++)
            {
                if (snapshots[i] != null)
                    rootContributors.Add((i, snapshots[i]));
            }

            List<MergedNode> mergedRoots = MergeLevel(rootContributors);

            var options = new List<ValueDropdownItem<string>>();
            var partialPaths = new HashSet<string>();
            var concreteByMergedPath = new Dictionary<string, string[]>();
            bool hasVariants = false;

            var rootConcrete = new string[targetCount];
            for (int i = 0; i < targetCount; i++)
                rootConcrete[i] = snapshots[i] != null ? "" : null;

            Flatten(mergedRoots, "", "", rootConcrete, prefabCount, options, partialPaths, concreteByMergedPath, ref hasVariants);

            return new HierarchyOptionsResult(options, partialPaths, prefabCount, hasVariants, concreteByMergedPath);
        }

        private static NameTreeNode Snapshot(Transform transform, string rootName)
        {
            var node = new NameTreeNode { Name = transform.name, RootName = rootName };
            int descendants = 0;
            for (int i = 0; i < transform.childCount; i++)
            {
                var child = Snapshot(transform.GetChild(i), rootName);
                node.Children.Add(child);
                descendants += child.DescendantCount + 1;
            }
            node.DescendantCount = descendants;
            return node;
        }

        private static List<MergedNode> MergeLevel(List<(int targetIdx, NameTreeNode parent)> contributors)
        {
            var result = new List<MergedNode>();
            if (contributors.Count == 0)
                return result;

            // duplicate-named siblings within one target: keep the first only (Transform.Find semantics)
            var dedupedChildren = new List<(int targetIdx, List<NameTreeNode> children)>(contributors.Count);
            foreach (var (targetIdx, parent) in contributors)
            {
                var seenNames = new HashSet<string>();
                var children = new List<NameTreeNode>();
                foreach (var child in parent.Children)
                {
                    if (seenNames.Add(child.Name))
                        children.Add(child);
                }
                dedupedChildren.Add((targetIdx, children));
            }

            var byName = new Dictionary<string, List<(int targetIdx, NameTreeNode node)>>();
            var nameOrder = new List<string>();
            foreach (var (targetIdx, children) in dedupedChildren)
            {
                foreach (var child in children)
                {
                    if (!byName.TryGetValue(child.Name, out var group))
                    {
                        group = new List<(int, NameTreeNode)>();
                        byName[child.Name] = group;
                        nameOrder.Add(child.Name);
                    }
                    group.Add((targetIdx, child));
                }
            }

            var usedKeys = new HashSet<string>();

            // name matched by >= 2 targets => common node
            foreach (string name in nameOrder)
            {
                var group = byName[name];
                if (group.Count < 2)
                    continue;

                var node = new MergedNode { SegmentKey = name };
                node.DistinctNames.Add(name);
                node.Contributors.AddRange(group);
                usedKeys.Add(name);
                result.Add(node);
            }

            // leftovers (name unique to one target) clustered across targets by similarity
            var leftoverEntries = new List<(int targetIdx, int siblingIdx, NameTreeNode node)>();
            foreach (var (targetIdx, children) in dedupedChildren)
            {
                for (int s = 0; s < children.Count; s++)
                {
                    if (byName[children[s].Name].Count == 1)
                        leftoverEntries.Add((targetIdx, s, children[s]));
                }
            }

            int variantOrdinal = 0;
            foreach (var cluster in ClusterLeftovers(leftoverEntries))
            {
                var node = new MergedNode();
                foreach (var (targetIdx, _, treeNode) in cluster)
                {
                    node.Contributors.Add((targetIdx, treeNode));
                    node.DistinctNames.Add(treeNode.Name);
                }

                if (node.DistinctNames.Count == 1)
                {
                    node.SegmentKey = node.DistinctNames[0];
                    usedKeys.Add(node.SegmentKey);
                }
                else
                {
                    node.IsVariant = true;
                    string key = "*" + variantOrdinal;
                    variantOrdinal++;
                    while (!usedKeys.Add(key))
                        key += "*";
                    node.SegmentKey = key;
                }
                result.Add(node);
            }

            foreach (var node in result)
            {
                node.Children = MergeLevel(node.Contributors);
            }

            return result;
        }

        // greedy agglomerative clustering: best-scoring cross-target pairs merge first,
        // a cluster never holds two nodes from the same target
        private static List<List<(int targetIdx, int siblingIdx, NameTreeNode node)>> ClusterLeftovers(
            List<(int targetIdx, int siblingIdx, NameTreeNode node)> entries)
        {
            var clusters = new List<List<(int targetIdx, int siblingIdx, NameTreeNode node)>>();
            int n = entries.Count;
            if (n == 0)
                return clusters;

            var parent = new int[n];
            for (int i = 0; i < n; i++)
                parent[i] = i;

            int Find(int i)
            {
                while (parent[i] != i)
                {
                    parent[i] = parent[parent[i]];
                    i = parent[i];
                }
                return i;
            }

            if (n <= MaxClusterEntries)
            {
                var clusterTargets = new List<HashSet<int>>(n);
                for (int i = 0; i < n; i++)
                    clusterTargets.Add(new HashSet<int> { entries[i].targetIdx });

                var pairs = new List<(float score, int a, int b)>();
                for (int a = 0; a < n; a++)
                {
                    for (int b = a + 1; b < n; b++)
                    {
                        if (entries[a].targetIdx == entries[b].targetIdx)
                            continue;

                        float score = ScorePair(entries[a].node, entries[b].node);
                        if (score >= ClusterThreshold)
                            pairs.Add((score, a, b));
                    }
                }

                pairs.Sort((x, y) =>
                {
                    int c = y.score.CompareTo(x.score);
                    if (c != 0) return c;
                    c = x.a.CompareTo(y.a);
                    return c != 0 ? c : x.b.CompareTo(y.b);
                });

                foreach (var (_, a, b) in pairs)
                {
                    int rootA = Find(a);
                    int rootB = Find(b);
                    if (rootA == rootB)
                        continue;
                    if (clusterTargets[rootA].Overlaps(clusterTargets[rootB]))
                        continue;

                    parent[rootB] = rootA;
                    clusterTargets[rootA].UnionWith(clusterTargets[rootB]);
                }
            }
            else
            {
                Debug.LogWarning($"[FHierarchyOptionsBuilder] {n} unmatched nodes at one level exceeds {MaxClusterEntries}; skipping variant clustering there.");
            }

            var membersByRoot = new Dictionary<int, List<(int targetIdx, int siblingIdx, NameTreeNode node)>>();
            var rootOrder = new List<int>();
            for (int i = 0; i < n; i++)
            {
                int root = Find(i);
                if (!membersByRoot.TryGetValue(root, out var members))
                {
                    members = new List<(int, int, NameTreeNode)>();
                    membersByRoot[root] = members;
                    rootOrder.Add(root);
                }
                members.Add(entries[i]);
            }

            foreach (int root in rootOrder)
                clusters.Add(membersByRoot[root]);

            clusters.Sort((x, y) =>
            {
                int c = MinSiblingIdx(x).CompareTo(MinSiblingIdx(y));
                return c != 0 ? c : x[0].targetIdx.CompareTo(y[0].targetIdx);
            });

            return clusters;
        }

        private static int MinSiblingIdx(List<(int targetIdx, int siblingIdx, NameTreeNode node)> cluster)
        {
            int min = int.MaxValue;
            foreach (var (_, siblingIdx, _) in cluster)
                min = Math.Min(min, siblingIdx);
            return min;
        }

        private static float ScorePair(NameTreeNode a, NameTreeNode b)
        {
            float childSetSim = ChildSetSimilarity(a, b);
            float nameSim = NameAffinity(a.Name, b.Name);
            float sizeSim = (Math.Min(a.DescendantCount, b.DescendantCount) + 1f) / (Math.Max(a.DescendantCount, b.DescendantCount) + 1f);
            // "both nodes are named like their own prefab" — the real model branch carries the
            // prefab's name (Axololt ~ Pet_Axolotl), stray junk nodes (Police_Dog (1)) do not
            float rootAffinityPair = Math.Min(NameAffinity(a.Name, a.RootName), NameAffinity(b.Name, b.RootName));

            float structural = WeightChildSet * childSetSim
                               + WeightName * nameSim
                               + WeightSize * sizeSim
                               + WeightRootAffinity * rootAffinityPair;
            return Math.Max(structural, MainBranchFallbackWeight * rootAffinityPair);
        }

        // greedy bipartite match over direct child names; fuzzy so 82_Pet_Agony ~ 82_Pet_Axololt counts
        private static float ChildSetSimilarity(NameTreeNode a, NameTreeNode b)
        {
            int countA = a.Children.Count;
            int countB = b.Children.Count;
            if (countA == 0 || countB == 0)
                return 0f;

            var used = new bool[countB];
            int matched = 0;
            foreach (var childA in a.Children)
            {
                int best = -1;
                float bestAffinity = 0f;
                for (int j = 0; j < countB; j++)
                {
                    if (used[j])
                        continue;

                    float affinity = NameAffinity(childA.Name, b.Children[j].Name);
                    if (affinity > bestAffinity)
                    {
                        bestAffinity = affinity;
                        best = j;
                    }
                }

                if (best >= 0 && bestAffinity >= ChildNameMatchThreshold)
                {
                    used[best] = true;
                    matched++;
                }
            }

            return 2f * matched / (countA + countB);
        }

        // longest common substring (case-insensitive) over the shorter name's length
        private static float NameAffinity(string x, string y)
        {
            if (string.IsNullOrEmpty(x) || string.IsNullOrEmpty(y))
                return 0f;
            if (string.Equals(x, y, StringComparison.OrdinalIgnoreCase))
                return 1f;

            int lenX = x.Length;
            int lenY = y.Length;
            var prev = new int[lenY + 1];
            var curr = new int[lenY + 1];
            int best = 0;

            for (int i = 1; i <= lenX; i++)
            {
                char cx = char.ToLowerInvariant(x[i - 1]);
                for (int j = 1; j <= lenY; j++)
                {
                    if (cx == char.ToLowerInvariant(y[j - 1]))
                    {
                        curr[j] = prev[j - 1] + 1;
                        if (curr[j] > best)
                            best = curr[j];
                    }
                    else
                    {
                        curr[j] = 0;
                    }
                }

                var tmp = prev;
                prev = curr;
                curr = tmp;
            }

            return (float)best / Math.Min(lenX, lenY);
        }

        private static void Flatten(
            List<MergedNode> nodes,
            string valuePrefix,
            string displayPrefix,
            string[] concretePrefix,
            int prefabCount,
            List<ValueDropdownItem<string>> options,
            HashSet<string> partialPaths,
            Dictionary<string, string[]> concreteByMergedPath,
            ref bool hasVariants)
        {
            foreach (var node in nodes)
            {
                string valuePath = valuePrefix.Length == 0 ? node.SegmentKey : valuePrefix + "/" + node.SegmentKey;
                string displayPath = displayPrefix.Length == 0 ? BuildDisplaySegment(node) : displayPrefix + "/" + BuildDisplaySegment(node);

                var concrete = new string[concretePrefix.Length];
                foreach (var (targetIdx, treeNode) in node.Contributors)
                {
                    string prefix = concretePrefix[targetIdx];
                    if (prefix == null)
                        continue;
                    concrete[targetIdx] = prefix.Length == 0 ? treeNode.Name : prefix + "/" + treeNode.Name;
                }

                int presenceCount = node.Contributors.Count;
                bool partial = presenceCount < prefabCount;
                // no '/' anywhere in the marker: Odin splits dropdown tree levels on '/'
                string label = partial ? $"{displayPath} [{presenceCount} of {prefabCount}]" : displayPath;

                if (node.IsVariant)
                    hasVariants = true;
                if (partial)
                    partialPaths.Add(valuePath);

                options.Add(new ValueDropdownItem<string>(label, valuePath));
                concreteByMergedPath[valuePath] = concrete;

                Flatten(node.Children, valuePath, displayPath, concrete, prefabCount, options, partialPaths, concreteByMergedPath, ref hasVariants);
            }
        }

        private static string BuildDisplaySegment(MergedNode node)
        {
            if (!node.IsVariant)
                return node.SegmentKey;

            string prefix = CommonNamePrefix(node.DistinctNames);
            if (prefix.Length >= 3)
            {
                // trim back to the last separator so "82_Pet_A" reads as "82_Pet_*"
                int lastSeparator = prefix.LastIndexOfAny(new[] { '_', ' ', '-', '.' });
                if (lastSeparator + 1 >= 3)
                    prefix = prefix.Substring(0, lastSeparator + 1);
                return prefix + "*";
            }

            int exampleCount = Math.Min(2, node.DistinctNames.Count);
            string examples = string.Join(" | ", node.DistinctNames.GetRange(0, exampleCount));
            if (node.DistinctNames.Count > exampleCount)
                examples += " | …";
            return $"<{examples}>";
        }

        // case-insensitive common prefix, rendered with the first name's casing
        private static string CommonNamePrefix(List<string> names)
        {
            string first = names[0];
            int length = first.Length;
            for (int i = 1; i < names.Count && length > 0; i++)
            {
                string name = names[i];
                int max = Math.Min(length, name.Length);
                int common = 0;
                while (common < max && char.ToLowerInvariant(first[common]) == char.ToLowerInvariant(name[common]))
                    common++;
                length = common;
            }
            return first.Substring(0, length);
        }
    }
}
