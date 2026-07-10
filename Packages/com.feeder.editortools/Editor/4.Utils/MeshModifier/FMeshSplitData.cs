#if UNITY_EDITOR
using System.Collections.Generic;
using UnityEngine;

namespace Feeder
{
    public enum FMeshSplitMode
    {
        Simple,
        Manual,
        Advanced
    }

    public enum FSplitPrimitiveKind
    {
        Box,
        Sphere,
        Plane,
        Circle
    }

    /// <summary>
    /// One selectable part of the analyzed mesh (a merged group of UV islands).
    /// </summary>
    public sealed class FMeshPartInfo
    {
        public int PartId;
        public string Name;
        public Color Color;
        public int TriangleCount;
        public Rect UvBounds;
        public bool IncludeInSplit = true;
    }

    /// <summary>
    /// Cutting volume for Advanced mode. Pose is mirrored to a hidden helper GameObject
    /// so the user can move it with Unity's built-in transform gizmos.
    /// Semantics: Box = OBB by Scale; Sphere = radius max(scale)/2; Plane = half-space
    /// along local +Y; Circle = infinite cylinder around local Y, radius max(scale.x, scale.z)/2.
    /// </summary>
    public sealed class FSplitPrimitive
    {
        public FSplitPrimitiveKind Kind = FSplitPrimitiveKind.Box;
        public Vector3 Position;
        public Quaternion Rotation = Quaternion.identity;
        public Vector3 Scale = Vector3.one;

        public bool Contains(Vector3 worldPoint)
        {
            Vector3 local = Quaternion.Inverse(Rotation) * (worldPoint - Position);
            switch (Kind)
            {
                case FSplitPrimitiveKind.Box:
                    return Mathf.Abs(local.x) <= Mathf.Abs(Scale.x) * 0.5f &&
                           Mathf.Abs(local.y) <= Mathf.Abs(Scale.y) * 0.5f &&
                           Mathf.Abs(local.z) <= Mathf.Abs(Scale.z) * 0.5f;
                case FSplitPrimitiveKind.Sphere:
                {
                    float r = SphereRadius;
                    return local.sqrMagnitude <= r * r;
                }
                case FSplitPrimitiveKind.Plane:
                    return local.y >= 0f;
                case FSplitPrimitiveKind.Circle:
                {
                    float r = CircleRadius;
                    return local.x * local.x + local.z * local.z <= r * r;
                }
                default:
                    return false;
            }
        }

        public float SphereRadius => Mathf.Max(Mathf.Abs(Scale.x), Mathf.Abs(Scale.y), Mathf.Abs(Scale.z)) * 0.5f;
        public float CircleRadius => Mathf.Max(Mathf.Abs(Scale.x), Mathf.Abs(Scale.z)) * 0.5f;
    }

    /// <summary>
    /// Result of analyzing one mesh for splitting: cached geometry arrays, weld maps,
    /// UV-island part assignment and spatial-island data for Manual mode.
    /// Triangle indices used everywhere are GLOBAL (concatenated across submeshes).
    /// </summary>
    public sealed class FMeshSplitAnalysis
    {
        public Mesh Mesh;
        public int VertexCountSnapshot;
        public int TriangleCountSnapshot;

        public Vector3[] Vertices;
        public Vector2[] Uvs;
        public bool HasUv;

        /// <summary>Concatenated triangle corner indices (3 per triangle) across all submeshes.</summary>
        public int[] Triangles;
        /// <summary>Global triangle index -> submesh index.</summary>
        public int[] TriangleSubmesh;
        /// <summary>Global triangle index -> UV island id (before part merging).</summary>
        public int[] TriangleIslandId;
        public int IslandCount;
        /// <summary>Global triangle index -> part id (after merging repeated islands).</summary>
        public int[] TrianglePartId;
        public List<FMeshPartInfo> Parts = new List<FMeshPartInfo>();

        /// <summary>Raw vertex index -> canonical (position-welded) vertex id.</summary>
        public int[] PositionWeldMap;
        public int CanonicalCount;
        /// <summary>One representative local position per canonical id.</summary>
        public Vector3[] CanonicalPositions;
        /// <summary>Canonical id -> spatial connectivity island root (for "Select Linked").</summary>
        public int[] SpatialIslandOfCanonical;
        /// <summary>Canonical id -> adjacent canonical ids. Built lazily by FMeshSplitAnalyzer.EnsureAdjacency.</summary>
        public List<int>[] CanonicalNeighbors;

        /// <summary>Per-triangle local-space centroid, precomputed for Advanced-mode volume tests.</summary>
        public Vector3[] TriangleLocalCentroids;

        public int TriangleCount => Triangles != null ? Triangles.Length / 3 : 0;

        /// <summary>The analysis is stale when the mesh was edited or destroyed after Analyze.</summary>
        public bool IsStale
        {
            get
            {
                if (Mesh == null || Mesh.vertexCount != VertexCountSnapshot)
                    return true;

                long indexCount = 0;
                for (int s = 0; s < Mesh.subMeshCount; s++)
                    indexCount += Mesh.GetIndexCount(s);
                return indexCount / 3 != TriangleCountSnapshot;
            }
        }
    }

    public sealed class FMeshSplitResult
    {
        public string PrefabPath;
        public readonly List<string> MeshPaths = new List<string>();
    }
}
#endif
