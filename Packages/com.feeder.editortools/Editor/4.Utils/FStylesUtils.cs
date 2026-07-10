using UnityEditor;
using UnityEngine;

namespace Feeder
{
    public static class FStylesUtils
    {
        public static readonly Color FBlue = new Color(0.2f, 0.5f, 1f, 0.42f);
        public static readonly Color FInfoTeal = new Color(0.2f, 0.75f, 0.55f, 0.28f);

        private static GUIStyle s_DescriptionStyle;
        private static GUIStyle s_InfoBoxStyle;
        private static float s_LastContentWidth = 400f;

        public static void DrawDescription(string text)
        {
            EnsureStyles();
            DrawBox(text, FBlue, s_DescriptionStyle);
        }

        public static void DrawInfoBox(string text)
        {
            EnsureStyles();
            DrawBox(text, FInfoTeal, s_InfoBoxStyle);
        }

        private static void DrawBox(string text, Color bgColor, GUIStyle style)
        {
            EnsureStyles();

            // Use cached width from previous frame's actual layout rect.
            // EditorGUIUtility.currentViewWidth returns full window width (including sidebar),
            // which causes CalcHeight to underestimate the required height in OdinMenuEditorWindow.
            GUIContent content = new GUIContent(text);
            float innerWidth = Mathf.Max(s_LastContentWidth - 16f, 50f);
            float textHeight = style.CalcHeight(content, innerWidth);
            float totalHeight = textHeight + 12f; // 6px top + 6px bottom padding

            Rect rect = GUILayoutUtility.GetRect(
                GUIContent.none,
                GUIStyle.none,
                GUILayout.ExpandWidth(true),
                GUILayout.Height(totalHeight)
            );

            // rect.width is 0 during Layout pass; update only when we have a real value
            if (rect.width > 10f)
                s_LastContentWidth = rect.width;

            EditorGUI.DrawRect(rect, bgColor);

            Rect padded = new Rect(rect.x + 8, rect.y + 6, rect.width - 16, rect.height - 12);
            EditorGUI.LabelField(padded, text, style);
        }

        private static void EnsureStyles()
        {
            if (s_DescriptionStyle == null)
            {
                s_DescriptionStyle = new GUIStyle(EditorStyles.wordWrappedLabel)
                {
                    fontSize = 12,
                    fontStyle = FontStyle.Bold,
                    alignment = TextAnchor.MiddleLeft,
                    stretchWidth = true
                };
                s_DescriptionStyle.normal.textColor = EditorStyles.label.normal.textColor;
            }

            if (s_InfoBoxStyle == null)
            {
                s_InfoBoxStyle = new GUIStyle(EditorStyles.wordWrappedLabel)
                {
                    fontSize = 11,
                    fontStyle = FontStyle.Normal,
                    alignment = TextAnchor.UpperLeft,
                    stretchWidth = true
                };
                s_InfoBoxStyle.normal.textColor = EditorStyles.label.normal.textColor;
            }
        }
    }
}
