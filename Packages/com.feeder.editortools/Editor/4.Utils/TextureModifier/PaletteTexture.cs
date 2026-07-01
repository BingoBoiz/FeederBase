#if UNITY_EDITOR
using System.Collections.Generic;
using System.IO;
using UnityEngine;

namespace Feeder
{
    /// <summary>
    /// Data model for a grid-based palette texture. Each cell represents one solid color, and free
    /// space is identified by transparent or white texels. The class can load image pixels, detect
    /// the cell size, find matching color cells, allocate free cells, and write new cell colors.
    /// </summary>
    public class PaletteTexture
    {
        private const byte AlphaOccupied = 8;
        private const byte WhiteThreshold = 248;

        private static bool IsEmptyTexel(Color32 c)
        {
            if (c.a <= AlphaOccupied) return true;
            return c.r >= WhiteThreshold && c.g >= WhiteThreshold && c.b >= WhiteThreshold;
        }

        private static bool IsTransparentTexel(Color32 c) => c.a <= AlphaOccupied;

        public Color32[] Pixels { get; private set; }
        public int Width { get; private set; }
        public int Height { get; private set; }
        public int CellSize { get; set; }

        public int Cols => CellSize > 0 ? Width / CellSize : 0;
        public int Rows => CellSize > 0 ? Height / CellSize : 0;

        private PaletteTexture() { }

        /// <summary>Loads a PNG or JPG from disk without depending on the imported asset readability flag.</summary>
        public static PaletteTexture LoadFromPath(string path, out string error)
        {
            error = null;
            if (string.IsNullOrEmpty(path) || !File.Exists(path))
            {
                error = "Texture file was not found on disk.";
                return null;
            }

            byte[] bytes = File.ReadAllBytes(path);
            var tmp = new Texture2D(2, 2, TextureFormat.RGBA32, false);
            if (!tmp.LoadImage(bytes))
            {
                Object.DestroyImmediate(tmp);
                error = "Image data could not be read. Only PNG and JPG textures are supported.";
                return null;
            }

            var pal = new PaletteTexture
            {
                Width = tmp.width,
                Height = tmp.height,
                Pixels = tmp.GetPixels32(),
            };
            Object.DestroyImmediate(tmp);

            pal.CellSize = pal.DetectCellSize();
            return pal;
        }

        // =====================================================================
        // Cell size detection
        // =====================================================================
        /// <summary>
        /// Detects cell pitch by finding color-change boundaries on both axes, clustering adjacent
        /// boundaries, then using the most common distance between boundary centers.
        /// </summary>
        public int DetectCellSize()
        {
            int pitchX = DetectPitch(horizontal: true);
            int pitchY = DetectPitch(horizontal: false);

            int pitch;
            if (pitchX > 0 && pitchY > 0) pitch = Mathf.Min(pitchX, pitchY);
            else pitch = Mathf.Max(pitchX, pitchY);

            if (pitch <= 0)
                pitch = Mathf.Max(4, Mathf.Min(Width, Height) / 16);

            int max = Mathf.Max(4, Mathf.Min(Width, Height) / 4);
            return Mathf.Clamp(pitch, 4, max);
        }

        private int DetectPitch(bool horizontal)
        {
            int axisLen = horizontal ? Width : Height;
            int crossLen = horizontal ? Height : Width;
            const int diffThreshold = 24;
            int step = Mathf.Max(1, crossLen / 256);
            int samples = crossLen / step;
            int minHits = Mathf.Max(4, samples / 40);

            var boundaries = new List<int>();
            for (int a = 1; a < axisLen; a++)
            {
                int hits = 0;
                for (int b = 0; b < crossLen; b += step)
                {
                    Color32 c0 = horizontal ? Get(a, b) : Get(b, a);
                    Color32 c1 = horizontal ? Get(a - 1, b) : Get(b, a - 1);
                    if (c0.a <= AlphaOccupied && c1.a <= AlphaOccupied) continue;
                    int d = Mathf.Abs(c0.r - c1.r) + Mathf.Abs(c0.g - c1.g) + Mathf.Abs(c0.b - c1.b);
                    if (d > diffThreshold) hits++;
                }
                if (hits > minHits) boundaries.Add(a);
            }

            var centers = ClusterAdjacent(boundaries, 3);
            if (centers.Count < 2) return 0;

            var hist = new Dictionary<int, int>();
            int bestGap = 0, bestCount = 0;
            for (int i = 1; i < centers.Count; i++)
            {
                int gap = centers[i] - centers[i - 1];
                if (gap < 4) continue;
                hist.TryGetValue(gap, out int n);
                n++; hist[gap] = n;
                if (n > bestCount) { bestCount = n; bestGap = gap; }
            }
            return bestGap;
        }

        /// <summary>Clusters adjacent positions into a single center.</summary>
        private static List<int> ClusterAdjacent(List<int> sortedPos, int mergeDist)
        {
            var centers = new List<int>();
            int i = 0;
            while (i < sortedPos.Count)
            {
                int j = i; long sum = sortedPos[i]; int count = 1;
                while (j + 1 < sortedPos.Count && sortedPos[j + 1] - sortedPos[j] <= mergeDist)
                {
                    j++; sum += sortedPos[j]; count++;
                }
                centers.Add(Mathf.RoundToInt((float)sum / count));
                i = j + 1;
            }
            return centers;
        }

        // =====================================================================
        // Cell queries and allocation
        // =====================================================================
        public Color32 Get(int x, int y) => Pixels[y * Width + x];

        /// <summary>Representative color sampled from the center of a cell.</summary>
        public Color32 CellRepresentativeColor(int cx, int cy)
        {
            int x = Mathf.Min(cx * CellSize + CellSize / 2, Width - 1);
            int y = Mathf.Min(cy * CellSize + CellSize / 2, Height - 1);
            return Get(x, y);
        }

        /// <summary>UV coordinate at the center of a cell.</summary>
        public Vector2 CellCenterUV(int cx, int cy)
        {
            return new Vector2(
                (cx * CellSize + CellSize * 0.5f) / Width,
                (cy * CellSize + CellSize * 0.5f) / Height);
        }

        /// <summary>Returns true when every texel in a cell is transparent or white.</summary>
        public bool CellIsEmpty(Color32[] px, int cx, int cy)
        {
            int x0 = cx * CellSize, y0 = cy * CellSize;
            for (int y = y0; y < y0 + CellSize; y++)
            {
                int row = y * Width;
                for (int x = x0; x < x0 + CellSize; x++)
                    if (!IsEmptyTexel(px[row + x])) return false;
            }
            return true;
        }

        /// <summary>Returns true when every texel in a cell is transparent.</summary>
        public bool CellIsTransparent(Color32[] px, int cx, int cy)
        {
            int x0 = cx * CellSize, y0 = cy * CellSize;
            for (int y = y0; y < y0 + CellSize; y++)
            {
                int row = y * Width;
                for (int x = x0; x < x0 + CellSize; x++)
                    if (!IsTransparentTexel(px[row + x])) return false;
            }
            return true;
        }

        /// <summary>Finds an existing color cell using the original palette pixels.</summary>
        public bool TryFindColorCell(Color32 c, int tol, out int cx, out int cy)
        {
            for (int y = 0; y < Rows; y++)
                for (int x = 0; x < Cols; x++)
                {
                    Color32 rep = CellRepresentativeColor(x, y);
                    if (IsEmptyTexel(rep)) continue;
                    if (FTextureModifierUtils.ColorsClose(rep, c, tol)) { cx = x; cy = y; return true; }
                }
            cx = cy = 0;
            return false;
        }

        /// <summary>
        /// Finds the first unclaimed free cell. Transparent cells are preferred, then solid white cells.
        /// </summary>
        public bool TryAllocateFreeCell(Color32[] px, bool[] claimed, out int cx, out int cy)
        {
            for (int y = 0; y < Rows; y++)
                for (int x = 0; x < Cols; x++)
                {
                    int idx = y * Cols + x;
                    if (claimed != null && claimed[idx]) continue;
                    if (CellIsTransparent(px, x, y)) { cx = x; cy = y; return true; }
                }

            for (int y = 0; y < Rows; y++)
                for (int x = 0; x < Cols; x++)
                {
                    int idx = y * Cols + x;
                    if (claimed != null && claimed[idx]) continue;
                    if (CellIsEmpty(px, x, y)) { cx = x; cy = y; return true; }
                }
            cx = cy = 0;
            return false;
        }

        /// <summary>Fills a palette cell with an opaque color.</summary>
        public void WriteCell(Color32[] px, int cx, int cy, Color32 c)
        {
            c.a = 255;
            int x0 = cx * CellSize, y0 = cy * CellSize;
            for (int y = y0; y < y0 + CellSize; y++)
            {
                int row = y * Width;
                for (int x = x0; x < x0 + CellSize; x++)
                    px[row + x] = c;
            }
        }

        /// <summary>Cell rectangle in source texture pixel coordinates.</summary>
        public Rect CellRect(int cx, int cy)
        {
            return new Rect(cx * CellSize, cy * CellSize, CellSize, CellSize);
        }
    }
}
#endif
