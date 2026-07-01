#if UNITY_EDITOR
using System;
using System.Collections.Generic;
using UnityEditor;
using UnityEngine;

namespace Feeder
{
    /// <summary>A palette color group used by the mesh, plus the replacement color chosen by the user.</summary>
    public class UsedColor
    {
        public Color32 color;
        public Color newColor;
        public readonly List<int> triStarts = new List<int>();
        public int faceCount;
        public bool Changed => !FTextureModifierUtils.ApproxEqual(newColor, color);
    }

    /// <summary>Result of analyzing which palette colors a target submesh uses.</summary>
    public struct MeshColorAnalysis
    {
        public List<UsedColor> usedColors;
        public int[] subTriangles;
        public Vector3[] vertices;
    }

    /// <summary>
    /// Pure mesh logic for finding palette color groups used by a mesh and building a duplicate mesh
    /// whose affected triangles point to replacement palette cells through UV0.
    /// </summary>
    public static class MeshColorRemapper
    {
        /// <summary>Samples the source color at UV coordinates using repeat wrapping.</summary>
        public static Color32 SampleOriginal(Vector2 uv, Color32[] px, int w, int h)
        {
            float fu = uv.x - Mathf.Floor(uv.x);
            float fv = uv.y - Mathf.Floor(uv.y);
            int x = Mathf.Clamp(Mathf.FloorToInt(fu * w), 0, w - 1);
            int y = Mathf.Clamp(Mathf.FloorToInt(fv * h), 0, h - 1);
            return px[y * w + x];
        }

        /// <summary>Analyzes the palette colors used by the target submesh, grouped by color tolerance.</summary>
        public static MeshColorAnalysis AnalyzeUsedColors(
            Mesh mesh, int materialSlot, int texW, int texH, Color32[] originalPixels, int tol)
        {
            var result = new MeshColorAnalysis { usedColors = new List<UsedColor>() };

            Vector2[] meshUV = mesh.uv;
            if (meshUV == null || meshUV.Length == 0)
            {
                Debug.LogWarning("[Feeder Texture Modifier] Mesh has no UV0 data, or Read/Write is disabled.");
                return result;
            }

            int sub = Mathf.Clamp(materialSlot, 0, mesh.subMeshCount - 1);
            result.subTriangles = mesh.GetTriangles(sub);
            result.vertices = mesh.vertices;

            int[] tris = result.subTriangles;
            var usedColors = result.usedColors;
            for (int t = 0; t < tris.Length; t += 3)
            {
                int a = tris[t], b = tris[t + 1], c = tris[t + 2];
                if (a >= meshUV.Length || b >= meshUV.Length || c >= meshUV.Length) continue;

                Vector2 uv = (meshUV[a] + meshUV[b] + meshUV[c]) / 3f;
                Color32 col = SampleOriginal(uv, originalPixels, texW, texH);

                UsedColor match = null;
                for (int u = 0; u < usedColors.Count; u++)
                {
                    if (FTextureModifierUtils.ColorsClose(usedColors[u].color, col, tol)) { match = usedColors[u]; break; }
                }
                if (match == null)
                {
                    match = new UsedColor { color = col, newColor = (Color)col };
                    usedColors.Add(match);
                }
                match.triStarts.Add(t);
                match.faceCount++;
            }
            return result;
        }

        /// <summary>
        /// Builds a copy of the source mesh. Affected triangles in the target submesh are split so
        /// their UV0 coordinates can point to the replacement palette cell center.
        /// </summary>
        public static Mesh BuildRemappedMesh(Mesh sourceMesh, int materialSlot, Dictionary<UsedColor, Vector2> swatchUV)
        {
            var verts = new List<Vector3>(sourceMesh.vertices);
            var normals = new List<Vector3>(sourceMesh.normals);
            var tangents = new List<Vector4>(sourceMesh.tangents);
            var colors = new List<Color32>(sourceMesh.colors32);
            var uv0 = new List<Vector2>(sourceMesh.uv);
            var uv2 = new List<Vector2>(sourceMesh.uv2);
            var uv3 = new List<Vector2>(sourceMesh.uv3);
            var uv4 = new List<Vector2>(sourceMesh.uv4);
            var bw = new List<BoneWeight>(sourceMesh.boneWeights);

            bool hasNormals = normals.Count == verts.Count;
            bool hasTangents = tangents.Count == verts.Count;
            bool hasColors = colors.Count == verts.Count;
            bool hasUv0 = uv0.Count == verts.Count;
            bool hasUv2 = uv2.Count == verts.Count;
            bool hasUv3 = uv3.Count == verts.Count;
            bool hasUv4 = uv4.Count == verts.Count;
            bool hasBw = bw.Count == verts.Count;

            var triToUV = new Dictionary<int, Vector2>();
            foreach (var kv in swatchUV)
                foreach (int triStart in kv.Key.triStarts)
                    triToUV[triStart] = kv.Value;

            int subTarget = Mathf.Clamp(materialSlot, 0, sourceMesh.subMeshCount - 1);

            var allSubTris = new List<int[]>();
            for (int s = 0; s < sourceMesh.subMeshCount; s++)
            {
                int[] tris = sourceMesh.GetTriangles(s);
                if (s == subTarget)
                {
                    var newTris = new int[tris.Length];
                    for (int t = 0; t < tris.Length; t += 3)
                    {
                        if (triToUV.TryGetValue(t, out Vector2 newUV))
                        {
                            for (int k = 0; k < 3; k++)
                            {
                                int orig = tris[t + k];
                                int ni = verts.Count;
                                verts.Add(verts[orig]);
                                if (hasNormals) normals.Add(normals[orig]);
                                if (hasTangents) tangents.Add(tangents[orig]);
                                if (hasColors) colors.Add(colors[orig]);
                                if (hasUv0) uv0.Add(newUV);
                                if (hasUv2) uv2.Add(uv2[orig]);
                                if (hasUv3) uv3.Add(uv3[orig]);
                                if (hasUv4) uv4.Add(uv4[orig]);
                                if (hasBw) bw.Add(bw[orig]);
                                newTris[t + k] = ni;
                            }
                        }
                        else
                        {
                            newTris[t] = tris[t];
                            newTris[t + 1] = tris[t + 1];
                            newTris[t + 2] = tris[t + 2];
                        }
                    }
                    allSubTris.Add(newTris);
                }
                else
                {
                    allSubTris.Add(tris);
                }
            }

            var mesh = new Mesh { name = sourceMesh.name + "_recolored" };
            mesh.indexFormat = verts.Count > 65535 ? UnityEngine.Rendering.IndexFormat.UInt32 : UnityEngine.Rendering.IndexFormat.UInt16;
            mesh.SetVertices(verts);
            if (hasNormals) mesh.SetNormals(normals);
            if (hasTangents) mesh.SetTangents(tangents);
            if (hasColors) mesh.SetColors(colors);
            if (hasUv0) mesh.SetUVs(0, uv0);
            if (hasUv2) mesh.SetUVs(1, uv2);
            if (hasUv3) mesh.SetUVs(2, uv3);
            if (hasUv4) mesh.SetUVs(3, uv4);
            if (hasBw && sourceMesh.bindposes != null && sourceMesh.bindposes.Length > 0)
            {
                mesh.boneWeights = bw.ToArray();
                mesh.bindposes = sourceMesh.bindposes;
            }

            mesh.subMeshCount = allSubTris.Count;
            for (int s = 0; s < allSubTris.Count; s++)
                mesh.SetTriangles(allSubTris[s], s, true);

            if (!hasNormals) mesh.RecalculateNormals();
            mesh.RecalculateBounds();
            return mesh;
        }

        /// <summary>
        /// Overwrites a writable mesh asset in place. Model-imported meshes create a separate mesh
        /// asset in the generated folder and return that mesh for assignment to the renderer.
        /// </summary>
        public static Mesh SaveOrOverwriteMesh(
            Mesh sourceMesh, Mesh newMesh, Renderer targetRenderer, string generatedFolder,
            out string writtenMeshPath, out bool createdMeshAsset)
        {
            string sourceMeshPath = AssetDatabase.GetAssetPath(sourceMesh);
            AssetImporter sourceImporter = string.IsNullOrEmpty(sourceMeshPath) ? null : AssetImporter.GetAtPath(sourceMeshPath);
            bool canOverwriteInPlace =
                !string.IsNullOrEmpty(sourceMeshPath) &&
                sourceMeshPath.EndsWith(".asset", StringComparison.OrdinalIgnoreCase) &&
                !(sourceImporter is ModelImporter);

            if (canOverwriteInPlace)
            {
                Undo.RegisterCompleteObjectUndo(sourceMesh, "Feeder Texture Modifier Overwrite Mesh");
                OverwriteMeshInPlace(sourceMesh, newMesh);
                EditorUtility.SetDirty(sourceMesh);
                writtenMeshPath = sourceMeshPath;
                createdMeshAsset = false;
                UnityEngine.Object.DestroyImmediate(newMesh);
                return sourceMesh;
            }

            FTextureModifierUtils.EnsureGeneratedFolder(generatedFolder);
            string stamp = DateTime.Now.ToString("yyyyMMdd_HHmmss");
            string safeMeshName = FTextureModifierUtils.MakeSafe(sourceMesh != null ? sourceMesh.name : "Mesh");
            string safeRendererName = FTextureModifierUtils.MakeSafe(targetRenderer != null ? targetRenderer.name : "Renderer");
            writtenMeshPath = AssetDatabase.GenerateUniqueAssetPath($"{generatedFolder}/{safeMeshName}_{safeRendererName}_{stamp}.asset");
            AssetDatabase.CreateAsset(newMesh, writtenMeshPath);
            createdMeshAsset = true;
            return newMesh;
        }

        private static void OverwriteMeshInPlace(Mesh target, Mesh replacement)
        {
            string targetName = target.name;
            target.Clear(false);
            target.name = targetName;
            target.indexFormat = replacement.indexFormat;

            target.vertices = replacement.vertices;
            if (replacement.normals != null && replacement.normals.Length == replacement.vertexCount)
                target.normals = replacement.normals;
            if (replacement.tangents != null && replacement.tangents.Length == replacement.vertexCount)
                target.tangents = replacement.tangents;
            if (replacement.colors32 != null && replacement.colors32.Length == replacement.vertexCount)
                target.colors32 = replacement.colors32;

            if (replacement.uv != null && replacement.uv.Length == replacement.vertexCount)
                target.uv = replacement.uv;
            if (replacement.uv2 != null && replacement.uv2.Length == replacement.vertexCount)
                target.uv2 = replacement.uv2;
            if (replacement.uv3 != null && replacement.uv3.Length == replacement.vertexCount)
                target.uv3 = replacement.uv3;
            if (replacement.uv4 != null && replacement.uv4.Length == replacement.vertexCount)
                target.uv4 = replacement.uv4;

            if (replacement.boneWeights != null && replacement.boneWeights.Length == replacement.vertexCount)
                target.boneWeights = replacement.boneWeights;
            if (replacement.bindposes != null && replacement.bindposes.Length > 0)
                target.bindposes = replacement.bindposes;

            target.subMeshCount = replacement.subMeshCount;
            for (int s = 0; s < replacement.subMeshCount; s++)
                target.SetIndices(replacement.GetIndices(s), replacement.GetTopology(s), s, false);

            target.bounds = replacement.bounds;
        }
    }
}
#endif
