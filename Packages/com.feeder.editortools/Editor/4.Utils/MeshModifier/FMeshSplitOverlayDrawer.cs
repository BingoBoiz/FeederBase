#if UNITY_EDITOR
using System.Collections.Generic;
using UnityEditor;
using UnityEngine;
using UnityEngine.Rendering;

namespace Feeder
{
    /// <summary>
    /// Cached overlay meshes for the Mesh Splitter scene view: a triangle-soup "parts" mesh
    /// (one submesh per part, vertex-colored) rebuilt on Analyze, a selection-tint mesh
    /// rebuilt on selection change, and billboard dots for Manual-mode selected vertices.
    /// Drawn with Hidden/Internal-Colored via Graphics.DrawMeshNow (vertex colors carry the
    /// tint; per-triangle Handles drawing is too slow on large meshes).
    /// </summary>
    public static class FMeshSplitOverlayDrawer
    {
        private static readonly Color SelectionColor = new Color(1f, 0.95f, 0.35f, 0.45f);
        private static readonly Color VertexDotColor = new Color(1f, 0.55f, 0.1f, 0.95f);
        private const float PartsAlpha = 0.30f;
        private const float HoverAlpha = 0.55f;

        private static Material _overlayMaterial;
        private static Mesh _partsMesh;
        private static Mesh _selectionMesh;
        private static Mesh _vertexDotsMesh;
        private static float _normalOffset;

        [InitializeOnLoadMethod]
        private static void Init()
        {
            AssemblyReloadEvents.beforeAssemblyReload += Clear;
        }

        public static void Clear()
        {
            DestroyMesh(ref _partsMesh);
            DestroyMesh(ref _selectionMesh);
            DestroyMesh(ref _vertexDotsMesh);

            if (_overlayMaterial != null)
            {
                Object.DestroyImmediate(_overlayMaterial);
                _overlayMaterial = null;
            }
        }

        // ================================================================ build

        /// <summary>Builds the color-per-part soup mesh (one submesh per part). Call after Analyze.</summary>
        public static void BuildPartsMesh(FMeshSplitAnalysis a)
        {
            DestroyMesh(ref _partsMesh);
            DestroyMesh(ref _selectionMesh);
            DestroyMesh(ref _vertexDotsMesh);
            if (a == null || a.TriangleCount == 0)
                return;

            _normalOffset = a.Mesh != null ? a.Mesh.bounds.size.magnitude * 0.002f : 0.001f;

            int triCount = a.TriangleCount;
            var positions = new Vector3[triCount * 3];
            var colors = new Color[triCount * 3];

            // Bucket triangles per part so each part becomes one submesh.
            int partCount = a.Parts.Count;
            var partTriangles = new List<int>[partCount];
            for (int p = 0; p < partCount; p++)
                partTriangles[p] = new List<int>();
            for (int t = 0; t < triCount; t++)
                partTriangles[a.TrianglePartId[t]].Add(t);

            var submeshIndices = new int[partCount][];
            int vert = 0;
            for (int p = 0; p < partCount; p++)
            {
                Color color = a.Parts[p].Color;
                color.a = PartsAlpha;
                var indices = new int[partTriangles[p].Count * 3];
                int w = 0;
                foreach (int t in partTriangles[p])
                {
                    Vector3 v0 = a.Vertices[a.Triangles[t * 3]];
                    Vector3 v1 = a.Vertices[a.Triangles[t * 3 + 1]];
                    Vector3 v2 = a.Vertices[a.Triangles[t * 3 + 2]];
                    Vector3 offset = Vector3.Cross(v1 - v0, v2 - v0).normalized * _normalOffset;

                    positions[vert] = v0 + offset;
                    positions[vert + 1] = v1 + offset;
                    positions[vert + 2] = v2 + offset;
                    colors[vert] = colors[vert + 1] = colors[vert + 2] = color;
                    indices[w++] = vert;
                    indices[w++] = vert + 1;
                    indices[w++] = vert + 2;
                    vert += 3;
                }
                submeshIndices[p] = indices;
            }

            _partsMesh = new Mesh
            {
                hideFlags = HideFlags.HideAndDontSave,
                indexFormat = positions.Length > 65534 ? IndexFormat.UInt32 : IndexFormat.UInt16
            };
            _partsMesh.vertices = positions;
            _partsMesh.colors = colors;
            _partsMesh.subMeshCount = partCount;
            for (int p = 0; p < partCount; p++)
                _partsMesh.SetIndices(submeshIndices[p], MeshTopology.Triangles, p);
            _partsMesh.RecalculateBounds();
        }

        /// <summary>Rebuilds the selection-tint mesh from a triangle mask. Call on selection change.</summary>
        public static void BuildSelectionMesh(FMeshSplitAnalysis a, bool[] triangleMask)
        {
            DestroyMesh(ref _selectionMesh);
            if (a == null || triangleMask == null)
                return;

            int selectedCount = 0;
            for (int t = 0; t < triangleMask.Length; t++)
                if (triangleMask[t])
                    selectedCount++;
            if (selectedCount == 0)
                return;

            var positions = new Vector3[selectedCount * 3];
            var colors = new Color[selectedCount * 3];
            var indices = new int[selectedCount * 3];

            int vert = 0;
            for (int t = 0; t < triangleMask.Length; t++)
            {
                if (!triangleMask[t])
                    continue;

                Vector3 v0 = a.Vertices[a.Triangles[t * 3]];
                Vector3 v1 = a.Vertices[a.Triangles[t * 3 + 1]];
                Vector3 v2 = a.Vertices[a.Triangles[t * 3 + 2]];
                Vector3 offset = Vector3.Cross(v1 - v0, v2 - v0).normalized * (_normalOffset * 1.5f);

                positions[vert] = v0 + offset;
                positions[vert + 1] = v1 + offset;
                positions[vert + 2] = v2 + offset;
                colors[vert] = colors[vert + 1] = colors[vert + 2] = SelectionColor;
                indices[vert] = vert;
                indices[vert + 1] = vert + 1;
                indices[vert + 2] = vert + 2;
                vert += 3;
            }

            _selectionMesh = new Mesh
            {
                hideFlags = HideFlags.HideAndDontSave,
                indexFormat = positions.Length > 65534 ? IndexFormat.UInt32 : IndexFormat.UInt16
            };
            _selectionMesh.vertices = positions;
            _selectionMesh.colors = colors;
            _selectionMesh.SetIndices(indices, MeshTopology.Triangles, 0);
            _selectionMesh.RecalculateBounds();
        }

        /// <summary>
        /// Rebuilds camera-facing billboard quads for Manual-mode selected vertices.
        /// Sized per vertex via HandleUtility.GetHandleSize, so rebuild when the camera moves
        /// noticeably is unnecessary — the size is only a picking aid.
        /// </summary>
        public static void BuildVertexDots(FMeshSplitAnalysis a, Transform target, HashSet<int> selectedCanonical)
        {
            DestroyMesh(ref _vertexDotsMesh);
            if (a == null || target == null || selectedCanonical == null || selectedCanonical.Count == 0)
                return;

            Matrix4x4 localToWorld = target.localToWorldMatrix;
            Camera cam = SceneView.lastActiveSceneView != null ? SceneView.lastActiveSceneView.camera : null;
            Vector3 camRight = cam != null ? cam.transform.right : Vector3.right;
            Vector3 camUp = cam != null ? cam.transform.up : Vector3.up;

            int count = selectedCanonical.Count;
            var positions = new Vector3[count * 4];
            var colors = new Color[count * 4];
            var indices = new int[count * 6];

            int i = 0;
            foreach (int c in selectedCanonical)
            {
                Vector3 world = localToWorld.MultiplyPoint3x4(a.CanonicalPositions[c]);
                float size = HandleUtility.GetHandleSize(world) * 0.03f;
                Vector3 r = camRight * size;
                Vector3 u = camUp * size;

                int v = i * 4;
                positions[v] = world - r - u;
                positions[v + 1] = world + r - u;
                positions[v + 2] = world + r + u;
                positions[v + 3] = world - r + u;
                colors[v] = colors[v + 1] = colors[v + 2] = colors[v + 3] = VertexDotColor;

                int w = i * 6;
                indices[w] = v;
                indices[w + 1] = v + 1;
                indices[w + 2] = v + 2;
                indices[w + 3] = v;
                indices[w + 4] = v + 2;
                indices[w + 5] = v + 3;
                i++;
            }

            _vertexDotsMesh = new Mesh
            {
                hideFlags = HideFlags.HideAndDontSave,
                indexFormat = positions.Length > 65534 ? IndexFormat.UInt32 : IndexFormat.UInt16
            };
            _vertexDotsMesh.vertices = positions;
            _vertexDotsMesh.colors = colors;
            _vertexDotsMesh.SetIndices(indices, MeshTopology.Triangles, 0);
            _vertexDotsMesh.RecalculateBounds();
        }

        // ================================================================ draw (Repaint only)

        /// <summary>Draws all parts color-coded; optionally re-draws one part brighter (hover).</summary>
        public static void DrawParts(Matrix4x4 localToWorld, int hoveredPartId, bool drawAllParts)
        {
            if (_partsMesh == null)
                return;
            Material mat = GetOverlayMaterial();
            if (mat == null)
                return;

            CompareFunction previousZTest = Handles.zTest;
            Handles.zTest = CompareFunction.LessEqual;
            mat.SetPass(0);

            if (drawAllParts)
            {
                for (int p = 0; p < _partsMesh.subMeshCount; p++)
                    Graphics.DrawMeshNow(_partsMesh, localToWorld, p);
            }

            // Hover pass: draw the hovered part again — alpha stacks into a brighter tint.
            if (hoveredPartId >= 0 && hoveredPartId < _partsMesh.subMeshCount)
            {
                Graphics.DrawMeshNow(_partsMesh, localToWorld, hoveredPartId);
                Graphics.DrawMeshNow(_partsMesh, localToWorld, hoveredPartId);
            }

            Handles.zTest = previousZTest;
        }

        public static void DrawSelection(Matrix4x4 localToWorld)
        {
            if (_selectionMesh == null)
                return;
            Material mat = GetOverlayMaterial();
            if (mat == null)
                return;

            CompareFunction previousZTest = Handles.zTest;
            Handles.zTest = CompareFunction.LessEqual;
            mat.SetPass(0);
            Graphics.DrawMeshNow(_selectionMesh, localToWorld);
            Handles.zTest = previousZTest;
        }

        /// <summary>Vertex dots are built in world space, so draw with identity.</summary>
        public static void DrawVertexDots()
        {
            if (_vertexDotsMesh == null)
                return;
            Material mat = GetOverlayMaterial();
            if (mat == null)
                return;

            CompareFunction previousZTest = Handles.zTest;
            Handles.zTest = CompareFunction.Always;
            mat.SetPass(0);
            Graphics.DrawMeshNow(_vertexDotsMesh, Matrix4x4.identity);
            Handles.zTest = previousZTest;
        }

        private static Material GetOverlayMaterial()
        {
            if (_overlayMaterial != null)
                return _overlayMaterial;

            Shader shader = Shader.Find("Hidden/Internal-Colored");
            if (shader == null)
                return null;

            _overlayMaterial = new Material(shader) { hideFlags = HideFlags.HideAndDontSave };
            _overlayMaterial.SetInt("_SrcBlend", (int)BlendMode.SrcAlpha);
            _overlayMaterial.SetInt("_DstBlend", (int)BlendMode.OneMinusSrcAlpha);
            _overlayMaterial.SetInt("_ZWrite", 0);
            _overlayMaterial.SetInt("_Cull", (int)CullMode.Off);
            return _overlayMaterial;
        }

        private static void DestroyMesh(ref Mesh mesh)
        {
            if (mesh == null)
                return;
            Object.DestroyImmediate(mesh);
            mesh = null;
        }
    }
}
#endif
