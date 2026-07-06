#if UNITY_EDITOR
using System.Collections.Generic;
using UnityEngine;

namespace Feeder
{
    /// <summary>One source renderer found during analysis.</summary>
    public class FMeshBakeRendererRow
    {
        public GameObject GameObject;
        public string Name;
        public int VertexCount;
        public int TriangleCount;
        public int SubMeshCount;
        public string MaterialNames;
        public string MainTextureInfo;
        public bool HasOutOfBoundsUVs;
        public bool IsMeshReadable = true;
    }

    /// <summary>Result of analyzing the selected targets: per-renderer rows, aggregates and warnings.</summary>
    public class FMeshBakeReport
    {
        public readonly List<FMeshBakeRendererRow> Rows = new List<FMeshBakeRendererRow>();
        public readonly List<string> Warnings = new List<string>();

        public int TotalVertices;
        public int TotalTriangles;
        public int DistinctMaterialCount;
        public int DistinctShaderCount;
        public int DistinctTextureCount;
        /// <summary>Sum of main texture pixel areas (including padding) — rough atlas space estimate.</summary>
        public long EstimatedAtlasPixels;
        public bool HasSceneObjects;
        public bool HasPrefabAssets;
    }

    /// <summary>Asset paths produced by a successful bake.</summary>
    public class FMeshBakeResult
    {
        public string PrefabPath;
        public string MeshPath;
        public string MaterialPath;
        public string BakeResultsPath;
        public readonly List<string> AtlasPaths = new List<string>();
    }
}
#endif
