#if UNITY_EDITOR
using UnityEditor;
using UnityEngine;

namespace Feeder
{
    /// <summary>Draws Scene view overlays for mesh palette colorizer selections.</summary>
    public static class MeshPaletteSceneOverlay
    {
        public static void Draw(
            Renderer renderer,
            MeshColorAnalysis analysis,
            int hoverIndex,
            bool colorEditing,
            Color highlightColor)
        {
            if (colorEditing) return;
            if (renderer == null) return;
            if (analysis.usedColors == null || analysis.subTriangles == null) return;
            if (analysis.vertices == null || analysis.vertices.Length == 0) return;
            if (hoverIndex < 0 || hoverIndex >= analysis.usedColors.Count) return;

            var usedColor = analysis.usedColors[hoverIndex];
            var verts = analysis.vertices;
            var subTriangles = analysis.subTriangles;
            Matrix4x4 matrix = renderer.transform.localToWorldMatrix;

            Color prevColor = Handles.color;
            var prevZ = Handles.zTest;
            Handles.zTest = UnityEngine.Rendering.CompareFunction.LessEqual;
            Handles.color = highlightColor;

            for (int k = 0; k < usedColor.triStarts.Count; k++)
            {
                int t = usedColor.triStarts[k];
                int ia = subTriangles[t], ib = subTriangles[t + 1], ic = subTriangles[t + 2];
                if (ia >= verts.Length || ib >= verts.Length || ic >= verts.Length)
                    continue;

                Vector3 a = matrix.MultiplyPoint3x4(verts[ia]);
                Vector3 b = matrix.MultiplyPoint3x4(verts[ib]);
                Vector3 c = matrix.MultiplyPoint3x4(verts[ic]);

                Vector3 n = Vector3.Cross(b - a, c - a).normalized * 0.0015f;
                a += n;
                b += n;
                c += n;

                Handles.DrawAAConvexPolygon(a, b, c);
            }

            Handles.color = prevColor;
            Handles.zTest = prevZ;
        }
    }
}
#endif
