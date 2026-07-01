#if UNITY_EDITOR
using System;
using System.Collections.Generic;
using UnityEngine;

namespace Feeder
{
    /// <summary>Palette pixels and mesh UV targets produced from the current color edits.</summary>
    public class PaletteColorBuildResult
    {
        public Color32[] Pixels { get; }
        public Dictionary<UsedColor, Vector2> SwatchUV { get; }
        public List<SwatchMarker> Markers { get; }
        public int UnplacedCount { get; }

        public PaletteColorBuildResult(
            Color32[] pixels,
            Dictionary<UsedColor, Vector2> swatchUV,
            List<SwatchMarker> markers,
            int unplacedCount)
        {
            Pixels = pixels;
            SwatchUV = swatchUV;
            Markers = markers;
            UnplacedCount = unplacedCount;
        }

        public SwatchMarker MarkerForRow(int rowIndex)
        {
            for (int i = 0; i < Markers.Count; i++)
                if (Markers[i].number - 1 == rowIndex)
                    return Markers[i];
            return null;
        }
    }

    /// <summary>Builds result palette pixels and target UVs for changed mesh palette colors.</summary>
    public static class PaletteColorChangeBuilder
    {
        private struct AllocatedCell
        {
            public Color32 color;
            public Vector2 uv;
            public Rect rect;
        }

        public static bool HasChanges(List<UsedColor> usedColors)
        {
            if (usedColors == null) return false;

            for (int i = 0; i < usedColors.Count; i++)
                if (usedColors[i].Changed)
                    return true;

            return false;
        }

        public static PaletteColorBuildResult Build(PaletteTexture palette, List<UsedColor> usedColors, int colorTolerance)
        {
            if (palette == null)
                throw new ArgumentNullException(nameof(palette));

            var src = palette.Pixels;
            var px = new Color32[src.Length];
            Array.Copy(src, px, px.Length);

            var swatchUV = new Dictionary<UsedColor, Vector2>();
            var markers = new List<SwatchMarker>();
            int unplaced = 0;

            int cols = palette.Cols;
            var claimed = new bool[Mathf.Max(1, cols * palette.Rows)];
            var newCells = new List<AllocatedCell>();

            if (usedColors == null)
                return new PaletteColorBuildResult(px, swatchUV, markers, unplaced);

            for (int u = 0; u < usedColors.Count; u++)
            {
                var uc = usedColors[u];
                if (!uc.Changed) continue;

                int number = u + 1;
                Color32 target = FTextureModifierUtils.ToColor32(uc.newColor, 255);

                bool matchedNew = false;
                for (int k = 0; k < newCells.Count; k++)
                {
                    if (!FTextureModifierUtils.ColorsClose(newCells[k].color, target, colorTolerance))
                        continue;

                    swatchUV[uc] = newCells[k].uv;
                    markers.Add(new SwatchMarker
                    {
                        number = number,
                        kind = SwatchKind.New,
                        cellRect = newCells[k].rect,
                        newColor = uc.newColor
                    });
                    matchedNew = true;
                    break;
                }
                if (matchedNew) continue;

                if (palette.TryFindColorCell(target, colorTolerance, out int fx, out int fy))
                {
                    swatchUV[uc] = palette.CellCenterUV(fx, fy);
                    markers.Add(new SwatchMarker
                    {
                        number = number,
                        kind = SwatchKind.Reused,
                        cellRect = palette.CellRect(fx, fy),
                        newColor = uc.newColor
                    });
                    continue;
                }

                if (palette.TryAllocateFreeCell(px, claimed, out int sx, out int sy))
                {
                    palette.WriteCell(px, sx, sy, target);
                    if (cols > 0)
                        claimed[sy * cols + sx] = true;

                    Vector2 uv = palette.CellCenterUV(sx, sy);
                    Rect rect = palette.CellRect(sx, sy);
                    swatchUV[uc] = uv;
                    markers.Add(new SwatchMarker
                    {
                        number = number,
                        kind = SwatchKind.New,
                        cellRect = rect,
                        newColor = uc.newColor
                    });
                    newCells.Add(new AllocatedCell { color = target, uv = uv, rect = rect });
                }
                else
                {
                    markers.Add(new SwatchMarker
                    {
                        number = number,
                        kind = SwatchKind.Unplaced,
                        newColor = uc.newColor
                    });
                    unplaced++;
                }
            }

            return new PaletteColorBuildResult(px, swatchUV, markers, unplaced);
        }
    }
}
#endif
