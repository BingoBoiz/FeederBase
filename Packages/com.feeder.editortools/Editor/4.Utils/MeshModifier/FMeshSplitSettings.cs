#if UNITY_EDITOR
using System;
using UnityEngine;

namespace Feeder
{
    /// <summary>
    /// Options for the Mesh Splitter tab: split mode, per-mode parameters and output paths.
    /// </summary>
    [Serializable]
    public class FMeshSplitSettings
    {
        public FMeshSplitMode mode = FMeshSplitMode.Simple;

        // Simple (UV islands)
        public bool mergeRepeatedIslands = true;
        [Range(0f, 1f)] public float uvMergeOverlap = 0.6f;

        // Manual (vertex picking)
        public float pickRadiusPixels = 12f;
        public float brushRadiusPixels = 30f;

        // Advanced (primitive volume)
        public FSplitPrimitiveKind primitiveKind = FSplitPrimitiveKind.Box;

        // Output
        public string outputFolder = "Assets/Generated Split Meshes";
        public string baseName = "SplitMesh";
        public bool placeResultInScene = true;

        public string PartFolder => $"{outputFolder}/{baseName}";
        public string PrefabPath => $"{PartFolder}/{baseName}.prefab";
        public string PartMeshPath(string partName) => $"{PartFolder}/{baseName}_{partName}.asset";
    }
}
#endif
