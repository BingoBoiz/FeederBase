#if UNITY_EDITOR
using System.Collections.Generic;

namespace Feeder
{
    /// <summary>
    /// Holds the Mesh Modifier tool state between GUI frames: targets, the analysis report
    /// and the last bake result.
    /// </summary>
    public class FMeshBakeSession
    {
        public readonly List<UnityEngine.Object> Targets = new List<UnityEngine.Object>();

        public FMeshBakeReport Report;
        public FMeshBakeResult LastResult;

        public bool IsAnalyzed => Report != null;

        public void InvalidateAnalysis()
        {
            Report = null;
        }

        public void Reset()
        {
            Targets.Clear();
            Report = null;
            LastResult = null;
        }
    }
}
#endif
