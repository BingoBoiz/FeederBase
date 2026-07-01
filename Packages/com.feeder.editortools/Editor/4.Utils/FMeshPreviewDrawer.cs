using System.Collections.Generic;
using UnityEditor;
using UnityEngine;

namespace Feeder
{
    public static class FMeshPreviewDrawer
    {
        static FMeshPreviewDrawer()
        {
            AssemblyReloadEvents.beforeAssemblyReload += Cleanup;
        }

        private const string DefaultMeshName = "(no mesh)";
        private const float PreviewHeight = 140f;
        private const float StatsHeight = 36f;

        private static readonly Dictionary<string, PreviewRenderUtility> s_PreviewUtilityBySlot = new Dictionary<string, PreviewRenderUtility>();
        private static Material s_SolidMat;
        private static Material s_WireMat;
        // One edge (wireframe) mesh cached per slot so the left/right previews (different meshes)
        // don't thrash a single shared cache and rebuild each other's edges every repaint.
        private static readonly Dictionary<string, EdgeMeshEntry> s_EdgeMeshBySlot = new Dictionary<string, EdgeMeshEntry>();
        private static readonly Dictionary<string, Vector2> s_OrbitBySlot = new Dictionary<string, Vector2>();
        private static readonly Dictionary<string, float> s_ZoomBySlot = new Dictionary<string, float>();
        private static readonly Dictionary<string, Vector3> s_RotationEulerBySlot = new Dictionary<string, Vector3>();
        private const float ZoomMin = 0.15f;
        private const float ZoomMax = 4f;
        private const float ZoomDefault = 1f;
        private const float ZoomScrollSensitivity = 0.08f;
        private static Mesh s_SphereMesh;
        private static Material s_RedMat;

        public static float BlockHeight => PreviewHeight + StatsHeight;

        public static void DrawBlock(Rect blockRect, Mesh mesh, string slotId = null, IReadOnlyList<int> highlightVertexIndices = null)
        {
            var previewHeight = Mathf.Max(20f, blockRect.height - StatsHeight);
            var previewRect = new Rect(blockRect.x, blockRect.y, blockRect.width, previewHeight);
            var statsRect = new Rect(blockRect.x, blockRect.y + previewHeight, blockRect.width, StatsHeight);
            DrawPreview(previewRect, mesh, slotId, highlightVertexIndices);
            DrawStats(statsRect, mesh);
        }

        public static void DrawPreview(Rect rect, Mesh mesh, string slotId = null, IReadOnlyList<int> highlightVertexIndices = null)
        {
            if (rect.width <= 0 || rect.height <= 0) return;

            if (mesh == null)
            {
                EditorGUI.DrawRect(rect, new Color(0.22f, 0.22f, 0.22f));
                return;
            }

            var key = slotId ?? "Default";
            if (!s_OrbitBySlot.TryGetValue(key, out var orbit))
                orbit = new Vector2(20f, 20f);
            if (!s_ZoomBySlot.TryGetValue(key, out var zoom))
                zoom = ZoomDefault;

            if (!s_RotationEulerBySlot.TryGetValue(key, out var rotationEuler))
            {
                rotationEuler = new Vector3(orbit.y, orbit.x, 0f);
            }

            var evt = Event.current;
            var cid = GUIUtility.GetControlID(key.GetHashCode(), FocusType.Passive);
            if (evt != null && rect.Contains(evt.mousePosition))
            {
                if (evt.type == EventType.MouseDown && evt.button == 0)
                    GUIUtility.hotControl = cid;
                if (GUIUtility.hotControl == cid && evt.type == EventType.MouseDrag && evt.button == 0)
                {
                    rotationEuler.y += evt.delta.x;
                    rotationEuler.x -= evt.delta.y;
                    rotationEuler.x = Mathf.Clamp(rotationEuler.x, -89f, 89f);
                    s_RotationEulerBySlot[key] = rotationEuler;
                    s_OrbitBySlot[key] = new Vector2(rotationEuler.y, rotationEuler.x);
                    evt.Use();
                    GUI.changed = true;
                }
                if (evt.type == EventType.MouseUp && evt.button == 0 && GUIUtility.hotControl == cid)
                    GUIUtility.hotControl = 0;
                if (evt.type == EventType.ScrollWheel)
                {
                    zoom -= evt.delta.y * ZoomScrollSensitivity;
                    zoom = Mathf.Clamp(zoom, ZoomMin, ZoomMax);
                    s_ZoomBySlot[key] = zoom;
                    evt.Use();
                    GUI.changed = true;
                }
            }

            var utility = GetPreviewUtility(key);
            utility.BeginPreview(rect, GUIStyle.none);

            var bounds = mesh.bounds;
            var size = bounds.size.magnitude;
            if (size < 0.001f) size = 1f;
            var distance = size * 2.5f / zoom;
            var cameraRot = Quaternion.Euler(rotationEuler);
            utility.camera.transform.position = cameraRot * (Vector3.forward * (-distance));
            utility.camera.transform.rotation = cameraRot;
            utility.camera.nearClipPlane = 0.01f;
            utility.camera.farClipPlane = distance * 4f;
            utility.cameraFieldOfView = 30f;

            var pos = Vector3.zero;
            var rot = Quaternion.identity;

            // solid pass: light gray fill
            var solidMat = GetSolidMaterial();
            if (solidMat != null)
            {
                for (var i = 0; i < mesh.subMeshCount; i++)
                    utility.DrawMesh(mesh, pos, rot, solidMat, i);
            }

            // wireframe pass: edge lines on top
            var edgeMesh = GetOrBuildEdgeMesh(mesh, key);
            var wireMat = GetWireframeMaterial();
            if (edgeMesh != null && wireMat != null)
                utility.DrawMesh(edgeMesh, pos, rot, wireMat, 0);

            // red dots at highlighted vertex positions (e.g. for Mesh Analyze)
            if (highlightVertexIndices != null && highlightVertexIndices.Count > 0)
            {
                var verts = mesh.vertices;
                var sphereMesh = GetOrCreateSphereMesh();
                var redMat = GetRedMaterial();
                var dotScale = size * 0.03f;
                if (dotScale < 0.001f) dotScale = 0.001f;
                for (var idx = 0; idx < highlightVertexIndices.Count; idx++)
                {
                    var vi = highlightVertexIndices[idx];
                    if (vi < 0 || vi >= verts.Length) continue;
                    var world = Matrix4x4.TRS(verts[vi], Quaternion.identity, Vector3.one * dotScale);
                    if (sphereMesh != null && redMat != null)
                        utility.DrawMesh(sphereMesh, world, redMat, 0);
                }
            }

            utility.camera.Render();
            var tex = utility.EndPreview();
            if (tex != null)
                GUI.DrawTexture(rect, tex, ScaleMode.StretchToFill, false);
        }

        /// <summary>Draws one line of stats: name then "X Vertices | Y Triangles | UV1".</summary>
        public static void DrawStats(Rect rect, Mesh mesh)
        {
            var vertCount = mesh != null ? mesh.vertexCount : 0;
            var triCount = mesh != null ? mesh.triangles.Length / 3 : 0;
            var hasUv1 = mesh != null && mesh.uv != null && mesh.uv.Length > 0;

            var line = $"{vertCount} Vertices | {triCount} Triangles | {(hasUv1 ? "UV1" : "no UV1")}";
            var style = new GUIStyle(EditorStyles.miniLabel)
            {
                alignment = TextAnchor.MiddleCenter,
                wordWrap = true
            };
            GUI.Label(rect, line, style);
        }

        // one utility per slot so each block has isolated scene (avoids overlapping mesh)
        private static PreviewRenderUtility GetPreviewUtility(string slotId)
        {
            if (s_PreviewUtilityBySlot.TryGetValue(slotId, out var util) && util != null) return util;
            util = new PreviewRenderUtility();
            s_PreviewUtilityBySlot[slotId] = util;
            return util;
        }

        private static Material GetSolidMaterial()
        {
            if (s_SolidMat != null) return s_SolidMat;

            Shader shader = null;

            // Detect render pipeline
            var rp = UnityEngine.Rendering.GraphicsSettings.currentRenderPipeline;

            if (rp == null)
            {
                // Built-in
                shader = Shader.Find("Standard");
                if (shader == null)
                    shader = Shader.Find("Legacy Shaders/Diffuse");
            }
            else
            {
                var rpType = rp.GetType().Name;

                if (rpType.Contains("Universal"))
                {
                    shader = Shader.Find("Universal Render Pipeline/Lit");
                    if (shader == null)
                        shader = Shader.Find("Universal Render Pipeline/Simple Lit");
                }
                else if (rpType.Contains("HD"))
                {
                    shader = Shader.Find("HDRP/Lit");
                }
            }

            if (shader == null)
                shader = Shader.Find("Unlit/Color");

            if (shader == null)
                return null;

            s_SolidMat = new Material(shader);
            s_SolidMat.hideFlags = HideFlags.HideAndDontSave;

            if (shader.name.Contains("Unlit"))
            {
                s_SolidMat.SetColor("_Color", new Color(0.45f, 0.45f, 0.45f));
            }
            else
            {
                s_SolidMat.color = new Color(0.45f, 0.45f, 0.45f);
            }

            return s_SolidMat;
        }

        private static Material GetWireframeMaterial()
        {
            if (s_WireMat != null) return s_WireMat;
            var shader = Shader.Find("Hidden/Internal-Colored");
            if (shader == null) return null;
            s_WireMat = new Material(shader);
            s_WireMat.hideFlags = HideFlags.HideAndDontSave;
            s_WireMat.SetInt("_ZWrite", 1);
            s_WireMat.SetInt("_Cull", (int)UnityEngine.Rendering.CullMode.Off);
            s_WireMat.SetColor("_Color", new Color(0.15f, 0.15f, 0.15f));
            return s_WireMat;
        }

        private static Mesh GetOrCreateSphereMesh()
        {
            if (s_SphereMesh != null) return s_SphereMesh;
            var go = GameObject.CreatePrimitive(PrimitiveType.Sphere);
            var mf = go?.GetComponent<MeshFilter>();
            if (mf?.sharedMesh != null)
            {
                s_SphereMesh = Object.Instantiate(mf.sharedMesh);
                s_SphereMesh.hideFlags = HideFlags.HideAndDontSave;
            }
            Object.DestroyImmediate(go);
            return s_SphereMesh;
        }

        private static Material GetRedMaterial()
        {
            if (s_RedMat != null) return s_RedMat;
            var shader = Shader.Find("Unlit/Color");
            if (shader == null) return null;
            s_RedMat = new Material(shader);
            s_RedMat.hideFlags = HideFlags.HideAndDontSave;
            s_RedMat.SetColor("_Color", Color.red);
            return s_RedMat;
        }

        /// <summary>Builds a line mesh from triangle edges for wireframe draw, cached per slot.</summary>
        private static Mesh GetOrBuildEdgeMesh(Mesh mesh, string slotId)
        {
            if (mesh == null) return null;
            var key = string.IsNullOrEmpty(slotId) ? "Default" : slotId;
            var id = mesh.GetInstanceID();
            if (s_EdgeMeshBySlot.TryGetValue(key, out var entry) && entry.Edge != null && entry.MeshId == id)
                return entry.Edge;

            var lineList = new List<int>();
            for (var s = 0; s < mesh.subMeshCount; s++)
            {
                var tris = mesh.GetIndices(s);
                if (tris == null || tris.Length < 3) continue;
                for (var i = 0; i < tris.Length; i += 3)
                {
                    var a = tris[i];
                    var b = tris[i + 1];
                    var c = tris[i + 2];
                    lineList.Add(a); lineList.Add(b);
                    lineList.Add(b); lineList.Add(c);
                    lineList.Add(c); lineList.Add(a);
                }
            }
            if (lineList.Count < 2) return null;

            var edgeMesh = new Mesh();
            edgeMesh.vertices = mesh.vertices;
            edgeMesh.SetIndices(lineList.ToArray(), MeshTopology.Lines, 0);
            edgeMesh.RecalculateBounds();
            edgeMesh.hideFlags = HideFlags.HideAndDontSave;

            if (entry != null && entry.Edge != null)
                Object.DestroyImmediate(entry.Edge);
            s_EdgeMeshBySlot[key] = new EdgeMeshEntry { MeshId = id, Edge = edgeMesh };
            return edgeMesh;
        }

        private sealed class EdgeMeshEntry
        {
            public int MeshId;
            public Mesh Edge;
        }

        /// <summary>Call when tool is destroyed or domain reload to free resources.</summary>
        public static void Cleanup()
        {
            foreach (var kv in s_PreviewUtilityBySlot)
            {
                kv.Value?.Cleanup();
            }
            s_PreviewUtilityBySlot.Clear();
            foreach (var kv in s_EdgeMeshBySlot)
            {
                if (kv.Value?.Edge != null)
                    Object.DestroyImmediate(kv.Value.Edge);
            }
            s_EdgeMeshBySlot.Clear();
            s_OrbitBySlot.Clear();
            s_ZoomBySlot.Clear();
            if (s_SphereMesh != null)
            {
                Object.DestroyImmediate(s_SphereMesh);
                s_SphereMesh = null;
            }
            s_RotationEulerBySlot.Clear();
            if (s_RedMat != null)
            {
                Object.DestroyImmediate(s_RedMat);
                s_RedMat = null;
            }
        }

        public static Vector3 GetRotationEuler(string slotId)
        {
            var key = string.IsNullOrEmpty(slotId) ? "Default" : slotId;
            if (s_RotationEulerBySlot.TryGetValue(key, out var euler))
                return euler;

            if (!s_OrbitBySlot.TryGetValue(key, out var orbit))
                orbit = new Vector2(20f, 20f);

            euler = new Vector3(orbit.y, orbit.x, 0f);
            s_RotationEulerBySlot[key] = euler;
            return euler;
        }

        public static void SetRotationEuler(string slotId, Vector3 euler)
        {
            var key = string.IsNullOrEmpty(slotId) ? "Default" : slotId;
            s_RotationEulerBySlot[key] = euler;
            s_OrbitBySlot[key] = new Vector2(euler.y, euler.x);
        }
    }
}
