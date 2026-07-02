using System.Drawing;

namespace WordLinkSolver.Core;

/// <summary>
/// Locates the 4×4 tile grid inside a captured BGRA frame.
///
/// Strategy (all pure pixel math so it is unit-testable off-Windows):
///  1. Downscale for speed, then threshold for the light tile color
///     (cream/gray tiles like #D4CFC0 on a dark teal background).
///  2. Connected-component labeling over the mask.
///  3. Filter components by size, squareness and fill ratio.
///  4. Cluster the 16 survivors into 4 rows × 4 columns.
///
/// When detection fails the caller falls back to <see cref="UniformGrid"/>
/// (an even 4×4 split of the region) or keeps the previously cached grid.
/// </summary>
public static class GridDetector
{
    public sealed class Result
    {
        /// <summary>16 tile bounding boxes, row-major, in source-image pixel coordinates.</summary>
        public required RectangleF[] Cells { get; init; }

        /// <summary>True when the grid was found by detection (false for the uniform fallback).</summary>
        public required bool Detected { get; init; }

        /// <summary>Union of all cells — the board area.</summary>
        public RectangleF BoardBounds
        {
            get
            {
                var b = Cells[0];
                foreach (var c in Cells)
                    b = RectangleF.Union(b, c);
                return b;
            }
        }
    }

    private const int TargetScaleSize = 400;
    private const int BrightnessThreshold = 140;
    private const int MaxChannelSpread = 90;

    /// <summary>True when a BGRA pixel looks like tile surface (light, low saturation).</summary>
    private static bool IsTilePixel(byte b, byte g, byte r)
    {
        int max = Math.Max(r, Math.Max(g, b));
        int min = Math.Min(r, Math.Min(g, b));
        int brightness = (r + g + b) / 3;
        return brightness >= BrightnessThreshold && (max - min) <= MaxChannelSpread;
    }

    /// <summary>
    /// Attempts to detect the 4×4 grid. Returns null when no plausible grid is found.
    /// </summary>
    /// <param name="bgra">Pixel data, 4 bytes per pixel (B,G,R,A), tightly packed.</param>
    public static Result? Detect(byte[] bgra, int width, int height)
    {
        if (width < 40 || height < 40 || bgra.Length < width * height * 4)
            return null;

        // --- 1. Downscale (nearest neighbor) + threshold ---
        int scale = Math.Max(1, Math.Max(width, height) / TargetScaleSize);
        int w = width / scale, h = height / scale;
        if (w < 20 || h < 20)
            return null;

        var mask = new bool[w * h];
        for (int y = 0; y < h; y++)
        {
            int srcRow = y * scale * width;
            for (int x = 0; x < w; x++)
            {
                int si = (srcRow + x * scale) * 4;
                mask[y * w + x] = IsTilePixel(bgra[si], bgra[si + 1], bgra[si + 2]);
            }
        }

        // --- 2. Connected components (two-pass union-find) ---
        var labels = new int[w * h];
        var parent = new List<int> { 0 }; // parent[0] unused; labels start at 1

        int Find(int a)
        {
            while (parent[a] != a)
            {
                parent[a] = parent[parent[a]];
                a = parent[a];
            }
            return a;
        }

        void Union(int a, int b)
        {
            a = Find(a);
            b = Find(b);
            if (a != b)
                parent[Math.Max(a, b)] = Math.Min(a, b);
        }

        for (int y = 0; y < h; y++)
        {
            for (int x = 0; x < w; x++)
            {
                int i = y * w + x;
                if (!mask[i])
                    continue;
                int left = x > 0 && mask[i - 1] ? labels[i - 1] : 0;
                int up = y > 0 && mask[i - w] ? labels[i - w] : 0;
                if (left == 0 && up == 0)
                {
                    parent.Add(parent.Count);
                    labels[i] = parent.Count - 1;
                }
                else if (left != 0 && up != 0)
                {
                    labels[i] = Math.Min(left, up);
                    Union(left, up);
                }
                else
                {
                    labels[i] = Math.Max(left, up);
                }
            }
        }

        // --- Component stats ---
        var stats = new Dictionary<int, (int Area, int MinX, int MinY, int MaxX, int MaxY)>();
        for (int y = 0; y < h; y++)
        {
            for (int x = 0; x < w; x++)
            {
                int l = labels[y * w + x];
                if (l == 0)
                    continue;
                int root = Find(l);
                if (stats.TryGetValue(root, out var s))
                {
                    stats[root] = (s.Area + 1, Math.Min(s.MinX, x), Math.Min(s.MinY, y),
                                   Math.Max(s.MaxX, x), Math.Max(s.MaxY, y));
                }
                else
                {
                    stats[root] = (1, x, y, x, y);
                }
            }
        }

        // --- 3. Filter to tile-like components ---
        int minDim = Math.Max(6, Math.Min(w, h) / 16);
        var tiles = new List<(float Cx, float Cy, RectangleF Box, int Area)>();
        foreach (var s in stats.Values)
        {
            int bw = s.MaxX - s.MinX + 1;
            int bh = s.MaxY - s.MinY + 1;
            if (bw < minDim || bh < minDim)
                continue;
            float aspect = bw / (float)bh;
            if (aspect < 0.62f || aspect > 1.62f)
                continue;
            float fill = s.Area / (float)(bw * bh);
            if (fill < 0.5f)
                continue; // tiles are solid rounded rects (letters punch small holes)
            tiles.Add((
                s.MinX + bw / 2f,
                s.MinY + bh / 2f,
                new RectangleF(s.MinX, s.MinY, bw, bh),
                s.Area));
        }

        if (tiles.Count < 16)
            return null;

        // Keep components close to the median area of the 16 largest.
        tiles.Sort((a, b) => b.Area.CompareTo(a.Area));
        int median = tiles[Math.Min(7, tiles.Count - 1)].Area;
        tiles = tiles.Where(t => t.Area >= median * 0.4f && t.Area <= median * 2.4f).ToList();
        if (tiles.Count < 16)
            return null;

        // --- 4. Cluster into 4 rows of 4 ---
        tiles.Sort((a, b) => a.Cy.CompareTo(b.Cy));
        float typicalH = tiles[tiles.Count / 2].Box.Height;
        var rows = new List<List<(float Cx, float Cy, RectangleF Box, int Area)>>();
        foreach (var t in tiles)
        {
            if (rows.Count > 0 && Math.Abs(t.Cy - rows[^1].Average(x => x.Cy)) < typicalH * 0.55f)
                rows[^1].Add(t);
            else
                rows.Add(new List<(float, float, RectangleF, int)> { t });
        }

        var fullRows = rows.Where(r => r.Count >= 4).ToList();
        if (fullRows.Count < 4)
            return null;
        if (fullRows.Count > 4)
        {
            // Prefer the 4 consecutive rows with the most consistent vertical spacing.
            fullRows = fullRows
                .OrderBy(r => r.Average(t => t.Cy))
                .Take(4)
                .ToList();
        }

        var cells = new RectangleF[16];
        for (int r = 0; r < 4; r++)
        {
            var row = fullRows[r].OrderBy(t => t.Cx).ToList();
            if (row.Count > 4)
            {
                // Drop outliers: keep the 4 with the most even horizontal spacing
                // (largest components win when ambiguous).
                row = row.OrderByDescending(t => t.Area).Take(4).OrderBy(t => t.Cx).ToList();
            }
            for (int c = 0; c < 4; c++)
            {
                var box = row[c].Box;
                cells[r * 4 + c] = new RectangleF(
                    box.X * scale, box.Y * scale, box.Width * scale, box.Height * scale);
            }
        }

        return new Result { Cells = cells, Detected = true };
    }

    /// <summary>
    /// Fallback grid: splits the region evenly into 4×4 cells with a small
    /// outer margin and per-cell inset, for when auto-detection fails but the
    /// user has framed the board inside the window.
    /// </summary>
    public static Result UniformGrid(int width, int height, float outerMarginFraction = 0.04f)
    {
        float mx = width * outerMarginFraction;
        float my = height * outerMarginFraction;
        float cw = (width - 2 * mx) / 4f;
        float ch = (height - 2 * my) / 4f;
        float inset = 0.07f;

        var cells = new RectangleF[16];
        for (int r = 0; r < 4; r++)
        {
            for (int c = 0; c < 4; c++)
            {
                cells[r * 4 + c] = new RectangleF(
                    mx + c * cw + cw * inset,
                    my + r * ch + ch * inset,
                    cw * (1 - 2 * inset),
                    ch * (1 - 2 * inset));
            }
        }
        return new Result { Cells = cells, Detected = false };
    }

    // ------------------------------------------------------------------
    // Cell change detection (difference hashing)
    // ------------------------------------------------------------------

    private const int HashGrid = 8;

    /// <summary>
    /// 64-bit difference hash of a cell region: resample to 9×8 grayscale,
    /// each bit compares horizontal neighbors. Robust to small shifts and
    /// lighting, sensitive to a different glyph.
    /// </summary>
    public static ulong HashCell(byte[] bgra, int width, int height, RectangleF cell)
    {
        Span<float> gray = stackalloc float[(HashGrid + 1) * HashGrid];

        for (int gy = 0; gy < HashGrid; gy++)
        {
            for (int gx = 0; gx <= HashGrid; gx++)
            {
                float fx = cell.X + cell.Width * (gx + 0.5f) / (HashGrid + 1);
                float fy = cell.Y + cell.Height * (gy + 0.5f) / HashGrid;
                int x = Math.Clamp((int)fx, 0, width - 1);
                int y = Math.Clamp((int)fy, 0, height - 1);
                int i = (y * width + x) * 4;
                gray[gy * (HashGrid + 1) + gx] = (bgra[i] + bgra[i + 1] + bgra[i + 2]) / 3f;
            }
        }

        ulong hash = 0;
        int bit = 0;
        for (int gy = 0; gy < HashGrid; gy++)
        {
            for (int gx = 0; gx < HashGrid; gx++)
            {
                if (gray[gy * (HashGrid + 1) + gx] < gray[gy * (HashGrid + 1) + gx + 1])
                    hash |= 1UL << bit;
                bit++;
            }
        }
        return hash;
    }

    /// <summary>Hamming distance between two cell hashes.</summary>
    public static int HashDistance(ulong a, ulong b) =>
        System.Numerics.BitOperations.PopCount(a ^ b);

    /// <summary>Distance at or below which two cell images are considered unchanged.</summary>
    public const int SameCellMaxDistance = 5;
}
