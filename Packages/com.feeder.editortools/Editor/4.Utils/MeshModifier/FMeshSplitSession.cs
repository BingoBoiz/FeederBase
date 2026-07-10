#if UNITY_EDITOR
using System.Collections.Generic;
using UnityEngine;

namespace Feeder
{
    /// <summary>
    /// Live state of the Mesh Splitter tab: target object, analysis and the current
    /// selection for each mode. Shared by reference between the window and the scene overlay.
    /// </summary>
    public sealed class FMeshSplitSession
    {
        public GameObject Target;
        public FMeshSplitAnalysis Analysis;

        /// <summary>Simple mode: selected part ids.</summary>
        public readonly HashSet<int> SelectedPartIds = new HashSet<int>();

        /// <summary>Manual mode: selected canonical (position-welded) vertex ids.</summary>
        public readonly HashSet<int> SelectedCanonicalVerts = new HashSet<int>();

        /// <summary>Advanced mode: the cutting volume.</summary>
        public readonly FSplitPrimitive Primitive = new FSplitPrimitive();

        public FMeshSplitResult LastResult;

        public bool IsAnalyzed => Analysis != null;

        public MeshFilter TargetMeshFilter => Target != null ? Target.GetComponent<MeshFilter>() : null;

        public Mesh TargetMesh
        {
            get
            {
                MeshFilter mf = TargetMeshFilter;
                return mf != null ? mf.sharedMesh : null;
            }
        }

        public void InvalidateAnalysis()
        {
            Analysis = null;
            SelectedPartIds.Clear();
            SelectedCanonicalVerts.Clear();
            LastResult = null;
        }

        public void ClearModeSelection()
        {
            SelectedPartIds.Clear();
            SelectedCanonicalVerts.Clear();
        }

        /// <summary>
        /// Triangle mask (global triangle index) for the current mode's selection.
        /// Simple: triangles of selected parts. Manual: triangles whose 3 corners are all
        /// selected. Advanced: triangles whose world-space centroid is inside the primitive.
        /// </summary>
        public bool[] GetSelectedTriangleMask(FMeshSplitSettings settings)
        {
            FMeshSplitAnalysis a = Analysis;
            if (a == null)
                return null;

            int triCount = a.TriangleCount;
            var mask = new bool[triCount];

            switch (settings.mode)
            {
                case FMeshSplitMode.Simple:
                    if (SelectedPartIds.Count == 0)
                        return mask;
                    for (int t = 0; t < triCount; t++)
                        mask[t] = SelectedPartIds.Contains(a.TrianglePartId[t]);
                    break;

                case FMeshSplitMode.Manual:
                    if (SelectedCanonicalVerts.Count == 0)
                        return mask;
                    for (int t = 0; t < triCount; t++)
                    {
                        int i = t * 3;
                        mask[t] = SelectedCanonicalVerts.Contains(a.PositionWeldMap[a.Triangles[i]]) &&
                                  SelectedCanonicalVerts.Contains(a.PositionWeldMap[a.Triangles[i + 1]]) &&
                                  SelectedCanonicalVerts.Contains(a.PositionWeldMap[a.Triangles[i + 2]]);
                    }
                    break;

                case FMeshSplitMode.Advanced:
                {
                    if (Target == null)
                        return mask;
                    Matrix4x4 localToWorld = Target.transform.localToWorldMatrix;
                    for (int t = 0; t < triCount; t++)
                        mask[t] = Primitive.Contains(localToWorld.MultiplyPoint3x4(a.TriangleLocalCentroids[t]));
                    break;
                }
            }

            return mask;
        }

        public int CountSelectedTriangles(FMeshSplitSettings settings)
        {
            bool[] mask = GetSelectedTriangleMask(settings);
            if (mask == null)
                return 0;
            int count = 0;
            for (int i = 0; i < mask.Length; i++)
                if (mask[i])
                    count++;
            return count;
        }
    }
}
#endif
