#if UNITY_EDITOR
using System.Collections.Generic;
using UnityEditor;
using UnityEngine;

namespace Feeder
{
    /// <summary>
    /// Builds an FMeshSplitAnalysis from a mesh: welds vertices, finds UV islands via
    /// union-find (Unity duplicates vertices along UV seams, so welding by position+UV
    /// reconnects hard-normal splits while staying disconnected across seams — triangle
    /// connectivity over those welded ids is exactly UV-island connectivity), then merges
    /// islands that share the same atlas region into user-facing "parts".
    /// </summary>
    public static class FMeshSplitAnalyzer
    {
        private const string ProgressTitle = "Feeder Mesh Splitter";
        private const int ProgressStep = 8192;

        // Disjoint-set union with path compression + union by size.
        private struct Dsu
        {
            private readonly int[] _parent;
            private readonly int[] _size;

            public Dsu(int count)
            {
                _parent = new int[count];
                _size = new int[count];
                for (int i = 0; i < count; i++)
                {
                    _parent[i] = i;
                    _size[i] = 1;
                }
            }

            public int Find(int x)
            {
                while (_parent[x] != x)
                {
                    _parent[x] = _parent[_parent[x]];
                    x = _parent[x];
                }
                return x;
            }

            public void Union(int a, int b)
            {
                int ra = Find(a);
                int rb = Find(b);
                if (ra == rb) return;
                if (_size[ra] < _size[rb]) (ra, rb) = (rb, ra);
                _parent[rb] = ra;
                _size[ra] += _size[rb];
            }
        }

        public static FMeshSplitAnalysis Analyze(Mesh mesh, FMeshSplitSettings settings)
        {
            if (mesh == null || !mesh.isReadable)
                return null;

            try
            {
                EditorUtility.DisplayProgressBar(ProgressTitle, "Đọc dữ liệu mesh...", 0f);

                var a = new FMeshSplitAnalysis
                {
                    Mesh = mesh,
                    VertexCountSnapshot = mesh.vertexCount,
                    Vertices = mesh.vertices,
                    Uvs = mesh.uv
                };
                a.HasUv = a.Uvs != null && a.Uvs.Length == a.Vertices.Length;

                CollectTriangles(mesh, a);
                a.TriangleCountSnapshot = a.TriangleCount;

                EditorUtility.DisplayProgressBar(ProgressTitle, "Weld vertex...", 0.2f);
                int[] uvWeldMap = BuildWeldMaps(a, out int uvWeldCount);

                EditorUtility.DisplayProgressBar(ProgressTitle, "Tìm UV island...", 0.45f);
                BuildIslands(a, uvWeldMap, uvWeldCount);

                EditorUtility.DisplayProgressBar(ProgressTitle, "Gộp island thành part...", 0.75f);
                Remerge(a, settings);

                EditorUtility.DisplayProgressBar(ProgressTitle, "Tính centroid tam giác...", 0.9f);
                BuildTriangleCentroids(a);

                return a;
            }
            finally
            {
                EditorUtility.ClearProgressBar();
            }
        }

        /// <summary>
        /// Re-runs only the island→part merging (cheap) so the merge-threshold slider
        /// can be tweaked without re-welding. Keeps existing part selection invalid —
        /// callers should clear the session selection.
        /// </summary>
        public static void Remerge(FMeshSplitAnalysis a, FMeshSplitSettings settings)
        {
            int islandCount = a.IslandCount;
            int triCount = a.TriangleCount;

            // Per-island UV bounds, triangle count and submesh.
            var islandBounds = new Rect[islandCount];
            var islandHasBounds = new bool[islandCount];
            var islandTriCount = new int[islandCount];
            var islandSubmesh = new int[islandCount];

            for (int t = 0; t < triCount; t++)
            {
                int island = a.TriangleIslandId[t];
                islandTriCount[island]++;
                islandSubmesh[island] = a.TriangleSubmesh[t];

                if (!a.HasUv)
                    continue;

                for (int k = 0; k < 3; k++)
                {
                    Vector2 uv = a.Uvs[a.Triangles[t * 3 + k]];
                    if (!islandHasBounds[island])
                    {
                        islandBounds[island] = new Rect(uv, Vector2.zero);
                        islandHasBounds[island] = true;
                    }
                    else
                    {
                        Rect r = islandBounds[island];
                        islandBounds[island] = Rect.MinMaxRect(
                            Mathf.Min(r.xMin, uv.x), Mathf.Min(r.yMin, uv.y),
                            Mathf.Max(r.xMax, uv.x), Mathf.Max(r.yMax, uv.y));
                    }
                }
            }

            // Merge islands whose UV bounds overlap enough (repeated elements like leaves
            // share the same atlas region). Sweep by xMin within each submesh group.
            var partDsu = new Dsu(islandCount);
            if (a.HasUv && settings.mergeRepeatedIslands && settings.uvMergeOverlap > 0f)
            {
                var order = new List<int>(islandCount);
                for (int i = 0; i < islandCount; i++)
                    order.Add(i);
                order.Sort((x, y) =>
                {
                    int bySubmesh = islandSubmesh[x].CompareTo(islandSubmesh[y]);
                    return bySubmesh != 0 ? bySubmesh : islandBounds[x].xMin.CompareTo(islandBounds[y].xMin);
                });

                for (int i = 0; i < order.Count; i++)
                {
                    int ia = order[i];
                    Rect ba = islandBounds[ia];
                    for (int j = i + 1; j < order.Count; j++)
                    {
                        int ib = order[j];
                        if (islandSubmesh[ib] != islandSubmesh[ia])
                            break;
                        Rect bb = islandBounds[ib];
                        if (bb.xMin > ba.xMax)
                            break;

                        float overlap = IntersectArea(ba, bb);
                        if (overlap <= 0f)
                            continue;
                        float minArea = Mathf.Max(1e-9f, Mathf.Min(Area(ba), Area(bb)));
                        if (overlap / minArea >= settings.uvMergeOverlap)
                            partDsu.Union(ia, ib);
                    }
                }
            }

            // Remap DSU roots to compact part ids and accumulate part info.
            var rootToPart = new Dictionary<int, int>();
            var partTriCount = new List<int>();
            var partBounds = new List<Rect>();
            var partHasBounds = new List<bool>();
            var islandPart = new int[islandCount];

            for (int i = 0; i < islandCount; i++)
            {
                int root = partDsu.Find(i);
                if (!rootToPart.TryGetValue(root, out int part))
                {
                    part = partTriCount.Count;
                    rootToPart.Add(root, part);
                    partTriCount.Add(0);
                    partBounds.Add(default);
                    partHasBounds.Add(false);
                }
                islandPart[i] = part;

                partTriCount[part] += islandTriCount[i];
                if (islandHasBounds[i])
                {
                    if (!partHasBounds[part])
                    {
                        partBounds[part] = islandBounds[i];
                        partHasBounds[part] = true;
                    }
                    else
                    {
                        Rect r = partBounds[part];
                        Rect b = islandBounds[i];
                        partBounds[part] = Rect.MinMaxRect(
                            Mathf.Min(r.xMin, b.xMin), Mathf.Min(r.yMin, b.yMin),
                            Mathf.Max(r.xMax, b.xMax), Mathf.Max(r.yMax, b.yMax));
                    }
                }
            }

            // Sort parts by descending triangle size so big parts (trunk/leaves) come first,
            // then remap ids to the sorted order.
            int partCount = partTriCount.Count;
            var sortedParts = new List<int>(partCount);
            for (int i = 0; i < partCount; i++)
                sortedParts.Add(i);
            sortedParts.Sort((x, y) => partTriCount[y].CompareTo(partTriCount[x]));

            var oldToNew = new int[partCount];
            for (int i = 0; i < partCount; i++)
                oldToNew[sortedParts[i]] = i;

            a.TrianglePartId = new int[triCount];
            for (int t = 0; t < triCount; t++)
                a.TrianglePartId[t] = oldToNew[islandPart[a.TriangleIslandId[t]]];

            a.Parts.Clear();
            for (int i = 0; i < partCount; i++)
            {
                int old = sortedParts[i];
                a.Parts.Add(new FMeshPartInfo
                {
                    PartId = i,
                    Name = $"Part{i}",
                    Color = Color.HSVToRGB(i * 0.618034f % 1f, 0.75f, 1f),
                    TriangleCount = partTriCount[old],
                    UvBounds = partHasBounds[old] ? partBounds[old] : Rect.zero
                });
            }
        }

        /// <summary>
        /// Lazily builds canonical-vertex adjacency (used by Manual mode's Grow).
        /// </summary>
        public static void EnsureAdjacency(FMeshSplitAnalysis a)
        {
            if (a.CanonicalNeighbors != null)
                return;

            var neighborSets = new HashSet<int>[a.CanonicalCount];
            int triCount = a.TriangleCount;
            for (int t = 0; t < triCount; t++)
            {
                int c0 = a.PositionWeldMap[a.Triangles[t * 3]];
                int c1 = a.PositionWeldMap[a.Triangles[t * 3 + 1]];
                int c2 = a.PositionWeldMap[a.Triangles[t * 3 + 2]];
                AddEdge(neighborSets, c0, c1);
                AddEdge(neighborSets, c1, c2);
                AddEdge(neighborSets, c2, c0);
            }

            a.CanonicalNeighbors = new List<int>[a.CanonicalCount];
            for (int i = 0; i < a.CanonicalCount; i++)
                a.CanonicalNeighbors[i] = neighborSets[i] != null ? new List<int>(neighborSets[i]) : new List<int>();
        }

        private static void AddEdge(HashSet<int>[] sets, int x, int y)
        {
            if (x == y) return;
            (sets[x] ?? (sets[x] = new HashSet<int>())).Add(y);
            (sets[y] ?? (sets[y] = new HashSet<int>())).Add(x);
        }

        // ================================================================ internals

        private static void CollectTriangles(Mesh mesh, FMeshSplitAnalysis a)
        {
            var allTriangles = new List<int>();
            var triangleSubmesh = new List<int>();

            for (int s = 0; s < mesh.subMeshCount; s++)
            {
                int[] tris = mesh.GetTriangles(s);
                allTriangles.AddRange(tris);
                int count = tris.Length / 3;
                for (int i = 0; i < count; i++)
                    triangleSubmesh.Add(s);
            }

            a.Triangles = allTriangles.ToArray();
            a.TriangleSubmesh = triangleSubmesh.ToArray();
        }

        /// <summary>
        /// Builds PositionWeldMap (position-only weld) and returns the position+UV weld map.
        /// </summary>
        private static int[] BuildWeldMaps(FMeshSplitAnalysis a, out int uvWeldCount)
        {
            int vertexCount = a.Vertices.Length;
            float posEps = Mathf.Max(1e-6f, a.Mesh.bounds.size.magnitude * 1e-5f);
            const float uvEps = 1e-4f;

            a.PositionWeldMap = new int[vertexCount];
            var uvWeldMap = new int[vertexCount];
            var posKeyToId = new Dictionary<Vector3Int, int>(vertexCount);
            var uvKeyToId = new Dictionary<(Vector3Int, Vector2Int), int>(vertexCount);
            var canonicalPositions = new List<Vector3>();

            for (int v = 0; v < vertexCount; v++)
            {
                Vector3 p = a.Vertices[v];
                var posKey = new Vector3Int(
                    Mathf.RoundToInt(p.x / posEps),
                    Mathf.RoundToInt(p.y / posEps),
                    Mathf.RoundToInt(p.z / posEps));

                if (!posKeyToId.TryGetValue(posKey, out int posId))
                {
                    posId = canonicalPositions.Count;
                    posKeyToId.Add(posKey, posId);
                    canonicalPositions.Add(p);
                }
                a.PositionWeldMap[v] = posId;

                if (a.HasUv)
                {
                    Vector2 uv = a.Uvs[v];
                    var uvKey = (posKey, new Vector2Int(
                        Mathf.RoundToInt(uv.x / uvEps),
                        Mathf.RoundToInt(uv.y / uvEps)));
                    if (!uvKeyToId.TryGetValue(uvKey, out int uvId))
                    {
                        uvId = uvKeyToId.Count;
                        uvKeyToId.Add(uvKey, uvId);
                    }
                    uvWeldMap[v] = uvId;
                }
                else
                {
                    uvWeldMap[v] = posId;
                }
            }

            a.CanonicalCount = canonicalPositions.Count;
            a.CanonicalPositions = canonicalPositions.ToArray();
            uvWeldCount = a.HasUv ? uvKeyToId.Count : a.CanonicalCount;
            return uvWeldMap;
        }

        /// <summary>
        /// UV-island ids per triangle (never crossing submesh boundaries) and spatial
        /// island roots per canonical vertex.
        /// </summary>
        private static void BuildIslands(FMeshSplitAnalysis a, int[] uvWeldMap, int uvWeldCount)
        {
            int triCount = a.TriangleCount;
            var uvDsu = new Dsu(uvWeldCount);
            var spatialDsu = new Dsu(a.CanonicalCount);

            for (int t = 0; t < triCount; t++)
            {
                if ((t & (ProgressStep - 1)) == 0 && triCount > ProgressStep)
                    EditorUtility.DisplayProgressBar(ProgressTitle, "Tìm UV island...",
                        0.45f + 0.3f * t / triCount);

                int i = t * 3;
                int u0 = uvWeldMap[a.Triangles[i]];
                int u1 = uvWeldMap[a.Triangles[i + 1]];
                int u2 = uvWeldMap[a.Triangles[i + 2]];
                uvDsu.Union(u0, u1);
                uvDsu.Union(u1, u2);

                int c0 = a.PositionWeldMap[a.Triangles[i]];
                int c1 = a.PositionWeldMap[a.Triangles[i + 1]];
                int c2 = a.PositionWeldMap[a.Triangles[i + 2]];
                spatialDsu.Union(c0, c1);
                spatialDsu.Union(c1, c2);
            }

            // Compact (dsu root, submesh) pairs into island ids.
            var keyToIsland = new Dictionary<(int, int), int>();
            a.TriangleIslandId = new int[triCount];
            for (int t = 0; t < triCount; t++)
            {
                var key = (uvDsu.Find(uvWeldMap[a.Triangles[t * 3]]), a.TriangleSubmesh[t]);
                if (!keyToIsland.TryGetValue(key, out int island))
                {
                    island = keyToIsland.Count;
                    keyToIsland.Add(key, island);
                }
                a.TriangleIslandId[t] = island;
            }
            a.IslandCount = keyToIsland.Count;

            a.SpatialIslandOfCanonical = new int[a.CanonicalCount];
            for (int c = 0; c < a.CanonicalCount; c++)
                a.SpatialIslandOfCanonical[c] = spatialDsu.Find(c);
        }

        private static void BuildTriangleCentroids(FMeshSplitAnalysis a)
        {
            int triCount = a.TriangleCount;
            a.TriangleLocalCentroids = new Vector3[triCount];
            for (int t = 0; t < triCount; t++)
            {
                int i = t * 3;
                a.TriangleLocalCentroids[t] =
                    (a.Vertices[a.Triangles[i]] + a.Vertices[a.Triangles[i + 1]] + a.Vertices[a.Triangles[i + 2]]) / 3f;
            }
        }

        private static float Area(Rect r) => r.width * r.height;

        private static float IntersectArea(Rect a, Rect b)
        {
            float w = Mathf.Min(a.xMax, b.xMax) - Mathf.Max(a.xMin, b.xMin);
            float h = Mathf.Min(a.yMax, b.yMax) - Mathf.Max(a.yMin, b.yMin);
            return w > 0f && h > 0f ? w * h : 0f;
        }
    }
}
#endif
