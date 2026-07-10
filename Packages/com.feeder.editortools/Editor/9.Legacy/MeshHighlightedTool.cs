using System;
using System.Collections.Generic;
using UnityEditor;
using UnityEngine;

namespace Feeder
{
    /// <summary>Immutable geometry fingerprint for mesh comparison and cache.</summary>
    public readonly struct GeometrySignature
    {
        public readonly int VertexCount;
        public readonly int TriangleCount;
        public readonly Vector3 BoundsSize;
        public readonly ulong GeometryHash;
        public readonly float Tolerance;
        public readonly bool BoundsOnly;

        public GeometrySignature(int vertexCount, int triangleCount, Vector3 boundsSize, ulong geometryHash, float tolerance, bool boundsOnly)
        {
            VertexCount = vertexCount;
            TriangleCount = triangleCount;
            BoundsSize = boundsSize;
            GeometryHash = geometryHash;
            Tolerance = tolerance;
            BoundsOnly = boundsOnly;
        }
    }

    /// <summary>Builds and caches mesh signatures; no per-frame allocation when cache hits.</summary>
    public static class GeometrySignatureCache
    {
        private static readonly Dictionary<Mesh, GeometrySignature> Cache = new Dictionary<Mesh, GeometrySignature>();

        public static GeometrySignature GetOrCompute(Mesh mesh, float tolerance, bool boundsOnly)
        {
            if (mesh == null) return default;
            if (Cache.TryGetValue(mesh, out var cached) && Math.Abs(cached.Tolerance - tolerance) < 1e-9f && cached.BoundsOnly == boundsOnly)
                return cached;

            var sig = ComputeSignature(mesh, tolerance, boundsOnly);
            Cache[mesh] = sig;
            return sig;
        }

        public static void Clear() => Cache.Clear();

        private static GeometrySignature ComputeSignature(Mesh mesh, float tolerance, bool boundsOnly)
        {
            var vertexCount = mesh.vertexCount;
            var triangleCount = mesh.triangles?.Length ?? 0;
            if (triangleCount % 3 != 0) triangleCount = 0;
            else triangleCount /= 3;
            var bounds = mesh.bounds;
            var boundsSize = bounds.size;

            ulong hash = 0;
            if (!boundsOnly && mesh.vertexCount > 0)
            {
                var verts = mesh.vertices;
                var center = bounds.center;
                var size = boundsSize;
                var inv = new Vector3(
                    size.x > 1e-6f ? 1f / size.x : 1f,
                    size.y > 1e-6f ? 1f / size.y : 1f,
                    size.z > 1e-6f ? 1f / size.z : 1f);

                var normalized = new Vector3[verts.Length];
                for (var i = 0; i < verts.Length; i++)
                {
                    var p = verts[i] - center;
                    p.x *= inv.x; p.y *= inv.y; p.z *= inv.z;
                    var t = tolerance > 0 ? tolerance : 1e-6f;
                    p.x = (float)(Math.Floor(p.x / t) * t);
                    p.y = (float)(Math.Floor(p.y / t) * t);
                    p.z = (float)(Math.Floor(p.z / t) * t);
                    normalized[i] = p;
                }
                Array.Sort(normalized, Vector3Comparer.Instance);
                for (var i = 0; i < normalized.Length; i++)
                {
                    var v = normalized[i];
                    hash = HashCombine(hash, v.x, v.y, v.z);
                }
            }

            return new GeometrySignature(vertexCount, triangleCount, boundsSize, hash, tolerance, boundsOnly);
        }

        private static ulong HashCombine(ulong h, float x, float y, float z)
        {
            var ux = (ulong)BitConverter.DoubleToInt64Bits(x);
            var uy = (ulong)BitConverter.DoubleToInt64Bits(y);
            var uz = (ulong)BitConverter.DoubleToInt64Bits(z);
            unchecked
            {
                h = h * 31ul + ux;
                h = h * 31ul + uy;
                h = h * 31ul + uz;
            }
            return h;
        }

        private sealed class Vector3Comparer : IComparer<Vector3>
        {
            public static readonly Vector3Comparer Instance = new Vector3Comparer();
            public int Compare(Vector3 a, Vector3 b)
            {
                var dx = a.x.CompareTo(b.x); if (dx != 0) return dx;
                var dy = a.y.CompareTo(b.y); if (dy != 0) return dy;
                return a.z.CompareTo(b.z);
            }
        }
    }

    
    public sealed class MeshHighlightedToolWindow : EditorWindow
    {
        private const string DrawerGoName = "_MeshHighlightDrawer";
        private const string WindowTitle = "Mesh Highlight (Legacy)";

        [SerializeField] private Mesh _meshA;
        [SerializeField] private Mesh _meshB;
        [SerializeField] private Vector2 _scroll;
        [SerializeField] private int _foundCount;
        [SerializeField] private float _geometryTolerance = 0.0001f;
        [SerializeField] private float _tricpOverlap = 0.9f;
        [SerializeField] private int _tricpMaxIterations = 30;

        private readonly List<Matrix4x4> _matrices = new List<Matrix4x4>();
        private MeshHighlightDrawer _drawer;

        [MenuItem("Tools/Feeder/Legacy/Mesh Highlight Tool")]
        private static void Open()
        {
            var w = GetWindow<MeshHighlightedToolWindow>(WindowTitle);
            w.minSize = new Vector2(280f, 200f);
        }

        private void OnEnable()
        {
            EnsureDrawer();
            UpdateDrawerData();
        }

        private void OnDisable()
        {
            MeshHighlightDrawer.SharedMesh = null;
        }

        private void OnGUI()
        {
            _scroll = EditorGUILayout.BeginScrollView(_scroll);

            EditorGUILayout.Space(4f);
            _meshA = (Mesh)EditorGUILayout.ObjectField("Mesh A (gizmo)", _meshA, typeof(Mesh), false);
            _meshB = (Mesh)EditorGUILayout.ObjectField("Mesh B (target in scene)", _meshB, typeof(Mesh), false);
            _geometryTolerance = EditorGUILayout.FloatField("Geometry tolerance", _geometryTolerance);
            _tricpOverlap = Mathf.Clamp01(EditorGUILayout.FloatField("TrICP overlap fraction", _tricpOverlap));
            _tricpMaxIterations = Mathf.Max(1, EditorGUILayout.IntField("TrICP max iterations", _tricpMaxIterations));
            EditorGUILayout.Space(4f);

            if (GUILayout.Button("Scan scene")) ScanScene();

            EditorGUILayout.Space(2f);
            EditorGUILayout.LabelField($"Found: {_foundCount} object(s)", EditorStyles.miniLabel);

            EditorGUILayout.EndScrollView();

            if (GUI.changed) UpdateDrawerData();
        }

        /// <summary>Compare meshes: bounds-only = triangle count + bounds size (allows vertex count variance).</summary>
        //private bool AreMeshesSimilar(Mesh a, Mesh b, float tolerance)
        //{
        //    if (a == null || b == null) return false;
        //    var sigA = GeometrySignatureCache.GetOrCompute(a, tolerance, _compareByBoundsOnly);
        //    var sigB = GeometrySignatureCache.GetOrCompute(b, tolerance, _compareByBoundsOnly);
        //    if (_compareByBoundsOnly)
        //    {
        //        if (sigA.TriangleCount != sigB.TriangleCount) return false;
        //        return BoundsSizeEqual(sigA.BoundsSize, sigB.BoundsSize, tolerance);
        //    }
        //    if (sigA.VertexCount != sigB.VertexCount || sigA.TriangleCount != sigB.TriangleCount)
        //        return false;
        //    return sigA.GeometryHash == sigB.GeometryHash;
        //}

        private static bool BoundsSizeEqual(Vector3 a, Vector3 b, float tolerance)
        {
            return Math.Abs(a.x - b.x) <= tolerance && Math.Abs(a.y - b.y) <= tolerance && Math.Abs(a.z - b.z) <= tolerance;
        }

        private void ScanScene()
        {
            _matrices.Clear();
            if (_meshB == null)
            {
                Debug.LogWarning("[MeshHighlight] Scan aborted: Mesh B (target) is not assigned.");
                return;
            }

            EnsureDrawer();
            if (_drawer == null)
            {
                Debug.LogError("[MeshHighlight] Scan aborted: drawer GameObject could not be created or found.");
                return;
            }

            var roots = UnityEngine.SceneManagement.SceneManager.GetActiveScene().GetRootGameObjects();
            var tolerance = _geometryTolerance;
            var filtersChecked = 0;
            for (var r = 0; r < roots.Length; r++)
            {
                var filters = roots[r].GetComponentsInChildren<MeshFilter>(true);
                for (var i = 0; i < filters.Length; i++)
                {
                    var f = filters[i];
                    if (!f.gameObject.activeInHierarchy) continue;
                    filtersChecked++;
                    var mesh = f?.sharedMesh;
                    if (mesh == null) continue;
                    //if (!AreMeshesSimilar(_meshB, mesh, tolerance)) continue;
                    var gizmoMesh = _meshA != null ? _meshA : mesh;
                    if (!FMeshMatchTransformUtils.CanComputeMatch(gizmoMesh, mesh)) continue;
                    var options = new FMeshMatchTransformUtils.TricpOptions
                    {
                        OverlapFraction = _tricpOverlap,
                        MaxIterations = _tricpMaxIterations,
                        ConvergenceThreshold = 1e-6f
                    };
                    var matrix = FMeshMatchTransformUtils.ComputeWorldMatchMatrix(gizmoMesh, mesh, f.transform, _drawer.transform, options);
                    _matrices.Add(matrix);
                }
            }

            _foundCount = _matrices.Count;
            UpdateDrawerData();
            SceneView.RepaintAll();
        }

        private void EnsureDrawer()
        {
            _drawer = FindObjectOfType<MeshHighlightDrawer>();
            if (_drawer != null) return;

            var go = new GameObject(DrawerGoName);
            go.hideFlags = HideFlags.HideAndDontSave;
            Undo.RegisterCreatedObjectUndo(go, "Mesh Highlight Drawer");
            _drawer = go.AddComponent<MeshHighlightDrawer>();
            Debug.Log("[MeshHighlight] Drawer GameObject created.");
        }

        private void UpdateDrawerData()
        {
            MeshHighlightDrawer.SharedMesh = _meshA;
            if (_meshA == null)
                Debug.LogWarning("[MeshHighlight] Mesh A (gizmo) is null: gizmo will not draw. Assign Mesh A to see wireframe.");
            if (_drawer == null)
            {
                Debug.LogWarning("[MeshHighlight] UpdateDrawerData: drawer is null, skipping transform apply.");
                SceneView.RepaintAll();
                return;
            }
            if (_matrices != null && _matrices.Count > 0)
            {
                ApplyMatrixToDrawer(_matrices[0]);
                Debug.Log($"[MeshHighlight] Drawer transform applied (position {_drawer.transform.position}).");
            }
            else
            {
                _drawer.transform.SetPositionAndRotation(Vector3.zero, Quaternion.identity);
                _drawer.transform.localScale = Vector3.one;
            }
            SceneView.RepaintAll();
        }

        /// <summary>Set drawer transform so gizmo mesh A (drawn by MeshHighlightDrawer) aligns with target mesh B.</summary>
        private void ApplyMatrixToDrawer(Matrix4x4 worldMatrix)
        {
            if (_drawer == null) return;
            FMeshMatchTransformUtils.ApplyWorldMatrixToTransform(_drawer.transform, worldMatrix);
        }
    }
}
