using UnityEditor;
using UnityEngine;

namespace Feeder
{
    public static class FeederDiffStyles
    {
        public const float LineHeight = 16f;
        public const float HunkHeaderHeight = 18f;
        public const float FileHeaderHeight = 26f;
        public const float ItemHeaderHeight = 20f;
        public const float GutterWidth = 46f;
        public const float SignWidth = 14f;

        public static float CodeOffsetX => GutterWidth * 2f + SignWidth;

        private static readonly string[] MonoCandidates =
        {
            "Consolas", "Cascadia Mono", "JetBrains Mono",
            "Menlo", "SF Mono",
            "DejaVu Sans Mono", "Liberation Mono",
            "Courier New", "monospace",
        };

        public static Color AddedRow;
        public static Color RemovedRow;
        public static Color AddedWord;
        public static Color RemovedWord;
        public static Color GutterBg;
        public static Color GutterText;
        public static Color HunkHeaderBg;
        public static Color HunkHeaderText;
        public static Color FileHeaderBg;
        public static Color ItemHeaderBg;
        public static Color Separator;
        public static Color CodeText;
        public static Color AddedBadge;
        public static Color RemovedBadge;
        public static Color BlockedText;

        public static readonly Color HoverFill = new Color(0f, 1f, 1f, 0.12f);
        public static readonly Color AltRowFill = new Color(1f, 1f, 1f, 0.03f);

        private static GUIStyle codeStyle;
        private static GUIStyle signStyle;
        private static GUIStyle gutterStyle;
        private static GUIStyle hunkStyle;
        private static GUIStyle fileHeaderStyle;
        private static GUIStyle badgeStyle;
        private static GUIStyle itemHeaderStyle;

        private static Font monoFont;
        private static int monoFontSize;
        private static float charWidth;
        private static bool cachedProSkin;
        private static bool initialized;

        public static bool HasMonoFont => monoFont != null;

        public static float CharWidth => charWidth > 0f ? charWidth : 7f;

        public static GUIStyle Code => Ensure(ref codeStyle);
        public static GUIStyle Sign => Ensure(ref signStyle);
        public static GUIStyle Gutter => Ensure(ref gutterStyle);
        public static GUIStyle Hunk => Ensure(ref hunkStyle);
        public static GUIStyle FileHeader => Ensure(ref fileHeaderStyle);
        public static GUIStyle Badge => Ensure(ref badgeStyle);
        public static GUIStyle ItemHeader => Ensure(ref itemHeaderStyle);

        private static GUIStyle Ensure(ref GUIStyle style)
        {
            Refresh();
            return style;
        }

        public static void Refresh()
        {
            if (initialized && cachedProSkin == EditorGUIUtility.isProSkin && codeStyle != null)
            {
                return;
            }

            cachedProSkin = EditorGUIUtility.isProSkin;
            initialized = true;
            BuildColors(cachedProSkin);
            BuildStyles();
        }

        private static void BuildColors(bool pro)
        {
            if (pro)
            {
                AddedRow = Hex(0x2E, 0x4B, 0x33);
                RemovedRow = Hex(0x53, 0x30, 0x33);
                AddedWord = Hex(0x3E, 0x74, 0x4A);
                RemovedWord = Hex(0x7A, 0x3B, 0x40);
                GutterBg = Hex(0x2E, 0x2E, 0x2E);
                GutterText = Hex(0x80, 0x80, 0x80);
                HunkHeaderBg = Hex(0x2B, 0x36, 0x4B);
                HunkHeaderText = Hex(0x7F, 0x9C, 0xC4);
                FileHeaderBg = Hex(0x3F, 0x3F, 0x3F);
                ItemHeaderBg = Hex(0x35, 0x35, 0x35);
                Separator = Hex(0x21, 0x21, 0x21);
                CodeText = Hex(0xC8, 0xC8, 0xC8);
                AddedBadge = Hex(0x6A, 0xC2, 0x77);
                RemovedBadge = Hex(0xE5, 0x73, 0x73);
                BlockedText = Hex(0xE0, 0xA0, 0x60);
            }
            else
            {
                AddedRow = Hex(0xDD, 0xF5, 0xDF);
                RemovedRow = Hex(0xFB, 0xE0, 0xE0);
                AddedWord = Hex(0xAB, 0xE9, 0xB3);
                RemovedWord = Hex(0xF5, 0xB7, 0xB7);
                GutterBg = Hex(0xD6, 0xD6, 0xD6);
                GutterText = Hex(0x6E, 0x6E, 0x6E);
                HunkHeaderBg = Hex(0xDC, 0xE6, 0xF5);
                HunkHeaderText = Hex(0x3B, 0x5C, 0x8A);
                FileHeaderBg = Hex(0xCF, 0xCF, 0xCF);
                ItemHeaderBg = Hex(0xDA, 0xDA, 0xDA);
                Separator = Hex(0xA8, 0xA8, 0xA8);
                CodeText = Hex(0x1E, 0x1E, 0x1E);
                AddedBadge = Hex(0x2E, 0x7D, 0x32);
                RemovedBadge = Hex(0xC6, 0x28, 0x28);
                BlockedText = Hex(0x8A, 0x5A, 0x10);
            }
        }

        private static Color Hex(int r, int g, int b)
        {
            return new Color32((byte)r, (byte)g, (byte)b, 255);
        }

        private static void BuildStyles()
        {
            int size = 11;
            Font font = GetMonoFont(size);

            codeStyle = new GUIStyle(EditorStyles.label)
            {
                fontSize = size,
                richText = false,
                wordWrap = false,
                clipping = TextClipping.Clip,
                alignment = TextAnchor.MiddleLeft,
                padding = new RectOffset(0, 0, 0, 0),
                margin = new RectOffset(0, 0, 0, 0),
            };
            codeStyle.normal.textColor = CodeText;
            if (font != null)
            {
                codeStyle.font = font;
            }

            signStyle = new GUIStyle(codeStyle)
            {
                alignment = TextAnchor.MiddleCenter,
            };

            gutterStyle = new GUIStyle(codeStyle)
            {
                alignment = TextAnchor.MiddleRight,
                padding = new RectOffset(0, 6, 0, 0),
            };
            gutterStyle.normal.textColor = GutterText;

            hunkStyle = new GUIStyle(codeStyle)
            {
                alignment = TextAnchor.MiddleLeft,
                padding = new RectOffset(6, 0, 0, 0),
            };
            hunkStyle.normal.textColor = HunkHeaderText;

            fileHeaderStyle = new GUIStyle(EditorStyles.boldLabel)
            {
                richText = false,
                alignment = TextAnchor.MiddleLeft,
                padding = new RectOffset(2, 2, 0, 0),
            };

            itemHeaderStyle = new GUIStyle(EditorStyles.label)
            {
                richText = false,
                alignment = TextAnchor.MiddleLeft,
                padding = new RectOffset(2, 2, 0, 0),
            };

            badgeStyle = new GUIStyle(EditorStyles.miniBoldLabel)
            {
                richText = false,
                alignment = TextAnchor.MiddleRight,
                padding = new RectOffset(4, 4, 0, 0),
            };

            charWidth = codeStyle.CalcSize(new GUIContent("M")).x;
            if (charWidth <= 0f)
            {
                charWidth = 7f;
            }
        }

        private static Font GetMonoFont(int size)
        {
            if (monoFont != null && monoFontSize == size)
            {
                return monoFont;
            }

            DestroyMonoFont();
            monoFontSize = size;
            monoFont = Font.CreateDynamicFontFromOSFont(MonoCandidates, size);
            if (monoFont != null)
            {
                monoFont.hideFlags = HideFlags.HideAndDontSave;
            }

            return monoFont;
        }

        private static void DestroyMonoFont()
        {
            if (monoFont != null)
            {
                Object.DestroyImmediate(monoFont, true);
                monoFont = null;
            }
        }

        [InitializeOnLoadMethod]
        private static void RegisterCleanup()
        {
            AssemblyReloadEvents.beforeAssemblyReload -= OnBeforeReload;
            AssemblyReloadEvents.beforeAssemblyReload += OnBeforeReload;
        }

        private static void OnBeforeReload()
        {
            DestroyMonoFont();
            initialized = false;
            codeStyle = null;
        }
    }
}
