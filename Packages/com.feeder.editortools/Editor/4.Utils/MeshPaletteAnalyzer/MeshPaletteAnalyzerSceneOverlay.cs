#if UNITY_EDITOR
using System.Collections.Generic;
using UnityEditor;
using UnityEngine;

namespace Feeder
{
    /// <summary>
    /// Scene view drawing for the analyzer: a colored wire box + label per classification for
    /// the whole scene, and a translucent triangle overlay for the hovered/selected mesh.
    /// </summary>
    public static class MeshPaletteAnalyzerSceneOverlay
    {
        private static GUIStyle s_LabelStyle;

        public static Color VerdictColor(MeshPaletteVerdict v)
        {
            switch (v)
            {
                case MeshPaletteVerdict.PaletteReady: return new Color(0.30f, 0.85f, 0.40f);
                case MeshPaletteVerdict.Gradient: return new Color(0.95f, 0.35f, 0.30f);
                case MeshPaletteVerdict.Pattern: return new Color(0.98f, 0.62f, 0.20f);
                case MeshPaletteVerdict.Uncertain: return new Color(0.95f, 0.85f, 0.25f);
                default: return new Color(0.55f, 0.55f, 0.55f); // Skipped
            }
        }

        public static void Draw(
            IReadOnlyList<MeshPaletteClassification> results,
            int hoverIndex,
            bool showLabels,
            IReadOnlyDictionary<MeshPaletteVerdict, bool> visible)
        {
            if (results == null || Event.current == null || Event.current.type != EventType.Repaint)
                return;

            Color prevColor = Handles.color;
            var prevZ = Handles.zTest;
            Handles.zTest = UnityEngine.Rendering.CompareFunction.LessEqual;

            for (int i = 0; i < results.Count; i++)
            {
                MeshPaletteClassification c = results[i];
                if (c == null || c.renderer == null) continue;
                if (visible != null && visible.TryGetValue(c.verdict, out bool show) && !show) continue;

                Color col = VerdictColor(c.verdict);
                Handles.color = col;
                Handles.DrawWireCube(c.worldBounds.center, c.worldBounds.size);

                if (showLabels)
                    DrawLabel(c, col);
            }

            if (hoverIndex >= 0 && hoverIndex < results.Count)
                DrawTriangleOverlay(results[hoverIndex]);

            Handles.color = prevColor;
            Handles.zTest = prevZ;
        }

        private static void DrawLabel(MeshPaletteClassification c, Color col)
        {
            EnsureStyle();
            s_LabelStyle.normal.textColor = col;
            Vector3 pos = c.worldBounds.center + Vector3.up * (c.worldBounds.extents.y + 0.05f);
            Handles.Label(pos, $"{c.DisplayName}\n{c.verdict}: {c.reason}", s_LabelStyle);
        }

        private static void DrawTriangleOverlay(MeshPaletteClassification c)
        {
            if (c == null || c.renderer == null || c.mesh == null || !c.mesh.isReadable)
                return;

            Vector3[] verts = c.mesh.vertices;
            int slot = Mathf.Clamp(c.materialSlot, 0, c.mesh.subMeshCount - 1);
            int[] tris = c.mesh.GetTriangles(slot);
            Matrix4x4 matrix = c.renderer.transform.localToWorldMatrix;

            Color fill = VerdictColor(c.verdict);
            fill.a = 0.28f;
            Handles.color = fill;

            for (int t = 0; t + 2 < tris.Length; t += 3)
            {
                int ia = tris[t], ib = tris[t + 1], ic = tris[t + 2];
                if (ia >= verts.Length || ib >= verts.Length || ic >= verts.Length) continue;

                Vector3 a = matrix.MultiplyPoint3x4(verts[ia]);
                Vector3 b = matrix.MultiplyPoint3x4(verts[ib]);
                Vector3 cc = matrix.MultiplyPoint3x4(verts[ic]);

                Vector3 n = Vector3.Cross(b - a, cc - a).normalized * 0.0015f;
                Handles.DrawAAConvexPolygon(a + n, b + n, cc + n);
            }
        }

        private static void EnsureStyle()
        {
            if (s_LabelStyle != null) return;
            s_LabelStyle = new GUIStyle(EditorStyles.boldLabel)
            {
                fontSize = 10,
                alignment = TextAnchor.UpperLeft,
                richText = false
            };
        }
    }
}
#endif
