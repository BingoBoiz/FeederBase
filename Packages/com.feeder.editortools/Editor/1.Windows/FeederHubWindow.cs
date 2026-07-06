using UnityEditor;
using UnityEngine;

namespace Feeder
{
    /// <summary>
    /// Launcher hub with a multi-layer Matrix rain background (see
    /// <see cref="MatrixRainRenderer"/>) and a one-per-editor-session
    /// typewriter intro gated by <see cref="SessionState"/>.
    /// </summary>
    public class FeederHubWindow : EditorWindow, IHasCustomMenu
    {
        [MenuItem("Tools/Feeder/Feeder Hub", priority = -20)]
        private static void OpenWindow()
        {
            GetOrCreateWindow();
        }

        [MenuItem("Tools/Feeder/Feeder Hub (Intro)", priority = -19)]
        private static void OpenWindowWithIntro()
        {
            GetOrCreateWindow().ResetIntro();
        }

        private static FeederHubWindow GetOrCreateWindow()
        {
            var window = GetWindow<FeederHubWindow>();
            window.titleContent = FeederIconCatalog.CreateWindowTitle("Feeder Hub", FeederIconCatalog.FeederHubTitleIcon);
            window.minSize = new Vector2(480f, 360f);
            window.Show();
            return window;
        }

        private const string IntroSessionKey = "Feeder.Hub.IntroPlayed";
        private const string IntroLine1 = "Welcome back, Architect.";
        private const string IntroLine2 = "Awaiting your command...";
        private const float BootDuration = 1.2f;
        private const float Pause1Duration = 0.5f;
        private const float FadeOutDuration = 0.8f;
        private const double RepaintInterval = 1.0 / 30.0;

        // Movie-style typing rhythm: fast irregular keystrokes; the trailing
        // ellipsis of line 2 lands one slow dot at a time.
        private const double CharDelayMin = 0.005;
        private const double CharDelayJitter = 0.05;
        private const double CharDelayAfterBreak = 0.03;
        private const double DotDelayMin = 0.32;
        private const double DotDelayJitter = 0.10;
        private const double CursorSolidWindow = 0.35;

        private static readonly Color Background = new Color(0.01f, 0.02f, 0.01f, 1f);
        private static readonly Color PanelColor = new Color(0f, 0.05f, 0f, 0.62f);
        private static readonly Color BorderColor = new Color(0.10f, 0.75f, 0.22f, 0.9f);

        private static readonly (string label, string menuPath)[] Entries =
        {
            ("MENU EDITOR", "Tools/Feeder/Feeder Menu Editor Window"),
            ("SCENES LOADER", "Tools/Feeder/Scenes Loader Window"),
            ("SCRIPT TEMPLATE", "Tools/Feeder/Script Template Window"),
            ("ASSET CACHE", "Tools/Feeder/Feeder Asset Cache"),
            ("BUILD SIZE DIFF", "Tools/Feeder/Feeder Build Size Diff"),
            ("TEXTURE MODIFIER", "Tools/Feeder/Feeder Texture Modifier"),
            ("MESH MODIFIER", "Tools/Feeder/Feeder Mesh Modifier"),
            ("STYLE MENU", "Tools/Feeder/Feeder Menu Style Window"),
        };

        private enum IntroPhase { Boot, Typing1, Pause1, Typing2, AwaitClick, FadeOut, Done }

        private MatrixRainRenderer rain;

        private GUIStyle buttonStyle;
        private GUIStyle introStyle;
        private GUIStyle headerStyle;
        private Texture2D texButtonNormal;
        private Texture2D texButtonHover;
        private GUIContent[] buttonContents;
        private GUIContent headerContent;
        private readonly GUIContent scratchContent = new GUIContent();

        private IntroPhase introPhase = IntroPhase.Done;
        private double phaseStart;
        private double nextCharTime;
        private double lastCharTime;
        private readonly System.Random introRng = new System.Random();
        private int visible1;
        private int visible2;
        private string typed1 = "";
        private string typed2 = "";
        private float typedWidth1;
        private float typedWidth2;
        private float introLineHeight;
        private float introBlockWidth;
        private float cursorWidth;
        private bool introMetricsReady;
        private float buttonsAlpha;

        private bool updateSubscribed;
        private double lastStepTime;
        private double nextRepaintTime;

        private void OnEnable()
        {
            bool played = SessionState.GetBool(IntroSessionKey, false);
            introPhase = played ? IntroPhase.Done : IntroPhase.Boot;
            buttonsAlpha = played ? 1f : 0f;
            phaseStart = EditorApplication.timeSinceStartup;
            nextCharTime = 0.0;
            lastCharTime = 0.0;
            Subscribe();
        }

        private void OnDisable()
        {
            Unsubscribe();
            if (rain != null)
            {
                rain.Dispose();
                rain = null;
            }
            DestroyStyleTextures();
            buttonStyle = null;
        }

        void IHasCustomMenu.AddItemsToMenu(GenericMenu menu)
        {
            menu.AddItem(new GUIContent("Reset Intro"), false, ResetIntro);
        }

        /// <summary>Replays the typewriter intro (also clears the session gate).</summary>
        private void ResetIntro()
        {
            SessionState.SetBool(IntroSessionKey, false);
            introPhase = IntroPhase.Boot;
            phaseStart = EditorApplication.timeSinceStartup;
            nextCharTime = 0.0;
            lastCharTime = 0.0;
            visible1 = 0;
            visible2 = 0;
            typed1 = "";
            typed2 = "";
            typedWidth1 = 0f;
            typedWidth2 = 0f;
            buttonsAlpha = 0f;
            Repaint();
        }

        private void OnBecameVisible() => Subscribe();

        private void OnBecameInvisible() => Unsubscribe();

        private void Subscribe()
        {
            if (updateSubscribed)
                return;
            EditorApplication.update += OnEditorUpdate;
            updateSubscribed = true;
            lastStepTime = EditorApplication.timeSinceStartup;
        }

        private void Unsubscribe()
        {
            if (!updateSubscribed)
                return;
            EditorApplication.update -= OnEditorUpdate;
            updateSubscribed = false;
        }

        private void OnEditorUpdate()
        {
            double now = EditorApplication.timeSinceStartup;
            if (now < nextRepaintTime)
                return;
            nextRepaintTime = now + RepaintInterval;
            Repaint();
        }

        private void OnGUI()
        {
            EnsureStyles();
            UpdateIntroFontSize();

            double now = EditorApplication.timeSinceStartup;
            Event e = Event.current;

            EditorGUI.DrawRect(new Rect(0f, 0f, position.width, position.height), Background);

            if (e.type == EventType.Repaint)
            {
                float dt = Mathf.Clamp((float)(now - lastStepTime), 0f, 0.1f);
                lastStepTime = now;
                rain.Step(dt);
                rain.Draw(position.width, position.height);
                UpdateIntro(now, dt);
            }

            if (e.type == EventType.MouseDown)
            {
                if (introPhase == IntroPhase.AwaitClick)
                {
                    // The confirming click: only now do the tools reveal.
                    SessionState.SetBool(IntroSessionKey, true);
                    AdvancePhase(IntroPhase.FadeOut, now);
                    e.Use();
                }
                else if (introPhase != IntroPhase.Done && introPhase != IntroPhase.FadeOut)
                {
                    // Click mid-typing fast-forwards the text, then waits for
                    // the confirming click like normal.
                    SetVisible1(IntroLine1.Length);
                    SetVisible2(IntroLine2.Length);
                    AdvancePhase(IntroPhase.AwaitClick, now);
                    e.Use();
                }
            }

            if (buttonsAlpha > 0.01f)
                DrawButtonGrid();

            if (introPhase != IntroPhase.Done && e.type == EventType.Repaint)
                DrawIntroOverlay(now);
        }

        // ---------------------------------------------------------------- intro

        private void UpdateIntro(double now, float dt)
        {
            switch (introPhase)
            {
                case IntroPhase.Boot:
                    // Blank screen, blinking cursor — like the opening shot.
                    if (now - phaseStart >= BootDuration)
                        AdvancePhase(IntroPhase.Typing1, now);
                    break;

                case IntroPhase.Typing1:
                    if (now >= nextCharTime)
                    {
                        SetVisible1(visible1 + 1);
                        lastCharTime = now;
                        if (visible1 >= IntroLine1.Length)
                            AdvancePhase(IntroPhase.Pause1, now);
                        else
                            nextCharTime = now + NextCharDelay(IntroLine1, visible1, slowTailDots: false);
                    }
                    break;

                case IntroPhase.Pause1:
                    if (now - phaseStart >= Pause1Duration)
                        AdvancePhase(IntroPhase.Typing2, now);
                    break;

                case IntroPhase.Typing2:
                    if (now >= nextCharTime)
                    {
                        SetVisible2(visible2 + 1);
                        lastCharTime = now;
                        if (visible2 >= IntroLine2.Length)
                            AdvancePhase(IntroPhase.AwaitClick, now);
                        else
                            nextCharTime = now + NextCharDelay(IntroLine2, visible2, slowTailDots: true);
                    }
                    break;

                case IntroPhase.AwaitClick:
                    break; // holds forever; only a click (handled in OnGUI) moves on

                case IntroPhase.FadeOut:
                    float t = Mathf.Clamp01((float)((now - phaseStart) / FadeOutDuration));
                    buttonsAlpha = t;
                    if (t >= 1f)
                        introPhase = IntroPhase.Done;
                    break;

                case IntroPhase.Done:
                    buttonsAlpha = Mathf.MoveTowards(buttonsAlpha, 1f, dt * 2f);
                    break;
            }
        }

        private void AdvancePhase(IntroPhase next, double now)
        {
            introPhase = next;
            phaseStart = now;
        }

        /// <summary>Delay before the character at <paramref name="nextIndex"/> appears.</summary>
        private double NextCharDelay(string line, int nextIndex, bool slowTailDots)
        {
            if (slowTailDots && nextIndex >= line.Length - 3)
                return DotDelayMin + introRng.NextDouble() * DotDelayJitter;

            double delay = CharDelayMin + introRng.NextDouble() * CharDelayJitter;
            char prev = line[nextIndex - 1];
            if (prev == ',' || prev == ' ')
                delay += CharDelayAfterBreak;
            return delay;
        }

        private void SetVisible1(int count)
        {
            if (count == visible1)
                return;
            visible1 = count;
            typed1 = IntroLine1.Substring(0, count);
            scratchContent.text = typed1;
            typedWidth1 = introStyle.CalcSize(scratchContent).x;
        }

        private void SetVisible2(int count)
        {
            if (count == visible2)
                return;
            visible2 = count;
            typed2 = IntroLine2.Substring(0, count);
            scratchContent.text = typed2;
            typedWidth2 = introStyle.CalcSize(scratchContent).x;
        }

        /// <summary>Scales the intro text with the window and re-derives cached metrics.</summary>
        private void UpdateIntroFontSize()
        {
            int size = Mathf.RoundToInt(Mathf.Clamp(position.width * 0.045f, 22f, 56f));
            if (introStyle.fontSize == size)
                return;
            introStyle.fontSize = size;
            introMetricsReady = false;
            scratchContent.text = typed1;
            typedWidth1 = introStyle.CalcSize(scratchContent).x;
            scratchContent.text = typed2;
            typedWidth2 = introStyle.CalcSize(scratchContent).x;
        }

        private void EnsureIntroMetrics()
        {
            if (introMetricsReady)
                return;
            scratchContent.text = IntroLine1;
            Vector2 size1 = introStyle.CalcSize(scratchContent);
            scratchContent.text = IntroLine2;
            Vector2 size2 = introStyle.CalcSize(scratchContent);
            scratchContent.text = "0";
            cursorWidth = Mathf.Max(6f, introStyle.CalcSize(scratchContent).x);
            introLineHeight = Mathf.Max(size1.y, size2.y);
            introBlockWidth = Mathf.Max(size1.x, size2.x);
            introMetricsReady = true;
        }

        private void DrawIntroOverlay(double now)
        {
            EnsureIntroMetrics();

            float alpha = introPhase == IntroPhase.FadeOut ? 1f - buttonsAlpha : 1f;
            if (alpha <= 0.01f)
                return;

            // Two-line block centered both ways; lines stay left-aligned within
            // the block so the text types out terminal-style, left to right.
            float x = Mathf.Max(20f, (position.width - introBlockWidth) * 0.5f);
            float y1 = position.height * 0.5f - introLineHeight * 1.35f;
            float y2 = y1 + introLineHeight * 1.7f;

            Color prev = GUI.color;
            GUI.color = new Color(1f, 1f, 1f, alpha);

            if (visible1 > 0)
            {
                scratchContent.text = typed1;
                GUI.Label(new Rect(x, y1, introBlockWidth + cursorWidth, introLineHeight), scratchContent, introStyle);
            }
            if (visible2 > 0)
            {
                scratchContent.text = typed2;
                GUI.Label(new Rect(x, y2, introBlockWidth + cursorWidth, introLineHeight), scratchContent, introStyle);
            }

            // Block cursor, movie behavior: solid while keystrokes are landing,
            // blinking ~1 Hz whenever the typing is idle (boot, pauses, slow dots).
            bool typingActive = now - lastCharTime < CursorSolidWindow;
            if (typingActive || Mathf.Repeat((float)now, 1f) < 0.55f)
            {
                bool onLine2 = introPhase >= IntroPhase.Typing2;
                float cursorX = x + (onLine2 ? typedWidth2 : typedWidth1) + 2f;
                float cursorY = onLine2 ? y2 : y1;
                Color c = MatrixRainRenderer.TrailColor;
                c.a *= alpha;
                EditorGUI.DrawRect(new Rect(cursorX, cursorY + 2f, cursorWidth, introLineHeight - 4f), c);
            }

            GUI.color = prev;
        }

        // -------------------------------------------------------------- buttons

        private void DrawButtonGrid()
        {
            float w = position.width;
            float h = position.height;

            const int cols = 2;
            const float btnH = 44f;
            const float gap = 10f;
            const float pad = 16f;
            const float headerH = 30f;

            int rows = Mathf.CeilToInt(Entries.Length / (float)cols);
            float panelW = Mathf.Min(560f, w * 0.72f);
            float panelH = pad * 2f + headerH + rows * btnH + (rows - 1) * gap;
            float panelX = (w - panelW) * 0.5f;
            float panelY = Mathf.Clamp((h - panelH) * 0.5f, 10f, Mathf.Max(10f, h - panelH - 10f));
            var panelRect = new Rect(panelX, panelY, panelW, panelH);

            if (Event.current.type == EventType.Repaint)
            {
                Color bg = PanelColor;
                bg.a *= buttonsAlpha;
                EditorGUI.DrawRect(panelRect, bg);

                Color border = BorderColor;
                border.a *= buttonsAlpha;
                EditorGUI.DrawRect(new Rect(panelRect.x, panelRect.y, panelRect.width, 1f), border);
                EditorGUI.DrawRect(new Rect(panelRect.x, panelRect.yMax - 1f, panelRect.width, 1f), border);
                EditorGUI.DrawRect(new Rect(panelRect.x, panelRect.y, 1f, panelRect.height), border);
                EditorGUI.DrawRect(new Rect(panelRect.xMax - 1f, panelRect.y, 1f, panelRect.height), border);
            }

            Color prev = GUI.color;
            GUI.color = new Color(1f, 1f, 1f, buttonsAlpha);

            GUI.Label(new Rect(panelX, panelY + pad * 0.5f, panelW, headerH), headerContent, headerStyle);

            float btnW = (panelW - pad * 2f - gap * (cols - 1)) / cols;
            float top = panelY + pad + headerH;
            for (int i = 0; i < Entries.Length; i++)
            {
                int row = i / cols;
                int colIdx = i % cols;
                var rect = new Rect(
                    panelX + pad + colIdx * (btnW + gap),
                    top + row * (btnH + gap),
                    btnW, btnH);

                if (GUI.Button(rect, buttonContents[i], buttonStyle))
                {
                    string path = Entries[i].menuPath;
                    EditorApplication.delayCall += () => EditorApplication.ExecuteMenuItem(path);
                }
            }

            GUI.color = prev;
        }

        // --------------------------------------------------------------- styles

        private void EnsureStyles()
        {
            if (rain == null)
                rain = new MatrixRainRenderer();
            if (buttonStyle != null)
                return;

            Font mono = rain.UIFont;

            texButtonNormal = MakeTex(new Color(0f, 0.12f, 0.03f, 0.85f));
            texButtonHover = MakeTex(new Color(0f, 0.35f, 0.08f, 0.95f));

            buttonStyle = new GUIStyle
            {
                font = mono,
                fontSize = 13,
                alignment = TextAnchor.MiddleCenter,
                normal = { textColor = MatrixRainRenderer.TrailColor, background = texButtonNormal },
                hover = { textColor = MatrixRainRenderer.HeadColor, background = texButtonHover },
                active = { textColor = Color.white, background = texButtonHover },
            };

            introStyle = new GUIStyle
            {
                font = mono,
                fontSize = 20,
                alignment = TextAnchor.UpperLeft,
                normal = { textColor = MatrixRainRenderer.TrailColor },
            };

            headerStyle = new GUIStyle
            {
                font = mono,
                fontSize = 12,
                alignment = TextAnchor.MiddleCenter,
                normal = { textColor = BorderColor },
            };

            headerContent = new GUIContent("FEEDER // TOOLKIT");
            buttonContents = new GUIContent[Entries.Length];
            for (int i = 0; i < Entries.Length; i++)
                buttonContents[i] = new GUIContent(Entries[i].label);

            introMetricsReady = false;
        }

        private static Texture2D MakeTex(Color color)
        {
            var tex = new Texture2D(1, 1, TextureFormat.RGBA32, false)
            {
                hideFlags = HideFlags.HideAndDontSave
            };
            tex.SetPixel(0, 0, color);
            tex.Apply();
            return tex;
        }

        private void DestroyStyleTextures()
        {
            if (texButtonNormal != null)
                DestroyImmediate(texButtonNormal);
            if (texButtonHover != null)
                DestroyImmediate(texButtonHover);
            texButtonNormal = null;
            texButtonHover = null;
        }
    }
}
