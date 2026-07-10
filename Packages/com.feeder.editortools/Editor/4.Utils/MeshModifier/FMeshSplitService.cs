#if UNITY_EDITOR
using System;
using System.Collections.Generic;
using System.IO;
using UnityEditor;
using UnityEngine;
using UnityEngine.Rendering;
using Object = UnityEngine.Object;

namespace Feeder
{
    /// <summary>
    /// Builds part meshes from triangle subsets and saves them as assets plus a prefab
    /// with one child per part. Source objects are never modified.
    /// </summary>
    public static class FMeshSplitService
    {
        private const string ProgressTitle = "Feeder Mesh Splitter";

        /// <summary>
        /// Splits the analyzed mesh into the given pieces (name + global triangle indices).
        /// Writes one Mesh asset per piece and one prefab with a child per piece.
        /// </summary>
        public static FMeshSplitResult Split(FMeshSplitSession session, FMeshSplitSettings settings,
            List<(string name, List<int> triangleIndices)> pieces, out string error)
        {
            error = Validate(session, settings, pieces);
            if (error != null)
                return null;

            FMeshSplitAnalysis a = session.Analysis;
            Mesh sourceMesh = a.Mesh;
            Renderer sourceRenderer = session.Target.GetComponent<Renderer>();
            Material[] sourceMaterials = sourceRenderer != null ? sourceRenderer.sharedMaterials : new Material[0];

            FMeshSplitResult result = null;
            GameObject prefabRoot = null;

            try
            {
                EnsureFolder(settings.PartFolder.Replace('\\', '/').TrimEnd('/'));

                var attributes = SourceAttributes.Read(sourceMesh, a.Vertices);
                var usedNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                result = new FMeshSplitResult { PrefabPath = settings.PrefabPath };
                prefabRoot = new GameObject(settings.baseName);

                for (int p = 0; p < pieces.Count; p++)
                {
                    string pieceName = MakeUniqueName(SanitizeName(pieces[p].name, $"Part{p}"), usedNames);
                    EditorUtility.DisplayProgressBar(ProgressTitle,
                        $"Tạo mesh \"{pieceName}\" ({p + 1}/{pieces.Count})...", (float)p / pieces.Count);

                    Mesh partMesh = BuildPartMesh(a, attributes, pieces[p].triangleIndices, out List<int> usedSubmeshes);
                    partMesh.name = $"{settings.baseName}_{pieceName}";

                    string meshPath = settings.PartMeshPath(pieceName);
                    AssetDatabase.CreateAsset(partMesh, meshPath);
                    result.MeshPaths.Add(meshPath);

                    var child = new GameObject(partMesh.name);
                    child.transform.SetParent(prefabRoot.transform, false);
                    child.AddComponent<MeshFilter>().sharedMesh = partMesh;

                    var materials = new Material[usedSubmeshes.Count];
                    for (int s = 0; s < usedSubmeshes.Count; s++)
                    {
                        int sourceIndex = Mathf.Clamp(usedSubmeshes[s], 0, Mathf.Max(0, sourceMaterials.Length - 1));
                        materials[s] = sourceMaterials.Length > 0 ? sourceMaterials[sourceIndex] : null;
                    }
                    child.AddComponent<MeshRenderer>().sharedMaterials = materials;
                }

                EditorUtility.DisplayProgressBar(ProgressTitle, "Lưu prefab...", 0.95f);
                GameObject prefabAsset = PrefabUtility.SaveAsPrefabAsset(prefabRoot, settings.PrefabPath);

                if (settings.placeResultInScene && prefabAsset != null)
                {
                    var instance = (GameObject)PrefabUtility.InstantiatePrefab(prefabAsset);
                    Transform sourceT = session.Target.transform;
                    instance.transform.SetPositionAndRotation(sourceT.position, sourceT.rotation);
                    instance.transform.localScale = sourceT.lossyScale;
                    Undo.RegisterCreatedObjectUndo(instance, "Split Mesh Result");
                }

                return result;
            }
            catch (Exception e)
            {
                Debug.LogException(e);
                error = "Tách mesh gặp lỗi ngoại lệ: " + e.Message + "\n\nXem Console để biết chi tiết.";
                result = null;
                return null;
            }
            finally
            {
                if (prefabRoot != null)
                    Object.DestroyImmediate(prefabRoot);

                EditorUtility.ClearProgressBar();
                AssetDatabase.SaveAssets();
                AssetDatabase.Refresh();

                if (result != null)
                    ReportSuccess(result);
            }
        }

        private static string Validate(FMeshSplitSession session, FMeshSplitSettings settings,
            List<(string name, List<int> triangleIndices)> pieces)
        {
            if (session == null || session.Target == null)
                return "Chưa có object mục tiêu.";
            if (session.Analysis == null)
                return "Chưa Analyze mesh.";
            if (session.Analysis.IsStale)
                return "Mesh đã thay đổi sau khi Analyze. Hãy bấm Analyze lại.";
            if (string.IsNullOrWhiteSpace(settings.baseName))
                return "Base Name không được để trống.";
            if (settings.baseName.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0)
                return "Base Name chứa ký tự không hợp lệ cho tên file.";

            string folder = settings.outputFolder.Replace('\\', '/').TrimEnd('/');
            if (!folder.StartsWith("Assets/") && folder != "Assets")
                return "Output Folder phải nằm trong thư mục Assets của project.";

            int nonEmpty = 0;
            if (pieces != null)
                foreach (var piece in pieces)
                    if (piece.triangleIndices != null && piece.triangleIndices.Count > 0)
                        nonEmpty++;
            if (nonEmpty < 2)
                return "Cần ít nhất 2 phần không rỗng để tách. Hãy chọn một phần của mesh trước.";
            return null;
        }

        // Vertex attribute arrays fetched once per Split call (mesh property getters allocate).
        private sealed class SourceAttributes
        {
            public Vector3[] Positions;
            public Vector3[] Normals;
            public Vector4[] Tangents;
            public Color32[] Colors;
            public Vector2[] Uv0, Uv1, Uv2, Uv3;

            public static SourceAttributes Read(Mesh mesh, Vector3[] cachedPositions)
            {
                var s = new SourceAttributes { Positions = cachedPositions };
                if (mesh.HasVertexAttribute(VertexAttribute.Normal)) s.Normals = mesh.normals;
                if (mesh.HasVertexAttribute(VertexAttribute.Tangent)) s.Tangents = mesh.tangents;
                if (mesh.HasVertexAttribute(VertexAttribute.Color)) s.Colors = mesh.colors32;
                if (mesh.HasVertexAttribute(VertexAttribute.TexCoord0)) s.Uv0 = mesh.uv;
                if (mesh.HasVertexAttribute(VertexAttribute.TexCoord1)) s.Uv1 = mesh.uv2;
                if (mesh.HasVertexAttribute(VertexAttribute.TexCoord2)) s.Uv2 = mesh.uv3;
                if (mesh.HasVertexAttribute(VertexAttribute.TexCoord3)) s.Uv3 = mesh.uv4;
                return s;
            }
        }

        /// <summary>
        /// Builds one Mesh from a subset of global triangle indices, remapping vertices and
        /// keeping one output submesh per source submesh used (so materials stay correct).
        /// </summary>
        private static Mesh BuildPartMesh(FMeshSplitAnalysis a, SourceAttributes src,
            List<int> triIndices, out List<int> usedSubmeshes)
        {
            // Group triangles by source submesh, preserving submesh order.
            var submeshTris = new SortedDictionary<int, List<int>>();
            foreach (int t in triIndices)
            {
                int s = a.TriangleSubmesh[t];
                if (!submeshTris.TryGetValue(s, out List<int> list))
                    submeshTris.Add(s, list = new List<int>());
                list.Add(t);
            }

            var remap = new Dictionary<int, int>();
            var newPositions = new List<Vector3>();
            var newNormals = src.Normals != null ? new List<Vector3>() : null;
            var newTangents = src.Tangents != null ? new List<Vector4>() : null;
            var newColors = src.Colors != null ? new List<Color32>() : null;
            var newUv0 = src.Uv0 != null ? new List<Vector2>() : null;
            var newUv1 = src.Uv1 != null ? new List<Vector2>() : null;
            var newUv2 = src.Uv2 != null ? new List<Vector2>() : null;
            var newUv3 = src.Uv3 != null ? new List<Vector2>() : null;

            var newIndicesPerSubmesh = new List<List<int>>();
            usedSubmeshes = new List<int>();

            foreach (var pair in submeshTris)
            {
                usedSubmeshes.Add(pair.Key);
                var indices = new List<int>(pair.Value.Count * 3);
                foreach (int t in pair.Value)
                {
                    for (int k = 0; k < 3; k++)
                    {
                        int old = a.Triangles[t * 3 + k];
                        if (!remap.TryGetValue(old, out int idx))
                        {
                            idx = newPositions.Count;
                            remap.Add(old, idx);
                            newPositions.Add(src.Positions[old]);
                            newNormals?.Add(src.Normals[old]);
                            newTangents?.Add(src.Tangents[old]);
                            newColors?.Add(src.Colors[old]);
                            newUv0?.Add(src.Uv0[old]);
                            newUv1?.Add(src.Uv1[old]);
                            newUv2?.Add(src.Uv2[old]);
                            newUv3?.Add(src.Uv3[old]);
                        }
                        indices.Add(idx);
                    }
                }
                newIndicesPerSubmesh.Add(indices);
            }

            var mesh = new Mesh();
            mesh.indexFormat = newPositions.Count > 65534 ? IndexFormat.UInt32 : IndexFormat.UInt16;
            mesh.SetVertices(newPositions);
            if (newNormals != null) mesh.SetNormals(newNormals);
            if (newTangents != null) mesh.SetTangents(newTangents);
            if (newColors != null) mesh.SetColors(newColors);
            if (newUv0 != null) mesh.SetUVs(0, newUv0);
            if (newUv1 != null) mesh.SetUVs(1, newUv1);
            if (newUv2 != null) mesh.SetUVs(2, newUv2);
            if (newUv3 != null) mesh.SetUVs(3, newUv3);

            mesh.subMeshCount = newIndicesPerSubmesh.Count;
            for (int s = 0; s < newIndicesPerSubmesh.Count; s++)
                mesh.SetTriangles(newIndicesPerSubmesh[s], s);

            mesh.RecalculateBounds();
            return mesh;
        }

        private static string SanitizeName(string name, string fallback)
        {
            if (string.IsNullOrWhiteSpace(name))
                return fallback;

            char[] invalid = Path.GetInvalidFileNameChars();
            var chars = name.Trim().ToCharArray();
            for (int i = 0; i < chars.Length; i++)
                if (Array.IndexOf(invalid, chars[i]) >= 0)
                    chars[i] = '_';
            return new string(chars);
        }

        private static string MakeUniqueName(string name, HashSet<string> used)
        {
            string candidate = name;
            int suffix = 1;
            while (!used.Add(candidate))
                candidate = $"{name}_{suffix++}";
            return candidate;
        }

        private static void EnsureFolder(string folder)
        {
            if (AssetDatabase.IsValidFolder(folder))
                return;

            string parent = Path.GetDirectoryName(folder)?.Replace('\\', '/');
            if (string.IsNullOrEmpty(parent))
                return;

            EnsureFolder(parent);
            AssetDatabase.CreateFolder(parent, Path.GetFileName(folder));
        }

        private static void ReportSuccess(FMeshSplitResult result)
        {
            var prefab = AssetDatabase.LoadAssetAtPath<GameObject>(result.PrefabPath);
            if (prefab != null)
                EditorGUIUtility.PingObject(prefab);

            var lines = new List<string>
            {
                "<color=#4CE04C><b>[Feeder Mesh Splitter]</b></color> Split completed. Created assets:",
                "  " + result.PrefabPath
            };
            foreach (string meshPath in result.MeshPaths)
                lines.Add("  " + meshPath);

            Debug.Log(string.Join("\n", lines), prefab);
        }
    }
}
#endif
