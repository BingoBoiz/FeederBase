using System;
using System.Collections.Generic;
using UnityEngine;

namespace Feeder
{
    public static class FMeshAutoAlignUtils
    {
        public enum SamplingMode
        {
            Vertices,
            SurfaceArea
        }

        public struct AutoAlignOptions
        {
            public SamplingMode Sampling;
            public int MaxBasePoints;
            public int MaxRansacIterations;
            public int MaxRansacCandidates;
            public int MaxRefinedCandidates;
            public float RansacDistanceTolerance;
            public float TrimmedOverlap;
            public float NormalWeight;
            public float AcceptScorePercentOfTargetRadius;
            public float MinCoverage;
            public int RandomSeed;

            public static AutoAlignOptions Default => new AutoAlignOptions
            {
                Sampling = SamplingMode.SurfaceArea,
                MaxBasePoints = 1536,
                MaxRansacIterations = 420,
                MaxRansacCandidates = 28,
                MaxRefinedCandidates = 14,
                RansacDistanceTolerance = 0.12f,
                TrimmedOverlap = 0.82f,
                NormalWeight = 0.08f,
                AcceptScorePercentOfTargetRadius = 0.08f,
                MinCoverage = 0.58f,
                RandomSeed = 917521
            };
        }

        public struct AutoAlignResult
        {
            public bool Success;
            public Matrix4x4 SourceScaledToWorld;
            public Vector3 Position;
            public Quaternion Rotation;
            public Vector3 LossyScale;
            public float Score;
            public float Coverage;
            public int CandidateCount;
            public int RefinedCandidateCount;
            public string BestStage;
            public string FailureReason;
        }

        private struct PointCloud
        {
            public Vector3[] Points;
            public Vector3[] Normals;
            public bool HasNormals;
            public Vector3 Centroid;
            public float Radius;
        }

        private struct Candidate
        {
            public Matrix4x4 Matrix;
            public string Stage;
            public float CoarseScore;
        }

        private struct ScoreResult
        {
            public float Score;
            public float Coverage;
        }

        private static readonly int[][] AxisPermutations =
        {
            new[] { 0, 1, 2 },
            new[] { 0, 2, 1 },
            new[] { 1, 0, 2 },
            new[] { 1, 2, 0 },
            new[] { 2, 0, 1 },
            new[] { 2, 1, 0 }
        };

        public static bool TryAutoAlign(
            Mesh sourceMesh,
            Mesh targetMesh,
            Transform targetTransform,
            Vector3 desiredLossyScale,
            AutoAlignOptions options,
            out AutoAlignResult result)
        {
            result = new AutoAlignResult
            {
                Success = false,
                LossyScale = desiredLossyScale,
                FailureReason = "Auto align did not run."
            };

            if (sourceMesh == null || targetMesh == null)
            {
                result.FailureReason = "Source or target mesh is null.";
                return false;
            }

            if (targetTransform == null)
            {
                result.FailureReason = "Target transform is null.";
                return false;
            }

            PointCloud sourceLocal = BuildPointCloud(sourceMesh, options.MaxBasePoints, options.Sampling);
            PointCloud targetWorld = BuildPointCloud(targetMesh, options.MaxBasePoints, options.Sampling, targetTransform.localToWorldMatrix);
            if (sourceLocal.Points.Length < 3 || targetWorld.Points.Length < 3)
            {
                result.FailureReason = "Not enough sampled points.";
                return false;
            }

            PointCloud sourceScaled = ScalePointCloud(sourceLocal, desiredLossyScale);
            if (sourceScaled.Radius <= 0.000001f || targetWorld.Radius <= 0.000001f)
            {
                result.FailureReason = "Source or target point cloud is degenerate.";
                return false;
            }

            var candidates = new List<Candidate>(64);
            AddPcaCandidates(sourceScaled, targetWorld, candidates);
            AddRansacCandidates(sourceScaled, targetWorld, options, candidates);

            if (candidates.Count == 0)
            {
                result.FailureReason = "No transform candidates were generated.";
                return false;
            }

            DeduplicateCandidates(candidates, targetWorld.Radius * 0.015f, 0.75f);
            ScoreCoarseCandidates(sourceScaled, targetWorld, candidates, options);
            candidates.Sort((a, b) => a.CoarseScore.CompareTo(b.CoarseScore));

            int refineCount = Mathf.Min(Mathf.Max(1, options.MaxRefinedCandidates), candidates.Count);
            Candidate best = default;
            ScoreResult bestScore = new ScoreResult { Score = float.MaxValue, Coverage = 0f };
            int[] levelCounts = BuildLevelCounts(sourceScaled.Points.Length, targetWorld.Points.Length);

            for (int i = 0; i < refineCount; i++)
            {
                Candidate refined = candidates[i];
                refined.Matrix = RunMultiScaleTrimmedIcp(sourceScaled, targetWorld, refined.Matrix, options, levelCounts);
                ScoreResult score = ScoreAlignment(sourceScaled, targetWorld, refined.Matrix, options, levelCounts[levelCounts.Length - 1]);
                if (score.Score < bestScore.Score)
                {
                    best = refined;
                    bestScore = score;
                }
            }

            float acceptScore = Mathf.Max(0.00001f, targetWorld.Radius * Mathf.Max(0.001f, options.AcceptScorePercentOfTargetRadius));
            bool accepted = bestScore.Score <= acceptScore && bestScore.Coverage >= options.MinCoverage;

            result.SourceScaledToWorld = best.Matrix;
            result.Position = best.Matrix.GetColumn(3);
            result.Rotation = SafeRotation(best.Matrix);
            result.Score = bestScore.Score;
            result.Coverage = bestScore.Coverage;
            result.CandidateCount = candidates.Count;
            result.RefinedCandidateCount = refineCount;
            result.BestStage = best.Stage;

            if (!accepted)
            {
                result.FailureReason =
                    $"Best score {bestScore.Score:0.#####}, coverage {bestScore.Coverage:P0}; threshold {acceptScore:0.#####}, min coverage {options.MinCoverage:P0}.";
                return false;
            }

            result.Success = true;
            result.FailureReason = string.Empty;
            return true;
        }

        private static PointCloud BuildPointCloud(Mesh mesh, int maxPoints, SamplingMode sampling, Matrix4x4? transform = null)
        {
            if (sampling == SamplingMode.SurfaceArea)
            {
                PointCloud sampled = BuildSurfacePointCloud(mesh, maxPoints, transform);
                if (sampled.Points.Length >= 3)
                    return sampled;
            }

            return BuildVertexPointCloud(mesh, maxPoints, transform);
        }

        private static PointCloud BuildVertexPointCloud(Mesh mesh, int maxPoints, Matrix4x4? transform)
        {
            Vector3[] verts = mesh.vertices ?? Array.Empty<Vector3>();
            Vector3[] meshNormals = mesh.normals ?? Array.Empty<Vector3>();
            int count = Mathf.Min(Mathf.Max(3, maxPoints), verts.Length);
            if (verts.Length == 0)
                return EmptyPointCloud();

            var points = new Vector3[count];
            var normals = new Vector3[count];
            bool hasNormals = meshNormals.Length == verts.Length;
            Matrix4x4 matrix = transform ?? Matrix4x4.identity;

            for (int i = 0; i < count; i++)
            {
                int src = PickEvenIndex(i, count, verts.Length);
                points[i] = matrix.MultiplyPoint3x4(verts[src]);
                if (hasNormals)
                    normals[i] = matrix.MultiplyVector(meshNormals[src]).normalized;
            }

            return FinalizePointCloud(points, normals, hasNormals);
        }

        private static PointCloud BuildSurfacePointCloud(Mesh mesh, int maxPoints, Matrix4x4? transform)
        {
            Vector3[] verts = mesh.vertices ?? Array.Empty<Vector3>();
            if (verts.Length < 3)
                return EmptyPointCloud();

            var triangles = new List<int>();
            for (int i = 0; i < mesh.subMeshCount; i++)
            {
                int[] indices = mesh.GetTriangles(i);
                if (indices != null && indices.Length >= 3)
                    triangles.AddRange(indices);
            }

            int triangleCount = triangles.Count / 3;
            if (triangleCount == 0)
                return EmptyPointCloud();

            var cumulativeAreas = new float[triangleCount];
            float totalArea = 0f;
            for (int i = 0; i < triangleCount; i++)
            {
                Vector3 a = verts[triangles[i * 3]];
                Vector3 b = verts[triangles[i * 3 + 1]];
                Vector3 c = verts[triangles[i * 3 + 2]];
                float area = Vector3.Cross(b - a, c - a).magnitude * 0.5f;
                totalArea += area;
                cumulativeAreas[i] = totalArea;
            }

            if (totalArea <= 0.0000001f)
                return EmptyPointCloud();

            int count = Mathf.Max(3, maxPoints);
            var points = new Vector3[count];
            var normals = new Vector3[count];
            Matrix4x4 matrix = transform ?? Matrix4x4.identity;

            for (int i = 0; i < count; i++)
            {
                float areaCursor = ((i + 0.5f) / count) * totalArea;
                int tri = LowerBound(cumulativeAreas, areaCursor);
                int ia = triangles[tri * 3];
                int ib = triangles[tri * 3 + 1];
                int ic = triangles[tri * 3 + 2];

                float u = Halton(i + 1, 2);
                float v = Halton(i + 1, 3);
                if (u + v > 1f)
                {
                    u = 1f - u;
                    v = 1f - v;
                }

                Vector3 a = verts[ia];
                Vector3 b = verts[ib];
                Vector3 c = verts[ic];
                Vector3 p = a + (b - a) * u + (c - a) * v;
                Vector3 n = Vector3.Cross(b - a, c - a).normalized;

                points[i] = matrix.MultiplyPoint3x4(p);
                normals[i] = matrix.MultiplyVector(n).normalized;
            }

            return FinalizePointCloud(points, normals, true);
        }

        private static PointCloud ScalePointCloud(PointCloud source, Vector3 scale)
        {
            var points = new Vector3[source.Points.Length];
            var normals = new Vector3[source.Points.Length];
            for (int i = 0; i < points.Length; i++)
            {
                Vector3 p = source.Points[i];
                points[i] = new Vector3(p.x * scale.x, p.y * scale.y, p.z * scale.z);

                if (source.HasNormals)
                {
                    Vector3 n = source.Normals[i];
                    normals[i] = new Vector3(
                        scale.x != 0f ? n.x / scale.x : n.x,
                        scale.y != 0f ? n.y / scale.y : n.y,
                        scale.z != 0f ? n.z / scale.z : n.z).normalized;
                }
            }

            return FinalizePointCloud(points, normals, source.HasNormals);
        }

        private static PointCloud FinalizePointCloud(Vector3[] points, Vector3[] normals, bool hasNormals)
        {
            if (points == null || points.Length == 0)
                return EmptyPointCloud();

            Vector3 centroid = Vector3.zero;
            for (int i = 0; i < points.Length; i++)
                centroid += points[i];
            centroid /= points.Length;

            float radiusSq = 0f;
            for (int i = 0; i < points.Length; i++)
                radiusSq += (points[i] - centroid).sqrMagnitude;
            float radius = Mathf.Sqrt(radiusSq / points.Length);

            return new PointCloud
            {
                Points = points,
                Normals = normals ?? Array.Empty<Vector3>(),
                HasNormals = hasNormals && normals != null && normals.Length == points.Length,
                Centroid = centroid,
                Radius = radius
            };
        }

        private static PointCloud EmptyPointCloud()
        {
            return new PointCloud
            {
                Points = Array.Empty<Vector3>(),
                Normals = Array.Empty<Vector3>(),
                HasNormals = false,
                Centroid = Vector3.zero,
                Radius = 0f
            };
        }

        private static void AddPcaCandidates(PointCloud source, PointCloud target, List<Candidate> candidates)
        {
            Vector3[] sourceAxes = ComputePcaAxes(source);
            Vector3[] targetAxes = ComputePcaAxes(target);
            Matrix4x4 targetBasis = BasisMatrix(targetAxes);

            for (int p = 0; p < AxisPermutations.Length; p++)
            {
                int[] perm = AxisPermutations[p];
                int permSign = PermutationSign(perm);
                for (int sx = -1; sx <= 1; sx += 2)
                for (int sy = -1; sy <= 1; sy += 2)
                for (int sz = -1; sz <= 1; sz += 2)
                {
                    if (permSign * sx * sy * sz < 0)
                        continue;

                    Vector3[] variant =
                    {
                        sourceAxes[perm[0]] * sx,
                        sourceAxes[perm[1]] * sy,
                        sourceAxes[perm[2]] * sz
                    };

                    Matrix4x4 sourceBasis = BasisMatrix(variant);
                    Matrix4x4 rotationMatrix = targetBasis * sourceBasis.transpose;
                    Quaternion rotation = SafeRotation(rotationMatrix);
                    Vector3 position = target.Centroid - rotation * source.Centroid;
                    Matrix4x4 matrix = Matrix4x4.TRS(position, rotation, Vector3.one);

                    candidates.Add(new Candidate
                    {
                        Matrix = matrix,
                        Stage = "PCA",
                        CoarseScore = float.MaxValue
                    });
                }
            }
        }

        private static void AddRansacCandidates(PointCloud source, PointCloud target, AutoAlignOptions options, List<Candidate> candidates)
        {
            int maxCandidates = Mathf.Max(0, options.MaxRansacCandidates);
            if (maxCandidates == 0 || source.Points.Length < 4 || target.Points.Length < 4)
                return;

            var random = new System.Random(options.RandomSeed);
            int added = 0;
            int iterations = Mathf.Max(0, options.MaxRansacIterations);

            for (int iter = 0; iter < iterations && added < maxCandidates; iter++)
            {
                int[] src = PickDistinctTriple(random, source.Points.Length);
                int[] dst = PickDistinctTriple(random, target.Points.Length);
                if (!IsUsefulTriangle(source.Points, src) || !IsUsefulTriangle(target.Points, dst))
                    continue;

                float[] srcSig = TriangleSignature(source.Points, src, source.Radius);
                float[] dstSig = TriangleSignature(target.Points, dst, target.Radius);
                if (!AreSignaturesClose(srcSig, dstSig, options.RansacDistanceTolerance))
                    continue;

                int[][] targetOrders =
                {
                    new[] { dst[0], dst[1], dst[2] },
                    new[] { dst[0], dst[2], dst[1] },
                    new[] { dst[1], dst[0], dst[2] },
                    new[] { dst[1], dst[2], dst[0] },
                    new[] { dst[2], dst[0], dst[1] },
                    new[] { dst[2], dst[1], dst[0] }
                };

                for (int order = 0; order < targetOrders.Length && added < maxCandidates; order++)
                {
                    var ps = new[]
                    {
                        source.Points[src[0]],
                        source.Points[src[1]],
                        source.Points[src[2]]
                    };
                    var qs = new[]
                    {
                        target.Points[targetOrders[order][0]],
                        target.Points[targetOrders[order][1]],
                        target.Points[targetOrders[order][2]]
                    };

                    if (!FMeshMatchTransformUtils.ComputeRigidTransform(ps, qs, out Matrix4x4 matrix))
                        continue;

                    candidates.Add(new Candidate
                    {
                        Matrix = matrix,
                        Stage = "RANSAC",
                        CoarseScore = float.MaxValue
                    });
                    added++;
                }
            }
        }

        private static void ScoreCoarseCandidates(PointCloud source, PointCloud target, List<Candidate> candidates, AutoAlignOptions options)
        {
            int count = Mathf.Min(220, Mathf.Min(source.Points.Length, target.Points.Length));
            for (int i = 0; i < candidates.Count; i++)
            {
                Candidate candidate = candidates[i];
                candidate.CoarseScore = ScoreAlignment(source, target, candidate.Matrix, options, count).Score;
                candidates[i] = candidate;
            }
        }

        private static Matrix4x4 RunMultiScaleTrimmedIcp(PointCloud source, PointCloud target, Matrix4x4 initial, AutoAlignOptions options, int[] levelCounts)
        {
            Matrix4x4 current = initial;
            for (int level = 0; level < levelCounts.Length; level++)
            {
                int count = levelCounts[level];
                int maxIterations = level == 0 ? 18 : level == 1 ? 14 : 10;
                current = RunTrimmedIcpLevel(source, target, current, options, count, maxIterations);
            }

            return current;
        }

        private static Matrix4x4 RunTrimmedIcpLevel(PointCloud source, PointCloud target, Matrix4x4 initial, AutoAlignOptions options, int count, int maxIterations)
        {
            Matrix4x4 current = initial;
            Vector3[] srcLevel = SelectEvenPoints(source.Points, count);
            Vector3[] dstLevel = SelectEvenPoints(target.Points, count);
            if (srcLevel.Length < 3 || dstLevel.Length < 3)
                return current;

            var pairs = new List<(int srcIdx, int dstIdx, float distSq)>(srcLevel.Length);
            Vector3[] worldSource = new Vector3[srcLevel.Length];

            for (int iter = 0; iter < maxIterations; iter++)
            {
                pairs.Clear();
                for (int i = 0; i < srcLevel.Length; i++)
                {
                    worldSource[i] = current.MultiplyPoint3x4(srcLevel[i]);
                    int nearest = FindNearestPoint(worldSource[i], dstLevel, out float distSq);
                    pairs.Add((i, nearest, distSq));
                }

                pairs.Sort((a, b) => a.distSq.CompareTo(b.distSq));
                int keep = Mathf.Clamp((int)(pairs.Count * Mathf.Clamp01(options.TrimmedOverlap)), 3, pairs.Count);
                var ps = new Vector3[keep];
                var qs = new Vector3[keep];
                for (int i = 0; i < keep; i++)
                {
                    ps[i] = worldSource[pairs[i].srcIdx];
                    qs[i] = dstLevel[pairs[i].dstIdx];
                }

                if (!FMeshMatchTransformUtils.ComputeRigidTransform(ps, qs, out Matrix4x4 delta))
                    break;

                current = delta * current;
                if (IsDeltaConverged(delta))
                    break;
            }

            return current;
        }

        private static ScoreResult ScoreAlignment(PointCloud source, PointCloud target, Matrix4x4 matrix, AutoAlignOptions options, int count)
        {
            Vector3[] srcPoints = SelectEvenPoints(source.Points, count);
            Vector3[] dstPoints = SelectEvenPoints(target.Points, count);
            Vector3[] srcNormals = source.HasNormals ? SelectEvenPoints(source.Normals, count) : Array.Empty<Vector3>();
            Vector3[] dstNormals = target.HasNormals ? SelectEvenPoints(target.Normals, count) : Array.Empty<Vector3>();
            if (srcPoints.Length == 0 || dstPoints.Length == 0)
                return new ScoreResult { Score = float.MaxValue, Coverage = 0f };

            float gate = Mathf.Max(0.0001f, target.Radius * 0.08f);
            float gateSq = gate * gate;
            float forward = ScoreOneWay(srcPoints, srcNormals, dstPoints, dstNormals, matrix, true, options, gateSq, out float forwardCoverage);
            Matrix4x4 inverse = matrix.inverse;
            float backward = ScoreOneWay(dstPoints, dstNormals, srcPoints, srcNormals, inverse, true, options, gateSq, out float backwardCoverage);

            return new ScoreResult
            {
                Score = (forward + backward) * 0.5f,
                Coverage = (forwardCoverage + backwardCoverage) * 0.5f
            };
        }

        private static float ScoreOneWay(
            Vector3[] sourcePoints,
            Vector3[] sourceNormals,
            Vector3[] targetPoints,
            Vector3[] targetNormals,
            Matrix4x4 sourceToTarget,
            bool transformNormals,
            AutoAlignOptions options,
            float coverageGateSq,
            out float coverage)
        {
            var distances = new float[sourcePoints.Length];
            int covered = 0;
            bool useNormals = sourceNormals.Length == sourcePoints.Length && targetNormals.Length == targetPoints.Length;

            for (int i = 0; i < sourcePoints.Length; i++)
            {
                Vector3 p = sourceToTarget.MultiplyPoint3x4(sourcePoints[i]);
                int nearest = FindNearestPoint(p, targetPoints, out float distSq);
                if (distSq <= coverageGateSq)
                    covered++;

                float distance = Mathf.Sqrt(distSq);
                if (useNormals && options.NormalWeight > 0f)
                {
                    Vector3 n = transformNormals
                        ? sourceToTarget.MultiplyVector(sourceNormals[i]).normalized
                        : sourceNormals[i];
                    float normalPenalty = 1f - Mathf.Abs(Vector3.Dot(n, targetNormals[nearest]));
                    distance += normalPenalty * options.NormalWeight * Mathf.Sqrt(coverageGateSq);
                }

                distances[i] = distance;
            }

            Array.Sort(distances);
            int keep = Mathf.Clamp((int)(distances.Length * Mathf.Clamp01(options.TrimmedOverlap)), 1, distances.Length);
            float sum = 0f;
            for (int i = 0; i < keep; i++)
                sum += distances[i];

            coverage = sourcePoints.Length > 0 ? covered / (float)sourcePoints.Length : 0f;
            return sum / keep;
        }

        private static Vector3[] ComputePcaAxes(PointCloud cloud)
        {
            float[,] matrix = new float[3, 3];
            for (int i = 0; i < cloud.Points.Length; i++)
            {
                Vector3 p = cloud.Points[i] - cloud.Centroid;
                matrix[0, 0] += p.x * p.x;
                matrix[0, 1] += p.x * p.y;
                matrix[0, 2] += p.x * p.z;
                matrix[1, 1] += p.y * p.y;
                matrix[1, 2] += p.y * p.z;
                matrix[2, 2] += p.z * p.z;
            }

            matrix[1, 0] = matrix[0, 1];
            matrix[2, 0] = matrix[0, 2];
            matrix[2, 1] = matrix[1, 2];

            JacobiEigenSymmetric3x3(matrix, out float[] values, out Vector3[] vectors);
            Array.Sort(values, vectors);
            Array.Reverse(vectors);

            Vector3 x = vectors[0].normalized;
            Vector3 y = MakeOrthogonal(vectors[1], x);
            Vector3 z = Vector3.Cross(x, y).normalized;
            y = Vector3.Cross(z, x).normalized;

            return new[] { x, y, z };
        }

        private static void JacobiEigenSymmetric3x3(float[,] source, out float[] values, out Vector3[] vectors)
        {
            float[,] a = (float[,])source.Clone();
            float[,] v =
            {
                { 1f, 0f, 0f },
                { 0f, 1f, 0f },
                { 0f, 0f, 1f }
            };

            for (int iter = 0; iter < 24; iter++)
            {
                int p = 0;
                int q = 1;
                float max = Mathf.Abs(a[0, 1]);
                if (Mathf.Abs(a[0, 2]) > max) { p = 0; q = 2; max = Mathf.Abs(a[0, 2]); }
                if (Mathf.Abs(a[1, 2]) > max) { p = 1; q = 2; max = Mathf.Abs(a[1, 2]); }
                if (max < 0.0000001f)
                    break;

                float app = a[p, p];
                float aqq = a[q, q];
                float apq = a[p, q];
                float phi = 0.5f * Mathf.Atan2(2f * apq, aqq - app);
                float c = Mathf.Cos(phi);
                float s = Mathf.Sin(phi);

                for (int k = 0; k < 3; k++)
                {
                    float akp = a[k, p];
                    float akq = a[k, q];
                    a[k, p] = c * akp - s * akq;
                    a[k, q] = s * akp + c * akq;
                }

                for (int k = 0; k < 3; k++)
                {
                    float apk = a[p, k];
                    float aqk = a[q, k];
                    a[p, k] = c * apk - s * aqk;
                    a[q, k] = s * apk + c * aqk;
                }

                for (int k = 0; k < 3; k++)
                {
                    float vkp = v[k, p];
                    float vkq = v[k, q];
                    v[k, p] = c * vkp - s * vkq;
                    v[k, q] = s * vkp + c * vkq;
                }
            }

            values = new[] { a[0, 0], a[1, 1], a[2, 2] };
            vectors = new[]
            {
                new Vector3(v[0, 0], v[1, 0], v[2, 0]),
                new Vector3(v[0, 1], v[1, 1], v[2, 1]),
                new Vector3(v[0, 2], v[1, 2], v[2, 2])
            };
        }

        private static Vector3 MakeOrthogonal(Vector3 candidate, Vector3 basis)
        {
            Vector3 result = candidate - basis * Vector3.Dot(candidate, basis);
            if (result.sqrMagnitude > 0.0000001f)
                return result.normalized;

            Vector3 fallback = Mathf.Abs(basis.x) < 0.8f ? Vector3.right : Vector3.up;
            return (fallback - basis * Vector3.Dot(fallback, basis)).normalized;
        }

        private static Matrix4x4 BasisMatrix(Vector3[] axes)
        {
            Matrix4x4 matrix = Matrix4x4.identity;
            matrix.SetColumn(0, new Vector4(axes[0].x, axes[0].y, axes[0].z, 0f));
            matrix.SetColumn(1, new Vector4(axes[1].x, axes[1].y, axes[1].z, 0f));
            matrix.SetColumn(2, new Vector4(axes[2].x, axes[2].y, axes[2].z, 0f));
            return matrix;
        }

        private static int[] BuildLevelCounts(int sourceCount, int targetCount)
        {
            int max = Mathf.Min(sourceCount, targetCount);
            return new[]
            {
                Mathf.Clamp(192, 3, max),
                Mathf.Clamp(512, 3, max),
                Mathf.Clamp(1024, 3, max)
            };
        }

        private static Vector3[] SelectEvenPoints(Vector3[] points, int maxCount)
        {
            if (points == null || points.Length == 0)
                return Array.Empty<Vector3>();

            int count = Mathf.Min(Mathf.Max(1, maxCount), points.Length);
            if (count == points.Length)
                return points;

            var result = new Vector3[count];
            for (int i = 0; i < count; i++)
                result[i] = points[PickEvenIndex(i, count, points.Length)];
            return result;
        }

        private static int PickEvenIndex(int i, int count, int length)
        {
            if (count <= 1)
                return 0;
            return Mathf.Clamp(Mathf.RoundToInt(i * (length - 1) / (float)(count - 1)), 0, length - 1);
        }

        private static int FindNearestPoint(Vector3 p, Vector3[] points, out float distSq)
        {
            distSq = float.MaxValue;
            int idx = 0;
            for (int i = 0; i < points.Length; i++)
            {
                float sq = (p - points[i]).sqrMagnitude;
                if (sq < distSq)
                {
                    distSq = sq;
                    idx = i;
                }
            }

            return idx;
        }

        private static void DeduplicateCandidates(List<Candidate> candidates, float positionTolerance, float angleToleranceDeg)
        {
            for (int i = candidates.Count - 1; i >= 0; i--)
            {
                Vector3 pi = candidates[i].Matrix.GetColumn(3);
                Quaternion ri = SafeRotation(candidates[i].Matrix);
                for (int j = 0; j < i; j++)
                {
                    Vector3 pj = candidates[j].Matrix.GetColumn(3);
                    Quaternion rj = SafeRotation(candidates[j].Matrix);
                    if ((pi - pj).sqrMagnitude <= positionTolerance * positionTolerance &&
                        Quaternion.Angle(ri, rj) <= angleToleranceDeg)
                    {
                        candidates.RemoveAt(i);
                        break;
                    }
                }
            }
        }

        private static bool IsDeltaConverged(Matrix4x4 delta)
        {
            Vector3 trans = delta.GetColumn(3);
            return trans.sqrMagnitude <= 0.0000000001f &&
                   Quaternion.Angle(Quaternion.identity, SafeRotation(delta)) <= 0.001f;
        }

        private static Quaternion SafeRotation(Matrix4x4 matrix)
        {
            Vector3 forward = matrix.GetColumn(2);
            Vector3 upwards = matrix.GetColumn(1);
            if (forward.sqrMagnitude <= 0.0000001f || upwards.sqrMagnitude <= 0.0000001f)
                return Quaternion.identity;
            return Quaternion.LookRotation(forward.normalized, upwards.normalized);
        }

        private static int[] PickDistinctTriple(System.Random random, int length)
        {
            int a = random.Next(length);
            int b;
            int c;
            do { b = random.Next(length); } while (b == a);
            do { c = random.Next(length); } while (c == a || c == b);
            return new[] { a, b, c };
        }

        private static bool IsUsefulTriangle(Vector3[] points, int[] indices)
        {
            Vector3 a = points[indices[0]];
            Vector3 b = points[indices[1]];
            Vector3 c = points[indices[2]];
            return Vector3.Cross(b - a, c - a).sqrMagnitude > 0.0000001f;
        }

        private static float[] TriangleSignature(Vector3[] points, int[] indices, float radius)
        {
            float scale = Mathf.Max(0.000001f, radius);
            float[] signature =
            {
                Vector3.Distance(points[indices[0]], points[indices[1]]) / scale,
                Vector3.Distance(points[indices[1]], points[indices[2]]) / scale,
                Vector3.Distance(points[indices[2]], points[indices[0]]) / scale
            };
            Array.Sort(signature);
            return signature;
        }

        private static bool AreSignaturesClose(float[] a, float[] b, float tolerance)
        {
            for (int i = 0; i < a.Length; i++)
            {
                float denom = Mathf.Max(0.00001f, Mathf.Max(a[i], b[i]));
                if (Mathf.Abs(a[i] - b[i]) / denom > tolerance)
                    return false;
            }

            return true;
        }

        private static int LowerBound(float[] values, float target)
        {
            int lo = 0;
            int hi = values.Length - 1;
            while (lo < hi)
            {
                int mid = (lo + hi) / 2;
                if (values[mid] < target)
                    lo = mid + 1;
                else
                    hi = mid;
            }

            return lo;
        }

        private static float Halton(int index, int radix)
        {
            float result = 0f;
            float fraction = 1f / radix;
            while (index > 0)
            {
                result += fraction * (index % radix);
                index /= radix;
                fraction /= radix;
            }

            return result;
        }

        private static int PermutationSign(int[] perm)
        {
            int inversions = 0;
            for (int i = 0; i < perm.Length; i++)
            for (int j = i + 1; j < perm.Length; j++)
                if (perm[i] > perm[j])
                    inversions++;
            return inversions % 2 == 0 ? 1 : -1;
        }
    }
}
