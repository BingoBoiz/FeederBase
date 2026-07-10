#if UNITY_EDITOR
using UnityEditor;
using UnityEngine;

namespace Feeder
{
    /// <summary>
    /// Pure-math picking helpers for the Mesh Splitter scene overlay: ray↔triangle
    /// intersection over the analyzed arrays (no colliders, hits backfaces) and
    /// nearest-canonical-vertex lookup in GUI space.
    /// </summary>
    public static class FMeshSplitPicking
    {
        /// <summary>
        /// Casts a world-space ray against every triangle of the analysis (in the target's
        /// local space) and returns the closest hit. Möller–Trumbore, both faces.
        /// </summary>
        public static bool RaycastTriangle(FMeshSplitAnalysis a, Transform target, Ray worldRay,
            out int hitTriangle, out Vector3 worldHitPoint)
        {
            hitTriangle = -1;
            worldHitPoint = default;
            if (a == null || target == null)
                return false;

            Matrix4x4 worldToLocal = target.worldToLocalMatrix;
            Vector3 origin = worldToLocal.MultiplyPoint3x4(worldRay.origin);
            Vector3 dir = worldToLocal.MultiplyVector(worldRay.direction);
            float dirScale = dir.magnitude;
            if (dirScale < 1e-12f)
                return false;
            dir /= dirScale;

            float bestT = float.MaxValue;
            int triCount = a.TriangleCount;
            for (int t = 0; t < triCount; t++)
            {
                int i = t * 3;
                if (IntersectTriangle(origin, dir,
                        a.Vertices[a.Triangles[i]],
                        a.Vertices[a.Triangles[i + 1]],
                        a.Vertices[a.Triangles[i + 2]],
                        out float hitT) && hitT < bestT)
                {
                    bestT = hitT;
                    hitTriangle = t;
                }
            }

            if (hitTriangle < 0)
                return false;

            worldHitPoint = target.localToWorldMatrix.MultiplyPoint3x4(origin + dir * bestT);
            return true;
        }

        /// <summary>
        /// Nearest canonical vertex to a GUI point (screen-space distance), or -1 when none
        /// is within maxPixelDistance. Vertices behind the camera are skipped —
        /// WorldToGUIPoint returns garbage coordinates for them.
        /// </summary>
        public static int FindNearestCanonicalVertex(FMeshSplitAnalysis a, Transform target,
            Vector2 guiPoint, float maxPixelDistance)
        {
            if (a == null || target == null)
                return -1;

            Camera cam = GetSceneCamera();
            Matrix4x4 localToWorld = target.localToWorldMatrix;
            float bestSq = maxPixelDistance * maxPixelDistance;
            int best = -1;

            for (int c = 0; c < a.CanonicalCount; c++)
            {
                Vector3 world = localToWorld.MultiplyPoint3x4(a.CanonicalPositions[c]);
                if (IsBehindCamera(cam, world))
                    continue;

                float sq = (HandleUtility.WorldToGUIPoint(world) - guiPoint).sqrMagnitude;
                if (sq < bestSq)
                {
                    bestSq = sq;
                    best = c;
                }
            }

            return best;
        }

        /// <summary>
        /// Collects all canonical vertices within radiusPixels of a GUI point into the buffer.
        /// </summary>
        public static void CollectCanonicalVerticesInGuiRadius(FMeshSplitAnalysis a, Transform target,
            Vector2 guiPoint, float radiusPixels, System.Collections.Generic.List<int> buffer)
        {
            buffer.Clear();
            if (a == null || target == null)
                return;

            Camera cam = GetSceneCamera();
            Matrix4x4 localToWorld = target.localToWorldMatrix;
            float radiusSq = radiusPixels * radiusPixels;

            for (int c = 0; c < a.CanonicalCount; c++)
            {
                Vector3 world = localToWorld.MultiplyPoint3x4(a.CanonicalPositions[c]);
                if (IsBehindCamera(cam, world))
                    continue;
                if ((HandleUtility.WorldToGUIPoint(world) - guiPoint).sqrMagnitude <= radiusSq)
                    buffer.Add(c);
            }
        }

        private static Camera GetSceneCamera()
        {
            SceneView view = SceneView.currentDrawingSceneView != null
                ? SceneView.currentDrawingSceneView
                : SceneView.lastActiveSceneView;
            return view != null ? view.camera : null;
        }

        private static bool IsBehindCamera(Camera cam, Vector3 world)
        {
            return cam != null && Vector3.Dot(world - cam.transform.position, cam.transform.forward) <= 0f;
        }

        // Möller–Trumbore, double-sided.
        private static bool IntersectTriangle(Vector3 origin, Vector3 dir,
            Vector3 v0, Vector3 v1, Vector3 v2, out float t)
        {
            t = 0f;
            Vector3 e1 = v1 - v0;
            Vector3 e2 = v2 - v0;
            Vector3 p = Vector3.Cross(dir, e2);
            float det = Vector3.Dot(e1, p);
            if (Mathf.Abs(det) < 1e-12f)
                return false;

            float invDet = 1f / det;
            Vector3 s = origin - v0;
            float u = Vector3.Dot(s, p) * invDet;
            if (u < 0f || u > 1f)
                return false;

            Vector3 q = Vector3.Cross(s, e1);
            float v = Vector3.Dot(dir, q) * invDet;
            if (v < 0f || u + v > 1f)
                return false;

            t = Vector3.Dot(e2, q) * invDet;
            return t > 1e-6f;
        }
    }
}
#endif
