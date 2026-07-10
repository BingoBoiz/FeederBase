using System;
using System.Collections.Generic;
using UnityEngine;

namespace Feeder
{
    public static class FDeduplicateMeshUtils
    {
        /// <summary>
        /// Tolerance as fraction of reference mesh bounds size (e.g. 0.1 = 10%).
        /// Each vertex in b must be within (tolerance * refBoundsSize) of corresponding vertex in a.
        /// </summary>
        public static bool AreMeshesVertexSimilar(Mesh a, Mesh b, float tolerancePercent = 0.1f)
        {
            if (a == null || b == null)
                throw new InvalidOperationException("mesh a or b is null.");
            if (tolerancePercent < 0f || tolerancePercent > 1f)
                throw new ArgumentOutOfRangeException(nameof(tolerancePercent), "tolerancePercent must be in [0, 1].");

            var va = a.vertices;
            var vb = b.vertices;
            if (va.Length != vb.Length || va.Length == 0)
                return false;

            var boundsSize = a.bounds.size.magnitude;
            if (boundsSize < 0.0001f)
                boundsSize = 1f;
            var maxDelta = tolerancePercent * boundsSize;

            for (int i = 0; i < va.Length; i++)
            {
                if (Vector3.SqrMagnitude(va[i] - vb[i]) > maxDelta * maxDelta)
                    return false;
            }
            return true;
        }

        /// <summary>
        /// Finds first mesh in list, from index (exclusive) downward, that is vertex-similar to reference.
        /// Returns the index, or -1 if none.
        /// </summary>
        public static int FindSimilarMeshIndex(IReadOnlyList<Mesh> list, Mesh reference, int fromIndexExclusive, float tolerancePercent = 0.1f)
        {
            if (list == null)
                throw new InvalidOperationException("list is null.");
            if (reference == null)
                throw new InvalidOperationException("reference mesh is null.");
            if (fromIndexExclusive < -1 || fromIndexExclusive >= list.Count)
                throw new ArgumentOutOfRangeException(nameof(fromIndexExclusive));

            for (int i = fromIndexExclusive + 1; i < list.Count; i++)
            {
                var m = list[i];
                if (m == null) continue;
                if (AreMeshesVertexSimilar(reference, m, tolerancePercent))
                    return i;
            }
            return -1;
        }
    }
}
