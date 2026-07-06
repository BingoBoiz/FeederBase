#if UNITY_EDITOR
using System.Collections.Generic;
using Feeder.MB.Core;
using UnityEditor;
using UnityEngine;

namespace Feeder
{
    /// <summary>
    /// Collects MeshRenderers from the selected targets (scene objects or prefab assets)
    /// and builds a <see cref="FMeshBakeReport"/> with stats and warnings.
    /// </summary>
    public static class FMeshBakeAnalyzer
    {
        public static FMeshBakeReport Analyze(List<Object> targets, FMeshBakeSettings settings)
        {
            var report = new FMeshBakeReport();
            var seenRenderers = new HashSet<MeshRenderer>();
            var materials = new HashSet<Material>();
            var shaders = new HashSet<Shader>();
            var textures = new HashSet<Texture>();
            var tiledMaterials = new HashSet<Material>();
            int outOfBoundsCount = 0;
            int unreadableCount = 0;
            int withTangents = 0;
            int withoutTangents = 0;

            foreach (Object target in targets)
            {
                var go = target as GameObject;
                if (go == null)
                    continue;

                if (EditorUtility.IsPersistent(go))
                    report.HasPrefabAssets = true;
                else
                    report.HasSceneObjects = true;

                foreach (MeshRenderer mr in go.GetComponentsInChildren<MeshRenderer>(true))
                {
                    if (!seenRenderers.Add(mr))
                        continue;

                    var mf = mr.GetComponent<MeshFilter>();
                    Mesh mesh = mf != null ? mf.sharedMesh : null;
                    if (mesh == null)
                        continue;

                    var row = new FMeshBakeRendererRow
                    {
                        GameObject = mr.gameObject,
                        Name = mr.gameObject.name,
                        VertexCount = mesh.vertexCount,
                        SubMeshCount = mesh.subMeshCount,
                        IsMeshReadable = mesh.isReadable
                    };

                    if (mesh.HasVertexAttribute(UnityEngine.Rendering.VertexAttribute.Tangent))
                        withTangents++;
                    else
                        withoutTangents++;

                    if (mesh.isReadable)
                    {
                        for (int s = 0; s < mesh.subMeshCount; s++)
                            row.TriangleCount += (int)(mesh.GetIndexCount(s) / 3);

                        Rect uvBounds = new Rect();
                        row.HasOutOfBoundsUVs = MB_Utility.hasOutOfBoundsUVs(mesh, ref uvBounds);
                    }
                    else
                    {
                        unreadableCount++;
                    }

                    if (row.HasOutOfBoundsUVs)
                        outOfBoundsCount++;

                    var matNames = new List<string>();
                    foreach (Material mat in mr.sharedMaterials)
                    {
                        if (mat == null)
                            continue;
                        matNames.Add(mat.name);
                        materials.Add(mat);
                        if (mat.shader != null)
                            shaders.Add(mat.shader);
                        string mainTexProp = mat.HasProperty("_MainTex") ? "_MainTex"
                            : mat.HasProperty("_BaseMap") ? "_BaseMap" : null;
                        if (mainTexProp != null &&
                            (mat.GetTextureScale(mainTexProp) != Vector2.one ||
                             mat.GetTextureOffset(mainTexProp) != Vector2.zero))
                            tiledMaterials.Add(mat);

                        Texture tex = mainTexProp != null ? mat.GetTexture(mainTexProp) : null;
                        if (tex != null)
                        {
                            textures.Add(tex);
                            if (string.IsNullOrEmpty(row.MainTextureInfo))
                                row.MainTextureInfo = $"{tex.name} {tex.width}x{tex.height}";
                        }
                    }

                    row.MaterialNames = matNames.Count > 0 ? string.Join(", ", matNames) : "(none)";
                    if (string.IsNullOrEmpty(row.MainTextureInfo))
                        row.MainTextureInfo = "(no texture)";

                    report.TotalVertices += row.VertexCount;
                    report.TotalTriangles += row.TriangleCount;
                    report.Rows.Add(row);
                }
            }

            report.DistinctMaterialCount = materials.Count;
            report.DistinctShaderCount = shaders.Count;
            report.DistinctTextureCount = textures.Count;

            foreach (Texture tex in textures)
            {
                long w = tex.width + settings.atlasPadding * 2;
                long h = tex.height + settings.atlasPadding * 2;
                report.EstimatedAtlasPixels += w * h;
            }

            BuildWarnings(report, settings, shaders, tiledMaterials, outOfBoundsCount, unreadableCount,
                withTangents, withoutTangents);
            return report;
        }

        private static void BuildWarnings(FMeshBakeReport report, FMeshBakeSettings settings,
            HashSet<Shader> shaders, HashSet<Material> tiledMaterials, int outOfBoundsCount, int unreadableCount,
            int withTangents, int withoutTangents)
        {
            if (report.Rows.Count == 0)
            {
                report.Warnings.Add("Không tìm thấy MeshRenderer nào có mesh hợp lệ trong các mục tiêu.");
                return;
            }

            if (report.TotalVertices > 65534)
                report.Warnings.Add(
                    $"Tổng số đỉnh ({report.TotalVertices:N0}) vượt 65.534. Mesh gộp sẽ dùng index buffer 32-bit — một số GPU mobile rất cũ không hỗ trợ.");

            if (shaders.Count > 1)
                report.Warnings.Add(
                    $"Các object nguồn dùng {shaders.Count} shader khác nhau. Material kết quả sẽ dùng shader của material ĐẦU TIÊN; " +
                    "thuộc tính texture thiếu ở các material khác sẽ được thay bằng màu đặc (fallback).");

            if (outOfBoundsCount > 0)
                report.Warnings.Add(
                    $"{outOfBoundsCount} mesh có UV nằm ngoài 0..1 (texture tile/lặp). Hãy giữ \"Consider Mesh UVs\" BẬT " +
                    "để vùng tile được bake đúng vào atlas.");

            if (tiledMaterials.Count > 0)
                report.Warnings.Add(
                    $"{tiledMaterials.Count} material dùng tiling/offset texture. Vùng tile sẽ được bake vào atlas; " +
                    $"vùng tile lớn bị giới hạn ở Max Tiling Bake Size ({settings.maxTilingBakeSize}px) nên có thể mất chi tiết.");

            if (withTangents > 0 && withoutTangents > 0)
                report.Warnings.Add(
                    $"{withoutTangents} mesh không có tangent trong khi {withTangents} mesh có. Tangent sẽ được sinh tự động " +
                    "cho các mesh thiếu — nếu UV của chúng bị suy biến (collapsed), Console sẽ báo \"Could not compute tangents\" " +
                    "và normal map trên các mesh đó có thể hiển thị sai.");

            if (unreadableCount > 0)
                report.Warnings.Add(
                    $"{unreadableCount} mesh không đọc được từ CPU (Read/Write đang tắt). Hãy bật Read/Write trong import settings " +
                    "của model, nếu không bake sẽ thất bại với các mesh này.");

            long atlasCapacity = (long)settings.maxAtlasSize * settings.maxAtlasSize;
            if (report.EstimatedAtlasPixels > atlasCapacity)
                report.Warnings.Add(
                    $"Tổng diện tích texture ước tính (~{report.EstimatedAtlasPixels:N0} px) vượt quá atlas {settings.maxAtlasSize}x{settings.maxAtlasSize}. " +
                    "Texture nguồn sẽ bị thu nhỏ để vừa atlas.");
        }
    }
}
#endif
