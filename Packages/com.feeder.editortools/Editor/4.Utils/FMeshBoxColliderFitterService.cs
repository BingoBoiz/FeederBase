using System;
using System.Collections.Generic;
using UnityEditor;
using UnityEngine;

namespace Feeder
{
    public static class FMeshBoxColliderFitterService
    {
        private struct OBBResult
        {
            public Vector3 Center;    // world-space center
            public Vector3 Size;      // world-space full extents along OBB axes
            public Quaternion Rotation; // world-space rotation of the OBB
        }

        public static int FitAll(IReadOnlyList<GameObject> targets, bool overwriteExisting)
        {
            if (!(targets?.Count > 0))
                throw new InvalidOperationException("targets list is empty.");

            int count = 0;
            for (int i = 0; i < targets.Count; i++)
            {
                var target = targets[i];
                if (target == null)
                {
                    Debug.LogWarning($"[FMeshBoxColliderFitterService] Skipping null at targets[{i}].");
                    continue;
                }

                bool isSceneObject = target.scene.IsValid();
                var root = FPrefabRootResolver.GetRootTransform(target, out var prefabRoot, out bool shouldUnload);
                bool shouldSave = false;

                try
                {
                    int fitted = FitHierarchy(root, overwriteExisting, isSceneObject);
                    count += fitted;
                    shouldSave = fitted > 0 && !isSceneObject;
                }
                finally
                {
                    if (shouldUnload && prefabRoot != null)
                    {
                        if (shouldSave)
                        {
                            var assetPath = AssetDatabase.GetAssetPath(target);
                            if (string.IsNullOrEmpty(assetPath))
                                throw new InvalidOperationException($"asset path is empty for {target.name}.");
                            PrefabUtility.SaveAsPrefabAsset(prefabRoot, assetPath);
                        }
                        PrefabUtility.UnloadPrefabContents(prefabRoot);
                    }
                }
            }

            AssetDatabase.SaveAssets();
            AssetDatabase.Refresh();
            return count;
        }

        private static int FitHierarchy(Transform root, bool overwriteExisting, bool isSceneObject)
        {
            var renderers = root.GetComponentsInChildren<MeshRenderer>(true);
            int count = 0;
            foreach (var renderer in renderers)
            {
                if (FitRenderer(renderer, overwriteExisting, isSceneObject))
                    count++;
            }
            return count;
        }

        private static bool FitRenderer(MeshRenderer renderer, bool overwriteExisting, bool isSceneObject)
        {
            if (renderer == null) return false;
            var filter = renderer.GetComponent<MeshFilter>();
            if (filter == null || filter.sharedMesh == null) return false;

            var go = renderer.gameObject;
            string colName = go.name + "_Col";

            var existingCol = go.transform.Find(colName);
            if (existingCol != null)
            {
                if (!overwriteExisting) return false;
                if (isSceneObject)
                    Undo.DestroyObjectImmediate(existingCol.gameObject);
                else
                    UnityEngine.Object.DestroyImmediate(existingCol.gameObject);
            }

            // Transform every vertex to world space so OBB accounts for the full
            // parent transform (position, rotation, AND non-uniform scale).
            var localVerts = filter.sharedMesh.vertices;
            var worldVerts = new Vector3[localVerts.Length];
            for (int i = 0; i < localVerts.Length; i++)
                worldVerts[i] = go.transform.TransformPoint(localVerts[i]);

            var obb = ComputeOBB(worldVerts);

            var colGO = new GameObject(colName);
            if (isSceneObject)
                Undo.RegisterCreatedObjectUndo(colGO, "Fit Box Collider");

            colGO.transform.SetParent(go.transform, false);

            // Place at world-space OBB center and orientation.
            colGO.transform.position = obb.Center;
            colGO.transform.rotation = obb.Rotation;

            // Neutralize the parent's lossyScale so BoxCollider.size equals the world-space
            // extents directly. Without this, a parent scale of (3.67,1,1) would stretch the
            // collider along its local axes, breaking the fit for any non-trivial OBB rotation.
            var ls = go.transform.lossyScale;
            colGO.transform.localScale = new Vector3(
                Mathf.Approximately(ls.x, 0f) ? 1f : 1f / ls.x,
                Mathf.Approximately(ls.y, 0f) ? 1f : 1f / ls.y,
                Mathf.Approximately(ls.z, 0f) ? 1f : 1f / ls.z
            );

            var box = colGO.AddComponent<BoxCollider>();
            box.center = Vector3.zero;
            box.size = obb.Size;

            if (isSceneObject)
                EditorUtility.SetDirty(go);

            return true;
        }

        private static OBBResult ComputeOBB(Vector3[] vertices)
        {
            if (vertices.Length == 0)
                return new OBBResult { Center = Vector3.zero, Size = Vector3.zero, Rotation = Quaternion.identity };

            // Centroid
            Vector3 centroid = Vector3.zero;
            for (int i = 0; i < vertices.Length; i++)
                centroid += vertices[i];
            centroid /= vertices.Length;

            // 3x3 covariance matrix of the vertex cloud
            float[,] cov = new float[3, 3];
            for (int i = 0; i < vertices.Length; i++)
            {
                Vector3 d = vertices[i] - centroid;
                for (int r = 0; r < 3; r++)
                    for (int c = 0; c < 3; c++)
                        cov[r, c] += d[r] * d[c];
            }
            float invN = 1f / vertices.Length;
            for (int r = 0; r < 3; r++)
                for (int c = 0; c < 3; c++)
                    cov[r, c] *= invN;

            // Principal axes via Jacobi eigendecomposition
            JacobiEigen3x3(cov, out Vector3 u, out Vector3 v, out Vector3 w);
            u = u.normalized;
            v = v.normalized;
            w = Vector3.Cross(u, v).normalized;

            // Project every vertex onto each axis to get full extents
            float minU = float.MaxValue, maxU = float.MinValue;
            float minV = float.MaxValue, maxV = float.MinValue;
            float minW = float.MaxValue, maxW = float.MinValue;

            for (int i = 0; i < vertices.Length; i++)
            {
                Vector3 d = vertices[i] - centroid;
                float du = Vector3.Dot(d, u);
                float dv = Vector3.Dot(d, v);
                float dw = Vector3.Dot(d, w);
                if (du < minU) minU = du; if (du > maxU) maxU = du;
                if (dv < minV) minV = dv; if (dv > maxV) maxV = dv;
                if (dw < minW) minW = dw; if (dw > maxW) maxW = dw;
            }

            Vector3 obbCenter = centroid
                + u * (0.5f * (minU + maxU))
                + v * (0.5f * (minV + maxV))
                + w * (0.5f * (minW + maxW));

            Vector3 obbSize = new Vector3(
                Mathf.Abs(maxU - minU),
                Mathf.Abs(maxV - minV),
                Mathf.Abs(maxW - minW)
            );

            var mat = new Matrix4x4();
            mat.SetColumn(0, new Vector4(u.x, u.y, u.z, 0));
            mat.SetColumn(1, new Vector4(v.x, v.y, v.z, 0));
            mat.SetColumn(2, new Vector4(w.x, w.y, w.z, 0));
            mat.SetColumn(3, new Vector4(0, 0, 0, 1));

            return new OBBResult { Center = obbCenter, Size = obbSize, Rotation = mat.rotation };
        }

        // Classic Jacobi eigendecomposition for a 3x3 symmetric matrix.
        // Converges in ~10 iterations for typical covariance matrices.
        // Eigenvectors are returned as columns of the accumulated rotation V.
        private static void JacobiEigen3x3(float[,] a, out Vector3 ev0, out Vector3 ev1, out Vector3 ev2)
        {
            float[,] v = { { 1, 0, 0 }, { 0, 1, 0 }, { 0, 0, 1 } };

            for (int iter = 0; iter < 64; iter++)
            {
                // Find the largest off-diagonal element
                int p = 0, q = 1;
                float maxOff = Mathf.Abs(a[0, 1]);
                if (Mathf.Abs(a[0, 2]) > maxOff) { maxOff = Mathf.Abs(a[0, 2]); p = 0; q = 2; }
                if (Mathf.Abs(a[1, 2]) > maxOff) { maxOff = Mathf.Abs(a[1, 2]); p = 1; q = 2; }

                if (maxOff < 1e-10f) break;

                float diff = a[q, q] - a[p, p];
                float theta = diff == 0f ? 0f : diff / (2f * a[p, q]);
                float sign = theta >= 0f ? 1f : -1f;
                float t = sign / (Mathf.Abs(theta) + Mathf.Sqrt(1f + theta * theta));
                float c = 1f / Mathf.Sqrt(1f + t * t);
                float s = t * c;

                float apq = a[p, q];
                a[p, p] -= t * apq;
                a[q, q] += t * apq;
                a[p, q] = 0f;
                a[q, p] = 0f;

                int r = 3 - p - q; // the remaining index (0+1+2=3)
                float arp = c * a[r, p] - s * a[r, q];
                float arq = s * a[r, p] + c * a[r, q];
                a[r, p] = arp; a[p, r] = arp;
                a[r, q] = arq; a[q, r] = arq;

                for (int i = 0; i < 3; i++)
                {
                    float vip = c * v[i, p] - s * v[i, q];
                    float viq = s * v[i, p] + c * v[i, q];
                    v[i, p] = vip;
                    v[i, q] = viq;
                }
            }

            ev0 = new Vector3(v[0, 0], v[1, 0], v[2, 0]);
            ev1 = new Vector3(v[0, 1], v[1, 1], v[2, 1]);
            ev2 = new Vector3(v[0, 2], v[1, 2], v[2, 2]);
        }
    }
}
