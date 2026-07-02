using System.Drawing;

namespace WordLinkSolver.Models;

/// <summary>
/// One tile of the 4×4 board: its letter, grid position and pixel geometry
/// (relative to the captured region / overlay canvas).
/// </summary>
public sealed class LetterTile
{
    public int Row { get; }
    public int Col { get; }

    /// <summary>Grid index 0..15 (row-major).</summary>
    public int Index => Row * 4 + Col;

    /// <summary>Recognized letter 'A'..'Z', or '\0' when OCR failed for this cell.</summary>
    public char Letter { get; set; }

    /// <summary>OCR confidence 0..1 (heuristic for WinRT OCR, which has no native confidence).</summary>
    public float OcrConfidence { get; set; }

    /// <summary>Bounding box of the tile in capture-region pixel coordinates.</summary>
    public RectangleF CellBounds { get; set; }

    /// <summary>Center of the tile in capture-region pixel coordinates (used to draw the path).</summary>
    public PointF PixelCenter => new(
        CellBounds.X + CellBounds.Width / 2f,
        CellBounds.Y + CellBounds.Height / 2f);

    /// <summary>Point value of this tile's letter.</summary>
    public int Points => Core.Scorer.LetterValue(Letter);

    /// <summary>True when the cell should be highlighted for manual verification.</summary>
    public bool IsUncertain => Letter == '\0' || OcrConfidence < 0.5f;

    public LetterTile(int row, int col)
    {
        Row = row;
        Col = col;
        Letter = '\0';
    }
}
