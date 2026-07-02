using System.Drawing;
using WordLinkSolver.Core;
using Xunit;

namespace WordLinkSolver.Tests;

public class GridDetectorTests
{
    private const int W = 480;
    private const int H = 480;

    /// <summary>Paints a synthetic WordLink-style board: cream tiles on dark teal.</summary>
    private static byte[] SyntheticBoard(
        out RectangleF[] expectedCells, char[]? glyphSeeds = null)
    {
        var bgra = new byte[W * H * 4];
        FillRect(bgra, new Rectangle(0, 0, W, H), b: 110, g: 95, r: 30); // dark teal

        expectedCells = new RectangleF[16];
        const int margin = 24;
        const int gap = 16;
        int tile = (W - 2 * margin - 3 * gap) / 4; // 96

        for (int row = 0; row < 4; row++)
        {
            for (int col = 0; col < 4; col++)
            {
                int x = margin + col * (tile + gap);
                int y = margin + row * (tile + gap);
                var rect = new Rectangle(x, y, tile, tile);
                expectedCells[row * 4 + col] = rect;
                FillRect(bgra, rect, b: 192, g: 207, r: 212); // cream tile #D4CFC0

                // A black "glyph" blob whose shape varies with the seed character.
                int seed = glyphSeeds is null ? 0 : glyphSeeds[row * 4 + col];
                int gw = 18 + (seed % 7) * 8;
                int gh = 34 + (seed % 3) * 8;
                FillRect(bgra, new Rectangle(x + (tile - gw) / 2, y + (tile - gh) / 2, gw, gh),
                    b: 10, g: 10, r: 10);
            }
        }
        return bgra;
    }

    private static void FillRect(byte[] bgra, Rectangle rect, byte b, byte g, byte r)
    {
        for (int y = rect.Y; y < rect.Bottom; y++)
        {
            for (int x = rect.X; x < rect.Right; x++)
            {
                int i = (y * W + x) * 4;
                bgra[i] = b;
                bgra[i + 1] = g;
                bgra[i + 2] = r;
                bgra[i + 3] = 255;
            }
        }
    }

    [Fact]
    public void DetectsAllSixteenTilesInRowMajorOrder()
    {
        var bgra = SyntheticBoard(out var expected);
        var result = GridDetector.Detect(bgra, W, H);

        Assert.NotNull(result);
        Assert.True(result!.Detected);
        Assert.Equal(16, result.Cells.Length);

        for (int i = 0; i < 16; i++)
        {
            var expectedCenter = new PointF(
                expected[i].X + expected[i].Width / 2f,
                expected[i].Y + expected[i].Height / 2f);
            var cell = result.Cells[i];
            var center = new PointF(cell.X + cell.Width / 2f, cell.Y + cell.Height / 2f);

            Assert.True(Math.Abs(center.X - expectedCenter.X) < expected[i].Width * 0.25f,
                $"cell {i}: center X {center.X} vs expected {expectedCenter.X}");
            Assert.True(Math.Abs(center.Y - expectedCenter.Y) < expected[i].Height * 0.25f,
                $"cell {i}: center Y {center.Y} vs expected {expectedCenter.Y}");
        }
    }

    [Fact]
    public void ReturnsNullWhenNoBoardIsVisible()
    {
        var bgra = new byte[W * H * 4]; // all black
        Assert.Null(GridDetector.Detect(bgra, W, H));
    }

    [Fact]
    public void ReturnsNullForTinyImages()
    {
        Assert.Null(GridDetector.Detect(new byte[30 * 30 * 4], 30, 30));
    }

    [Fact]
    public void UniformGridCoversTheRegionWithSixteenCells()
    {
        var grid = GridDetector.UniformGrid(400, 400);
        Assert.False(grid.Detected);
        Assert.Equal(16, grid.Cells.Length);
        Assert.All(grid.Cells, c => Assert.True(c.Width > 50 && c.Height > 50));

        // Row-major ordering.
        Assert.True(grid.Cells[0].X < grid.Cells[1].X);
        Assert.True(grid.Cells[0].Y < grid.Cells[4].Y);

        var union = grid.BoardBounds;
        Assert.True(union.Width > 300 && union.Height > 300);
    }

    [Fact]
    public void CellHashIsStableForIdenticalPixels()
    {
        var a = SyntheticBoard(out var cells, "ABCDEFGHIJKLMNOP".ToCharArray());
        var b = SyntheticBoard(out _, "ABCDEFGHIJKLMNOP".ToCharArray());

        for (int i = 0; i < 16; i++)
        {
            ulong ha = GridDetector.HashCell(a, W, H, cells[i]);
            ulong hb = GridDetector.HashCell(b, W, H, cells[i]);
            Assert.Equal(0, GridDetector.HashDistance(ha, hb));
        }
    }

    [Fact]
    public void CellHashChangesWhenAGlyphChanges()
    {
        var before = SyntheticBoard(out var cells, "AAAAAAAAAAAAAAAA".ToCharArray());
        var after = SyntheticBoard(out _, "ZAAAAAAAAAAAAAAA".ToCharArray());

        int changedDistance = GridDetector.HashDistance(
            GridDetector.HashCell(before, W, H, cells[0]),
            GridDetector.HashCell(after, W, H, cells[0]));
        int unchangedDistance = GridDetector.HashDistance(
            GridDetector.HashCell(before, W, H, cells[5]),
            GridDetector.HashCell(after, W, H, cells[5]));

        Assert.True(changedDistance > GridDetector.SameCellMaxDistance,
            $"expected a changed glyph to move the hash, distance={changedDistance}");
        Assert.True(unchangedDistance <= GridDetector.SameCellMaxDistance);
    }
}
