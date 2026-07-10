using System;
using System.Collections.Generic;
using MathNet.Numerics.LinearAlgebra.Double;
using UnityEngine;

namespace Feeder
{
    /// <summary>Aligns mesh A (transformA) to mesh B (transformB) using Trimmed ICP; returns world matrix for A.</summary>
    public static class FMeshMatchTransformUtils
    {
        /// <summary>True when we have enough vertices to run TrICP.</summary>
        public static bool CanComputeMatch(Mesh meshA, Mesh meshB)
        {
            if (meshA == null || meshB == null) return false;
            return meshA.vertexCount >= 3 && meshB.vertexCount >= 1;
        }

        /// <summary>TrICP options: overlap fraction and iteration limits.</summary>
        public struct TricpOptions
        {
            public float OverlapFraction;
            public int MaxIterations;
            public float ConvergenceThreshold;

            public static TricpOptions Default => new TricpOptions
            {
                OverlapFraction = 0.9f,
                MaxIterations = 30,
                ConvergenceThreshold = 1e-6f
            };
        }

        /// <summary>Trimmed ICP: returns world matrix for A that best aligns meshA to meshB.</summary>
        public static Matrix4x4 ComputeWorldMatchMatrix(Mesh meshA, Mesh meshB, Transform transformB, Transform transformA, TricpOptions options)
        {
            if (transformA == null || transformB == null) return Matrix4x4.identity;
            if (!CanComputeMatch(meshA, meshB)) return Matrix4x4.identity;

            var vertsA = meshA.vertices;
            var vertsB = meshB.vertices;
            int na = vertsA.Length;
            int nb = vertsB.Length;

            var sourceWorld = new Vector3[na];
            var targetWorld = new Vector3[nb];
            for (int i = 0; i < nb; i++)
                targetWorld[i] = transformB.TransformPoint(vertsB[i]);

            // start from identity so result is "mesh A at origin → align to B", not dependent on transformA
            Matrix4x4 worldA = Matrix4x4.identity;
            float prevError = float.MaxValue;

            for (int iter = 0; iter < options.MaxIterations; iter++)
            {
                for (int i = 0; i < na; i++)
                    sourceWorld[i] = worldA.MultiplyPoint3x4(vertsA[i]);

                var pairs = new List<(int srcIdx, int tgtIdx, float distSq)>(na);
                for (int i = 0; i < na; i++)
                {
                    int nearest = FindNearestPoint(sourceWorld[i], targetWorld, out float distSq);
                    pairs.Add((i, nearest, distSq));
                }

                int keep = Mathf.Max(3, (int)(na * Mathf.Clamp01(options.OverlapFraction)));
                pairs.Sort((a, b) => a.distSq.CompareTo(b.distSq));
                if (pairs.Count > keep)
                    pairs.RemoveRange(keep, pairs.Count - keep);

                var ps = new Vector3[pairs.Count];
                var qs = new Vector3[pairs.Count];
                for (int i = 0; i < pairs.Count; i++)
                {
                    var (srcIdx, tgtIdx, _) = pairs[i];
                    ps[i] = sourceWorld[srcIdx];
                    qs[i] = targetWorld[tgtIdx];
                }

                if (!KabschRigidMathNet.ComputeRigidTransform(ps, qs, out Matrix4x4 Rt))
                {
                    Debug.LogWarning($"[TrICP] Kabsch failed at iter {iter}, stopping.");
                    break;
                }

                worldA = Rt * worldA;

                float meanSq = 0f;
                for (int i = 0; i < pairs.Count; i++)
                {
                    var (srcIdx, _, _) = pairs[i];
                    Vector3 pNew = worldA.MultiplyPoint3x4(vertsA[srcIdx]);
                    Vector3 q = qs[i];
                    meanSq += (pNew - q).sqrMagnitude;
                }
                meanSq /= pairs.Count;
                if (Math.Abs(prevError - meanSq) < options.ConvergenceThreshold)
                {
                    Debug.Log($"[TrICP] Converged at iter {iter}, meanSqError={meanSq}");
                    break;
                }
                prevError = meanSq;
            }

            return worldA;
        }

        /// <summary>Apply world matrix to transform (position, rotation, scale).</summary>
        public static void ApplyWorldMatrixToTransform(Transform t, Matrix4x4 world)
        {
            if (t == null) return;
            t.position = world.GetColumn(3);
            t.rotation = world.rotation;
            var worldScale = new Vector3(
                world.GetColumn(0).magnitude,
                world.GetColumn(1).magnitude,
                world.GetColumn(2).magnitude);
            t.localScale = t.parent != null
                ? new Vector3(
                    worldScale.x / t.parent.lossyScale.x,
                    worldScale.y / t.parent.lossyScale.y,
                    worldScale.z / t.parent.lossyScale.z)
                : worldScale;
        }

        /// <summary>Kabsch rigid (source→target); same formula as MeshKabschAlignMathNetTool.</summary>
        public static bool ComputeRigidTransform(Vector3[] source, Vector3[] target, out Matrix4x4 result) =>
            KabschRigidMathNet.ComputeRigidTransform(source, target, out result);

        private static int FindNearestPoint(Vector3 p, Vector3[] points, out float distSq)
        {
            distSq = float.MaxValue;
            int idx = 0;
            for (int i = 0; i < points.Length; i++)
            {
                float d = (p - points[i]).sqrMagnitude;
                if (d < distSq) { distSq = d; idx = i; }
            }
            return idx;
        }

        /// <summary>Kabsch rigid transform via MathNet SVD (R = V * diag(1,1,sign) * U^T).</summary>
        private static class KabschRigidMathNet
        {
            public static bool ComputeRigidTransform(Vector3[] source, Vector3[] target, out Matrix4x4 result)
            {
                result = Matrix4x4.identity;
                if (source == null || target == null || source.Length != target.Length || source.Length < 3)
                    return false;

                int n = source.Length;
                Vector3 cenP = Vector3.zero;
                Vector3 cenQ = Vector3.zero;
                for (int i = 0; i < n; i++)
                {
                    cenP += source[i];
                    cenQ += target[i];
                }
                cenP /= n;
                cenQ /= n;

                var H = new DenseMatrix(3, 3);
                for (int k = 0; k < n; k++)
                {
                    Vector3 p = source[k] - cenP;
                    Vector3 q = target[k] - cenQ;
                    H[0, 0] += p.x * q.x; H[0, 1] += p.x * q.y; H[0, 2] += p.x * q.z;
                    H[1, 0] += p.y * q.x; H[1, 1] += p.y * q.y; H[1, 2] += p.y * q.z;
                    H[2, 0] += p.z * q.x; H[2, 1] += p.z * q.y; H[2, 2] += p.z * q.z;
                }

                var svd = H.Svd(true);
                var U = svd.U;
                var V = svd.VT.Transpose();

                double det = V.Determinant() * U.Determinant();
                double sign = det >= 0.0 ? 1.0 : -1.0;

                var diag = DenseMatrix.CreateIdentity(3);
                diag[2, 2] = sign;

                var Rm = V * diag * U.Transpose();

                float r00 = (float)Rm[0, 0];
                float r01 = (float)Rm[0, 1];
                float r02 = (float)Rm[0, 2];
                float r10 = (float)Rm[1, 0];
                float r11 = (float)Rm[1, 1];
                float r12 = (float)Rm[1, 2];
                float r20 = (float)Rm[2, 0];
                float r21 = (float)Rm[2, 1];
                float r22 = (float)Rm[2, 2];

                float tx = cenQ.x - (r00 * cenP.x + r01 * cenP.y + r02 * cenP.z);
                float ty = cenQ.y - (r10 * cenP.x + r11 * cenP.y + r12 * cenP.z);
                float tz = cenQ.z - (r20 * cenP.x + r21 * cenP.y + r22 * cenP.z);

                var m = Matrix4x4.identity;
                m.SetColumn(0, new Vector4(r00, r10, r20, 0f));
                m.SetColumn(1, new Vector4(r01, r11, r21, 0f));
                m.SetColumn(2, new Vector4(r02, r12, r22, 0f));
                m.SetColumn(3, new Vector4(tx, ty, tz, 1f));

                result = m;
                return true;
            }
        }
    }
}
