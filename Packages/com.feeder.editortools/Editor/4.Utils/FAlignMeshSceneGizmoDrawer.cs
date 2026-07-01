using System.Collections.Generic;
using UnityEditor;
using UnityEngine;
using UnityEngine.Rendering;

namespace Feeder
{
    // editor-only scene wire mesh; MonoBehaviour gizmos in Editor assemblies are unreliable on scene objects
    public static class FAlignMeshSceneGizmoDrawer
    {
        private static Mesh _sharedMesh;
        private static bool _drawingEnabled;
        private static Vector3 _position;
        private static Quaternion _rotation = Quaternion.identity;
        private static Vector3 _lossyScale = Vector3.one;

        private static Material _wireMaterial;
        private static Mesh _edgeMeshCache;
        private static int _edgeMeshCacheId;

        private static readonly Color WireColor = new Color(0f, 1f, 0.5f, 0.85f);

        public static Mesh SharedMesh => _sharedMesh;
        public static bool DrawingEnabled => _drawingEnabled;
        public static Vector3 Position => _position;
        public static Quaternion Rotation => _rotation;
        public static Vector3 LossyScale => _lossyScale;

        [InitializeOnLoadMethod]
        private static void RegisterSceneGui()
        {
            SceneView.duringSceneGui += OnSceneGui;
            AssemblyReloadEvents.beforeAssemblyReload += CleanupResources;
        }

        public static void SetSharedMesh(Mesh mesh)
        {
            if (_sharedMesh != mesh)
                _edgeMeshCacheId = 0;
            _sharedMesh = mesh;
            SceneView.RepaintAll();
        }

        public static void SetDrawingEnabled(bool enabled)
        {
            _drawingEnabled = enabled;
            SceneView.RepaintAll();
        }

        public static void CopyPoseFrom(GameObject source)
        {
            if (source == null)
            {
                _drawingEnabled = false;
                SceneView.RepaintAll();
                return;
            }

            Transform t = source.transform;
            _position = t.position;
            _rotation = t.rotation;
            _lossyScale = t.lossyScale;
            _drawingEnabled = true;
            SceneView.RepaintAll();
        }

        public static void SetPositionAndRotation(Vector3 position, Quaternion rotation)
        {
            _position = position;
            _rotation = rotation;
            SceneView.RepaintAll();
        }

        public static void SetLossyScale(Vector3 lossyScale)
        {
            _lossyScale = lossyScale;
            SceneView.RepaintAll();
        }

        public static void ApplyRigidTransformDelta(Matrix4x4 deltaWorld)
        {
            _position = deltaWorld.MultiplyPoint3x4(_position);
            _rotation = deltaWorld.rotation * _rotation;
            SceneView.RepaintAll();
        }

        public static void Clear()
        {
            _drawingEnabled = false;
            _sharedMesh = null;
            SceneView.RepaintAll();
        }

        private static void OnSceneGui(SceneView sceneView)
        {
            if (!_drawingEnabled || _sharedMesh == null)
                return;
            if (Event.current.type != EventType.Repaint)
                return;

            Mesh edgeMesh = GetOrBuildEdgeMesh(_sharedMesh);
            Material wireMaterial = GetWireMaterial();
            if (edgeMesh == null || wireMaterial == null)
                return;

            Matrix4x4 matrix = Matrix4x4.TRS(_position, _rotation, _lossyScale);
            CompareFunction previousZTest = Handles.zTest;
            Handles.zTest = CompareFunction.LessEqual;

            wireMaterial.SetColor("_Color", WireColor);
            wireMaterial.SetPass(0);
            Graphics.DrawMeshNow(edgeMesh, matrix);

            Handles.zTest = previousZTest;
        }

        private static Material GetWireMaterial()
        {
            if (_wireMaterial != null)
                return _wireMaterial;

            Shader shader = Shader.Find("Hidden/Internal-Colored");
            if (shader == null)
                return null;

            _wireMaterial = new Material(shader);
            _wireMaterial.hideFlags = HideFlags.HideAndDontSave;
            _wireMaterial.SetInt("_ZWrite", 1);
            _wireMaterial.SetInt("_Cull", (int)CullMode.Off);
            return _wireMaterial;
        }

        private static Mesh GetOrBuildEdgeMesh(Mesh mesh)
        {
            if (mesh == null)
                return null;

            int meshId = mesh.GetInstanceID();
            if (_edgeMeshCache != null && _edgeMeshCacheId == meshId)
                return _edgeMeshCache;

            List<int> lineList = new List<int>();
            for (int subMeshIndex = 0; subMeshIndex < mesh.subMeshCount; subMeshIndex++)
            {
                int[] triangles = mesh.GetIndices(subMeshIndex);
                if (triangles == null || triangles.Length < 3)
                    continue;

                for (int triangleIndex = 0; triangleIndex < triangles.Length; triangleIndex += 3)
                {
                    int a = triangles[triangleIndex];
                    int b = triangles[triangleIndex + 1];
                    int c = triangles[triangleIndex + 2];
                    lineList.Add(a);
                    lineList.Add(b);
                    lineList.Add(b);
                    lineList.Add(c);
                    lineList.Add(c);
                    lineList.Add(a);
                }
            }

            if (lineList.Count < 2)
                return null;

            Mesh edgeMesh = new Mesh();
            edgeMesh.vertices = mesh.vertices;
            edgeMesh.SetIndices(lineList.ToArray(), MeshTopology.Lines, 0);
            edgeMesh.RecalculateBounds();
            edgeMesh.hideFlags = HideFlags.HideAndDontSave;

            if (_edgeMeshCache != null)
                Object.DestroyImmediate(_edgeMeshCache);

            _edgeMeshCache = edgeMesh;
            _edgeMeshCacheId = meshId;
            return _edgeMeshCache;
        }

        private static void CleanupResources()
        {
            if (_edgeMeshCache != null)
            {
                Object.DestroyImmediate(_edgeMeshCache);
                _edgeMeshCache = null;
            }

            _edgeMeshCacheId = 0;

            if (_wireMaterial != null)
            {
                Object.DestroyImmediate(_wireMaterial);
                _wireMaterial = null;
            }
        }
    }
}
