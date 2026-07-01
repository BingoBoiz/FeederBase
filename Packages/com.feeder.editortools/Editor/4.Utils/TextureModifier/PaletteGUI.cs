#if UNITY_EDITOR
using System.Collections.Generic;
using UnityEditor;
using UnityEngine;

namespace Feeder
{
    /// <summary>Target cell state for a changed palette color.</summary>
    public enum SwatchKind
    {
        New,
        Reused,
        Unplaced
    }

    /// <summary>Links a changed color row to its target palette cell for preview drawing.</summary>
    public class SwatchMarker
    {
        public int number;
        public SwatchKind kind;
        public Rect cellRect;
        public Color newColor;
    }

    /// <summary>
    /// IMGUI helpers for drawing palette textures, transparency backgrounds, target cell markers,
    /// number badges, hover callouts, and the preview legend.
    /// </summary>
    public static class PaletteGUI
    {
        private static Texture2D _checker;
        private static GUIStyle _badgeStyle;
        private static GUIStyle _calloutStyle;

        private static readonly Color BorderColor = new Color(0f, 0f, 0f, 0.65f);
        private static readonly Color NewFrame = new Color(0.20f, 0.90f, 0.36f, 1f);
        private static readonly Color ReuseFrame = new Color(1f, 0.78f, 0.12f, 1f);
        private static readonly Color UnplacedFrame = new Color(1f, 0.25f, 0.25f, 1f);

        private static Color FrameOf(SwatchKind k) =>
            k == SwatchKind.New ? NewFrame : (k == SwatchKind.Reused ? ReuseFrame : UnplacedFrame);

        /// <summary>
        /// Draws a labeled texture that always fills the available layout width, keeping the source
        /// aspect ratio, with optional target cell markers.
        /// </summary>
        public static void DrawTextureFitWidth(
            string caption, Texture2D tex, List<SwatchMarker> markers, int hoveredRowIndex, int texW, int texH)
        {
            EditorGUILayout.LabelField(caption, EditorStyles.miniBoldLabel);

            float aspect = texH > 0 ? (float)texW / texH : 1f;
            Rect imgRect = GUILayoutUtility.GetAspectRect(aspect);

            DrawCheckerboard(imgRect);
            if (tex != null)
                GUI.DrawTexture(imgRect, tex, ScaleMode.StretchToFill, true);
            DrawBorder(imgRect, BorderColor, 1f);

            if (markers != null && texW > 0)
                DrawMarkers(imgRect, markers, hoveredRowIndex, imgRect.width / texW, texH);
        }

        /// <summary>Draws a labeled texture column with optional target cell markers.</summary>
        public static void DrawTextureColumn(
            string caption, Texture2D tex, float drawW, float drawH,
            List<SwatchMarker> markers, int hoveredRowIndex, float zoom, int texH)
        {
            using (new EditorGUILayout.VerticalScope(GUILayout.Width(drawW)))
            {
                EditorGUILayout.LabelField(caption, EditorStyles.miniBoldLabel, GUILayout.Width(drawW));

                Rect imgRect = GUILayoutUtility.GetRect(
                    drawW, drawH, GUILayout.ExpandWidth(false), GUILayout.ExpandHeight(false));

                DrawCheckerboard(imgRect);
                if (tex != null)
                    GUI.DrawTexture(imgRect, tex, ScaleMode.StretchToFill, true);
                DrawBorder(imgRect, BorderColor, 1f);

                if (markers != null)
                    DrawMarkers(imgRect, markers, hoveredRowIndex, zoom, texH);
            }
        }

        /// <summary>Draws changed-color markers on the palette texture.</summary>
        public static void DrawMarkers(
            Rect imgRect, List<SwatchMarker> markers, int hoveredRowIndex, float zoom, int texH)
        {
            if (markers == null || markers.Count == 0) return;

            SwatchMarker hovered = null;
            Rect hoveredRect = default;

            for (int i = 0; i < markers.Count; i++)
            {
                var m = markers[i];
                if (m.kind == SwatchKind.Unplaced) continue;

                Rect src = m.cellRect;
                Rect r = new Rect(
                    imgRect.x + src.x * zoom,
                    imgRect.y + (texH - src.y - src.height) * zoom,
                    src.width * zoom,
                    src.height * zoom);

                bool isHover = (m.number - 1) == hoveredRowIndex;
                Color frame = FrameOf(m.kind);

                if (isHover)
                {
                    EditorGUI.DrawRect(r, new Color(frame.r, frame.g, frame.b, 0.35f));
                    DrawBorder(r, Color.white, 3f);
                    DrawBorder(new Rect(r.x + 1.5f, r.y + 1.5f, r.width - 3f, r.height - 3f), frame, 2.5f);
                    hovered = m; hoveredRect = r;
                }
                else
                {
                    DrawBorder(r, Color.white, 2f);
                    DrawBorder(new Rect(r.x + 1f, r.y + 1f, r.width - 2f, r.height - 2f), frame, 1.5f);
                }

                if (Mathf.Min(r.width, r.height) >= 12f)
                    DrawNumberBadge(r, m.number, frame);
            }

            if (hovered != null)
                DrawCallout(imgRect, hoveredRect, hovered);
        }

        private static void DrawNumberBadge(Rect cell, int number, Color fill)
        {
            float s = Mathf.Clamp(Mathf.Min(cell.width, cell.height) * 0.6f, 12f, 18f);
            var badge = new Rect(cell.x + 1f, cell.y + 1f, s, s);
            EditorGUI.DrawRect(badge, new Color(0f, 0f, 0f, 0.85f));
            DrawBorder(badge, fill, 1.5f);
            GUI.Label(badge, number.ToString(), BadgeStyle());
        }

        private static void DrawCallout(Rect imgRect, Rect cell, SwatchMarker m)
        {
            string kindText = m.kind == SwatchKind.New ? "new cell"
                            : m.kind == SwatchKind.Reused ? "reused" : "no space";
            var content = new GUIContent($"#{m.number} -> {kindText}");
            var style = CalloutStyle();
            Vector2 size = style.CalcSize(content);
            size.x += 8f; size.y += 4f;

            float x = Mathf.Clamp(cell.x, imgRect.x, imgRect.xMax - size.x);
            float y = cell.y - size.y - 2f;
            if (y < imgRect.y) y = cell.yMax + 2f;
            var box = new Rect(x, y, size.x, size.y);

            EditorGUI.DrawRect(box, new Color(0f, 0f, 0f, 0.9f));
            DrawBorder(box, FrameOf(m.kind), 1.5f);
            GUI.Label(box, content, style);
        }

        /// <summary>Draws the marker color legend.</summary>
        public static void DrawLegend()
        {
            using (new EditorGUILayout.HorizontalScope())
            {
                LegendChip(NewFrame, "new cell");
                GUILayout.Space(10f);
                LegendChip(ReuseFrame, "reused");
                GUILayout.Space(10f);
                LegendChip(UnplacedFrame, "no space");
                GUILayout.FlexibleSpace();
            }
        }

        private static void LegendChip(Color c, string label)
        {
            Rect chip = GUILayoutUtility.GetRect(14, 14, GUILayout.Width(14), GUILayout.Height(14));
            EditorGUI.DrawRect(chip, new Color(0f, 0f, 0f, 0.85f));
            DrawBorder(chip, c, 2f);
            EditorGUILayout.LabelField(label, EditorStyles.miniLabel, GUILayout.Width(62));
        }

        /// <summary>Gets the frame color for a marker state.</summary>
        public static Color FrameColor(SwatchKind k) => FrameOf(k);

        private static GUIStyle BadgeStyle()
        {
            if (_badgeStyle == null)
            {
                _badgeStyle = new GUIStyle(EditorStyles.miniBoldLabel)
                {
                    alignment = TextAnchor.MiddleCenter,
                    fontSize = 9
                };
                _badgeStyle.normal.textColor = Color.white;
            }
            return _badgeStyle;
        }

        private static GUIStyle CalloutStyle()
        {
            if (_calloutStyle == null)
            {
                _calloutStyle = new GUIStyle(EditorStyles.miniBoldLabel)
                {
                    alignment = TextAnchor.MiddleCenter,
                    padding = new RectOffset(4, 4, 2, 2)
                };
                _calloutStyle.normal.textColor = Color.white;
            }
            return _calloutStyle;
        }

        public static void DrawBorder(Rect r, Color c, float t)
        {
            EditorGUI.DrawRect(new Rect(r.x, r.y, r.width, t), c);
            EditorGUI.DrawRect(new Rect(r.x, r.yMax - t, r.width, t), c);
            EditorGUI.DrawRect(new Rect(r.x, r.y, t, r.height), c);
            EditorGUI.DrawRect(new Rect(r.xMax - t, r.y, t, r.height), c);
        }

        public static void DrawCheckerboard(Rect r)
        {
            var t = GetCheckerTex();
            GUI.DrawTextureWithTexCoords(r, t, new Rect(0f, 0f, r.width / t.width, r.height / t.height));
        }

        private static Texture2D GetCheckerTex()
        {
            if (_checker != null) return _checker;

            const int cell = 8;
            const int size = cell * 2;
            _checker = new Texture2D(size, size, TextureFormat.RGBA32, false)
            {
                hideFlags = HideFlags.HideAndDontSave,
                filterMode = FilterMode.Point,
                wrapMode = TextureWrapMode.Repeat
            };
            var c0 = new Color(0.26f, 0.26f, 0.26f, 1f);
            var c1 = new Color(0.40f, 0.40f, 0.40f, 1f);
            var px = new Color[size * size];
            for (int y = 0; y < size; y++)
                for (int x = 0; x < size; x++)
                    px[y * size + x] = ((x / cell + y / cell) & 1) == 0 ? c0 : c1;
            _checker.SetPixels(px);
            _checker.Apply(false);
            return _checker;
        }
    }
}
#endif
