using UnityEditor;
using UnityEngine;

namespace Feeder
{
    /// <summary>
    /// Multi-layer Matrix digital rain with a continuous forward-dolly effect.
    /// All glyphs across all layers are drawn as textured quads from the dynamic
    /// font atlas in a single GL.QUADS batch (one draw call, zero steady-state GC).
    /// </summary>
    internal sealed class MatrixRainRenderer
    {
        private const int LayerCount = 4;
        private const float FontPointSize = 24f;
        private const float CellW = 15f;
        private const float CellH = 26f;

        // Layer local space spans window/ScaleRef, so a layer exactly covers the
        // window at scale = ScaleRef. Scale progresses exponentially with depth,
        // keeping the set of layer scales a fixed geometric progression, which
        // makes the near->far wrap statistically invisible.
        private const float ScaleMin = 0.4f;
        private const float ScaleMax = 1.3f;
        private const float ScaleRef = 0.6f;
        private const float DollySpeed = 0.5f;  // depth units per second
        private const float FadeInEnd = 0.25f;
        private const float FadeOutStart = 0.85f;
        private const float DimFar = 0.35f;

        private const int MinColumnsPerLayer = 8;
        private const int MaxColumnsPerLayer = 40;
        private const int MaxQuads = 3000;
        private const int MinTrail = 6;
        private const int MaxTrail = 28;
        private const float MinSpeed = 6f;        // local rows per second
        private const float MaxSpeed = 22f;
        private const float MutateChance = 0.02f;

        private const string ShaderPath = "Packages/com.feeder.editortools/Editor/1.Windows/FeederMatrixGlyph.shader";

        public static readonly Color HeadColor = new Color(0.78f, 1f, 0.82f, 1f);
        public static readonly Color TrailColor = new Color(0.13f, 0.94f, 0.27f, 1f);

        // Digits + half-width katakana, the classic Matrix glyph set.
        private const string Glyphs =
            "0123456789ｱｲｳｴｵｶｷｸｹｺｻｼｽｾｿﾀﾁﾂﾃﾄﾅﾆﾇﾈﾉﾊﾋﾌﾍﾎﾏﾐﾑﾒﾓﾔﾕﾖﾗﾘﾙﾚﾛﾜﾝ";

        private struct RainColumn
        {
            public float localX;
            public float headRow;
            public float speed;
            public int trail;
            public int glyphStart; // offset into glyphIndices, stride MaxTrail + 1
        }

        private struct RainLayer
        {
            public float depth; // 0 = farthest, ->1 = nearest
            public int columnStart;
            public int columnCount;
        }

        private RainLayer[] layers;
        private RainColumn[] columns;
        private byte[] glyphIndices;
        private CharacterInfo[] glyphInfo;
        private int[] layerOrder;
        private readonly System.Random rng = new System.Random();

        private Font font;
        private bool ownsFont;
        private Material material;
        private bool subscribedRebuilt;
        private bool glyphCacheDirty = true;
        private float cachedPpp = 1f;
        private float cachedW = -1f;
        private float cachedH = -1f;
        private float localWidth;
        private float localHeight;
        private int rowsLocal = 1;

        /// <summary>Monospace font shared with the window's UI styles.</summary>
        public Font UIFont
        {
            get
            {
                EnsureResources();
                return font;
            }
        }

        public bool EnsureResources()
        {
            if (font == null)
            {
                font = Font.CreateDynamicFontFromOSFont("Consolas", (int)FontPointSize);
                if (font == null)
                    font = Font.CreateDynamicFontFromOSFont("Courier New", (int)FontPointSize);
                ownsFont = font != null;
                if (ownsFont)
                    font.hideFlags = HideFlags.HideAndDontSave;
                else if (GUI.skin != null)
                    font = GUI.skin.font;
                glyphCacheDirty = true;
            }

            if (material == null)
            {
                Shader shader = AssetDatabase.LoadAssetAtPath<Shader>(ShaderPath);
                if (shader == null)
                    shader = Shader.Find("Hidden/Feeder/MatrixGlyph");
                if (shader == null)
                    shader = Shader.Find("GUI/Text Shader"); // degraded: single color, ignores GL.Color
                if (shader == null)
                    return false;

                material = new Material(shader) { hideFlags = HideFlags.HideAndDontSave };
                if (material.HasProperty("_Color"))
                    material.SetColor("_Color", TrailColor);
                glyphCacheDirty = true;
            }

            if (!subscribedRebuilt)
            {
                Font.textureRebuilt += OnFontTextureRebuilt;
                subscribedRebuilt = true;
            }

            return font != null;
        }

        public void Dispose()
        {
            if (subscribedRebuilt)
            {
                Font.textureRebuilt -= OnFontTextureRebuilt;
                subscribedRebuilt = false;
            }
            if (material != null)
                Object.DestroyImmediate(material);
            material = null;
            if (ownsFont && font != null)
                Object.DestroyImmediate(font);
            font = null;
        }

        private void OnFontTextureRebuilt(Font rebuilt)
        {
            if (rebuilt == font)
                glyphCacheDirty = true;
        }

        private void RebuildGlyphCache()
        {
            cachedPpp = EditorGUIUtility.pixelsPerPoint;
            int atlasSize = Mathf.Max(8, Mathf.RoundToInt(FontPointSize * cachedPpp));

            if (glyphInfo == null)
                glyphInfo = new CharacterInfo[Glyphs.Length];

            font.RequestCharactersInTexture(Glyphs, atlasSize, FontStyle.Normal);
            for (int i = 0; i < Glyphs.Length; i++)
                font.GetCharacterInfo(Glyphs[i], out glyphInfo[i], atlasSize, FontStyle.Normal);

            // The atlas texture object can be replaced on rebuild; never cache it independently.
            if (font.material != null)
                material.mainTexture = font.material.mainTexture;

            glyphCacheDirty = false;
        }

        private void Rebuild(float width, float height)
        {
            cachedW = width;
            cachedH = height;
            localWidth = width / ScaleRef;
            localHeight = height / ScaleRef;
            rowsLocal = Mathf.Max(1, Mathf.CeilToInt(localHeight / CellH));

            int colCount = Mathf.Clamp(
                Mathf.FloorToInt(localWidth / (CellW * 2f)),
                MinColumnsPerLayer, MaxColumnsPerLayer);

            bool fresh = layers == null;
            if (fresh)
            {
                layers = new RainLayer[LayerCount];
                layerOrder = new int[LayerCount];
            }

            int total = LayerCount * colCount;
            if (columns == null || columns.Length != total)
            {
                columns = new RainColumn[total];
                glyphIndices = new byte[total * (MaxTrail + 1)];
            }

            float spacing = localWidth / colCount;
            for (int l = 0; l < LayerCount; l++)
            {
                if (fresh)
                    layers[l].depth = l / (float)LayerCount;
                layers[l].columnStart = l * colCount;
                layers[l].columnCount = colCount;

                for (int c = 0; c < colCount; c++)
                {
                    int idx = layers[l].columnStart + c;
                    columns[idx].glyphStart = idx * (MaxTrail + 1);
                    float jitter = ((float)rng.NextDouble() - 0.5f) * 0.6f;
                    columns[idx].localX = -localWidth * 0.5f + (c + 0.5f + jitter) * spacing;
                    SeedColumn(ref columns[idx], scatter: true);
                }
            }
        }

        private void SeedColumn(ref RainColumn col, bool scatter)
        {
            col.speed = Mathf.Lerp(MinSpeed, MaxSpeed, (float)rng.NextDouble());
            col.trail = MinTrail + rng.Next(MaxTrail - MinTrail + 1);
            col.headRow = scatter
                ? (float)rng.NextDouble() * rowsLocal * 2f - rowsLocal * 0.5f
                : -col.trail - (float)rng.NextDouble() * 8f;
            for (int k = 0; k <= MaxTrail; k++)
                glyphIndices[col.glyphStart + k] = (byte)rng.Next(Glyphs.Length);
        }

        public void Step(float dt)
        {
            if (layers == null)
                return;

            for (int l = 0; l < LayerCount; l++)
            {
                layers[l].depth += DollySpeed * dt;
                if (layers[l].depth >= 1f)
                {
                    // Layer flew past the camera: wrap to the far plane (it is
                    // fully faded out on both sides of the wrap) and reseed.
                    layers[l].depth -= 1f;
                    for (int c = 0; c < layers[l].columnCount; c++)
                        SeedColumn(ref columns[layers[l].columnStart + c], scatter: true);
                }
            }

            for (int i = 0; i < columns.Length; i++)
            {
                ref RainColumn col = ref columns[i];
                col.headRow += col.speed * dt;

                int gs = col.glyphStart;
                for (int k = 0; k <= col.trail; k++)
                {
                    if (rng.NextDouble() < MutateChance)
                        glyphIndices[gs + k] = (byte)rng.Next(Glyphs.Length);
                }

                if (col.headRow - col.trail > rowsLocal + 4)
                    SeedColumn(ref col, scatter: false);
            }
        }

        public void Draw(float width, float height)
        {
            if (Event.current.type != EventType.Repaint)
                return;
            if (!EnsureResources() || material == null)
                return;

            if (Mathf.Abs(width - cachedW) > 1f || Mathf.Abs(height - cachedH) > 1f)
                Rebuild(width, height);
            if (glyphCacheDirty || !Mathf.Approximately(cachedPpp, EditorGUIUtility.pixelsPerPoint))
                RebuildGlyphCache();

            // The GUI matrix already maps points (ppp included), origin at the
            // window content top-left. GL.LoadPixelMatrix would be wrong here.
            GUI.BeginClip(new Rect(0f, 0f, width, height));
            GL.PushMatrix();
            material.SetPass(0);
            GL.Begin(GL.QUADS);
            EmitQuads(width, height);
            GL.End();
            GL.PopMatrix();
            GUI.EndClip();
        }

        private static float Smooth01(float x)
        {
            x = Mathf.Clamp01(x);
            return x * x * (3f - 2f * x);
        }

        private void EmitQuads(float width, float height)
        {
            // Far-to-near for correct alpha layering (4-element insertion sort).
            for (int i = 0; i < LayerCount; i++)
                layerOrder[i] = i;
            for (int i = 1; i < LayerCount; i++)
            {
                int key = layerOrder[i];
                float d = layers[key].depth;
                int j = i - 1;
                while (j >= 0 && layers[layerOrder[j]].depth > d)
                {
                    layerOrder[j + 1] = layerOrder[j];
                    j--;
                }
                layerOrder[j + 1] = key;
            }

            float cx = width * 0.5f;
            float cy = height * 0.5f;
            float invPpp = 1f / cachedPpp; // CharacterInfo metrics are in atlas pixels
            float lnScale = Mathf.Log(ScaleMax / ScaleMin);
            float halfLocalH = localHeight * 0.5f;
            int quads = 0;

            for (int li = 0; li < LayerCount; li++)
            {
                RainLayer layer = layers[layerOrder[li]];
                float depth = layer.depth;
                float layerAlpha = Smooth01(depth / FadeInEnd)
                                 * (1f - Smooth01((depth - FadeOutStart) / (1f - FadeOutStart)));
                if (layerAlpha <= 0.01f)
                    continue;

                float s = ScaleMin * Mathf.Exp(lnScale * depth);
                float bright = Mathf.Lerp(DimFar, 1f, depth);
                float cellW = CellW * s;
                float cellH = CellH * s;
                float k = invPpp * s;
                Color head = new Color(HeadColor.r, HeadColor.g, HeadColor.b, layerAlpha);
                float tr = TrailColor.r * bright;
                float tg = TrailColor.g * bright;
                float tb = TrailColor.b * bright;

                for (int c = 0; c < layer.columnCount; c++)
                {
                    ref RainColumn col = ref columns[layer.columnStart + c];
                    float sx = cx + col.localX * s;
                    if (sx + cellW < 0f || sx > width)
                        continue;

                    int headRowInt = Mathf.FloorToInt(col.headRow);
                    for (int t = 0; t <= col.trail; t++)
                    {
                        int row = headRowInt - t;
                        if (row < 0 || row >= rowsLocal)
                            continue;

                        // GUIClip does not scissor custom-shader GL geometry:
                        // cull in code, including y < 0 (the tab strip area).
                        float sy = cy + (row * CellH - halfLocalH) * s;
                        if (sy < 0f || sy + cellH > height)
                            continue;

                        if (quads >= MaxQuads)
                            return; // Draw() still runs GL.End/PopMatrix after us

                        CharacterInfo ci = glyphInfo[glyphIndices[col.glyphStart + t]];
                        float gw = (ci.maxX - ci.minX) * k;
                        float gh = (ci.maxY - ci.minY) * k;
                        if (gw <= 0f || gh <= 0f)
                            continue;

                        float gx = sx + (cellW - gw) * 0.5f;
                        float baseline = sy + cellH * 0.8f;
                        float gy = baseline - ci.maxY * k; // GUI space is y-down

                        if (t == 0)
                        {
                            GL.Color(head);
                        }
                        else
                        {
                            float fade = 1f - (float)t / col.trail;
                            GL.Color(new Color(tr, tg, tb, fade * fade * layerAlpha));
                        }

                        // Use all four UV corners: glyphs can be rotated in the atlas.
                        GL.TexCoord2(ci.uvTopLeft.x, ci.uvTopLeft.y);
                        GL.Vertex3(gx, gy, 0f);
                        GL.TexCoord2(ci.uvTopRight.x, ci.uvTopRight.y);
                        GL.Vertex3(gx + gw, gy, 0f);
                        GL.TexCoord2(ci.uvBottomRight.x, ci.uvBottomRight.y);
                        GL.Vertex3(gx + gw, gy + gh, 0f);
                        GL.TexCoord2(ci.uvBottomLeft.x, ci.uvBottomLeft.y);
                        GL.Vertex3(gx, gy + gh, 0f);
                        quads++;
                    }
                }
            }
        }
    }
}
