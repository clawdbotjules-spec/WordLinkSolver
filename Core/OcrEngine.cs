using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using System.Runtime.InteropServices.WindowsRuntime;
using System.Runtime.Versioning;
using Windows.Graphics.Imaging;
using WinOcrEngine = Windows.Media.Ocr.OcrEngine;

namespace WordLinkSolver.Core;

/// <summary>Result for one OCR'd cell.</summary>
public readonly record struct OcrCellResult(char Letter, float Confidence)
{
    public static readonly OcrCellResult Empty = new('\0', 0f);
}

/// <summary>
/// Single-letter OCR for board cells.
///
/// Primary backend is Windows.Media.Ocr (WinRT) — GPU-accelerated, ships with
/// Windows, and near-instant for clean game glyphs. Cells are preprocessed to
/// black-on-white and composed into one wide strip so a whole batch is
/// recognized in a single call; cells the strip pass misses get individual
/// retries.
///
/// Fallback backend is Tesseract 5 (psm 10, A–Z whitelist), used only when
/// WinRT OCR is unavailable and an <c>eng.traineddata</c> file exists in a
/// <c>tessdata</c> folder next to the executable.
/// </summary>
[SupportedOSPlatform("windows10.0.19041.0")]
public sealed class OcrEngine : IDisposable
{
    // Preprocessed glyph tile size and strip layout.
    private const int CellSize = 96;
    private const int StripGap = 48;
    private const int StripMargin = 48;
    private const int StripBatch = 8;

    // Letter zone inside a tile (fractions of the tile box). The bottom is cut
    // off before the point-value dots so they don't confuse the OCR.
    private const float ZoneLeft = 0.12f;
    private const float ZoneTop = 0.05f;
    private const float ZoneWidth = 0.76f;
    private const float ZoneHeight = 0.72f;

    private readonly IOcrBackend? _backend;

    public string BackendName { get; }

    public bool IsAvailable => _backend is not null;

    private OcrEngine(IOcrBackend? backend, string name)
    {
        _backend = backend;
        BackendName = name;
    }

    /// <summary>Creates the engine, preferring WinRT OCR, then Tesseract.</summary>
    public static OcrEngine Create()
    {
        try
        {
            var winRt = WinRtBackend.TryCreate();
            if (winRt is not null)
                return new OcrEngine(winRt, "Windows OCR");
        }
        catch (Exception ex)
        {
            Trace.WriteLine($"WinRT OCR unavailable: {ex.Message}");
        }

        try
        {
            var tess = TesseractBackend.TryCreate(
                Path.Combine(AppContext.BaseDirectory, "tessdata"));
            if (tess is not null)
                return new OcrEngine(tess, "Tesseract");
        }
        catch (Exception ex)
        {
            Trace.WriteLine($"Tesseract OCR unavailable: {ex.Message}");
        }

        return new OcrEngine(null, "None");
    }

    /// <summary>
    /// Recognizes the requested cells out of a full board capture.
    /// </summary>
    /// <param name="capture">The captured board region.</param>
    /// <param name="cells">All 16 cell boxes in capture coordinates.</param>
    /// <param name="cellIndices">Which cells to OCR (changed cells only, or all 16).</param>
    /// <returns>Results aligned with <paramref name="cellIndices"/>.</returns>
    public async Task<OcrCellResult[]> RecognizeCellsAsync(
        Bitmap capture, RectangleF[] cells, IReadOnlyList<int> cellIndices)
    {
        if (_backend is null)
            throw new InvalidOperationException("No OCR backend is available.");

        var images = new CellImage[cellIndices.Count];
        try
        {
            for (int i = 0; i < cellIndices.Count; i++)
                images[i] = PrepareCell(capture, cells[cellIndices[i]]);

            return await _backend.RecognizeAsync(images).ConfigureAwait(false);
        }
        finally
        {
            foreach (var img in images)
                img?.Dispose();
        }
    }

    public void Dispose() => (_backend as IDisposable)?.Dispose();

    // ------------------------------------------------------------------
    // Preprocessing
    // ------------------------------------------------------------------

    private sealed class CellImage : IDisposable
    {
        /// <summary>Thresholded black-on-white glyph, CellSize×CellSize.</summary>
        public required Bitmap Clean { get; init; }

        /// <summary>Plain grayscale crop (whole tile), for retry passes.</summary>
        public required Bitmap Raw { get; init; }

        public void Dispose()
        {
            Clean.Dispose();
            Raw.Dispose();
        }
    }

    private static CellImage PrepareCell(Bitmap capture, RectangleF cell)
    {
        var zone = new RectangleF(
            cell.X + cell.Width * ZoneLeft,
            cell.Y + cell.Height * ZoneTop,
            cell.Width * ZoneWidth,
            cell.Height * ZoneHeight);

        // Stretch the letter zone into the middle of a white square, then threshold.
        var clean = new Bitmap(CellSize, CellSize, PixelFormat.Format32bppArgb);
        using (var g = Graphics.FromImage(clean))
        {
            g.Clear(Color.White);
            g.InterpolationMode = InterpolationMode.HighQualityBicubic;
            const int pad = 10;
            g.DrawImage(capture,
                new RectangleF(pad, pad, CellSize - 2 * pad, CellSize - 2 * pad),
                zone, GraphicsUnit.Pixel);
        }
        Binarize(clean);

        // Raw crop of the whole tile at 2× cell size for retries.
        var raw = new Bitmap(CellSize * 2, CellSize * 2, PixelFormat.Format32bppArgb);
        using (var g = Graphics.FromImage(raw))
        {
            g.Clear(Color.White);
            g.InterpolationMode = InterpolationMode.HighQualityBicubic;
            g.DrawImage(capture,
                new RectangleF(0, 0, raw.Width, raw.Height),
                cell, GraphicsUnit.Pixel);
        }

        return new CellImage { Clean = clean, Raw = raw };
    }

    /// <summary>
    /// In-place Otsu threshold to pure black-on-white, inverting when the
    /// glyph turns out lighter than its background.
    /// </summary>
    private static void Binarize(Bitmap bmp)
    {
        var rect = new Rectangle(0, 0, bmp.Width, bmp.Height);
        var data = bmp.LockBits(rect, ImageLockMode.ReadWrite, PixelFormat.Format32bppArgb);
        try
        {
            int n = bmp.Width * bmp.Height;
            var pixels = new byte[data.Stride * bmp.Height];
            Marshal.Copy(data.Scan0, pixels, 0, pixels.Length);

            var gray = new byte[n];
            var histogram = new int[256];
            for (int i = 0; i < n; i++)
            {
                int o = i * 4;
                byte v = (byte)((pixels[o] + pixels[o + 1] + pixels[o + 2]) / 3);
                gray[i] = v;
                histogram[v]++;
            }

            int threshold = OtsuThreshold(histogram, n);

            int dark = 0;
            for (int i = 0; i < n; i++)
            {
                if (gray[i] < threshold)
                    dark++;
            }
            // The glyph should be the dark minority; if most pixels are dark the
            // tile theme is inverted, so flip polarity.
            bool invert = dark > n / 2;

            for (int i = 0; i < n; i++)
            {
                bool isInk = invert ? gray[i] >= threshold : gray[i] < threshold;
                byte v = isInk ? (byte)0 : (byte)255;
                int o = i * 4;
                pixels[o] = v;
                pixels[o + 1] = v;
                pixels[o + 2] = v;
                pixels[o + 3] = 255;
            }

            Marshal.Copy(pixels, 0, data.Scan0, pixels.Length);
        }
        finally
        {
            bmp.UnlockBits(data);
        }
    }

    private static int OtsuThreshold(int[] histogram, int total)
    {
        long sum = 0;
        for (int i = 0; i < 256; i++)
            sum += (long)i * histogram[i];

        long sumBackground = 0;
        int weightBackground = 0;
        double maxVariance = -1;
        int threshold = 128;

        for (int t = 0; t < 256; t++)
        {
            weightBackground += histogram[t];
            if (weightBackground == 0)
                continue;
            int weightForeground = total - weightBackground;
            if (weightForeground == 0)
                break;

            sumBackground += (long)t * histogram[t];
            double meanBackground = sumBackground / (double)weightBackground;
            double meanForeground = (sum - sumBackground) / (double)weightForeground;
            double variance = (double)weightBackground * weightForeground *
                              (meanBackground - meanForeground) * (meanBackground - meanForeground);
            if (variance > maxVariance)
            {
                maxVariance = variance;
                threshold = t + 1;
            }
        }
        return threshold;
    }

    // ------------------------------------------------------------------
    // Backends
    // ------------------------------------------------------------------

    private interface IOcrBackend
    {
        Task<OcrCellResult[]> RecognizeAsync(CellImage[] cells);
    }

    /// <summary>Windows.Media.Ocr backend — batches cells into strips.</summary>
    private sealed class WinRtBackend : IOcrBackend
    {
        private readonly WinOcrEngine _engine;

        private WinRtBackend(WinOcrEngine engine) => _engine = engine;

        public static WinRtBackend? TryCreate()
        {
            WinOcrEngine? engine = null;
            try
            {
                var english = new Windows.Globalization.Language("en-US");
                if (WinOcrEngine.IsLanguageSupported(english))
                    engine = WinOcrEngine.TryCreateFromLanguage(english);
            }
            catch
            {
                // fall through to profile languages
            }
            engine ??= WinOcrEngine.TryCreateFromUserProfileLanguages();
            return engine is null ? null : new WinRtBackend(engine);
        }

        public async Task<OcrCellResult[]> RecognizeAsync(CellImage[] cells)
        {
            var results = new OcrCellResult[cells.Length];

            // Pass 1: strip batches — one OCR call per StripBatch cells.
            for (int offset = 0; offset < cells.Length; offset += StripBatch)
            {
                int count = Math.Min(StripBatch, cells.Length - offset);
                await RecognizeStripAsync(cells, offset, count, results).ConfigureAwait(false);
            }

            // Pass 2: individual retries for anything the strip missed.
            for (int i = 0; i < cells.Length; i++)
            {
                if (results[i].Letter == '\0')
                    results[i] = await RecognizeSingleAsync(cells[i]).ConfigureAwait(false);
            }

            return results;
        }

        private async Task RecognizeStripAsync(
            CellImage[] cells, int offset, int count, OcrCellResult[] results)
        {
            int slotStride = CellSize + StripGap;
            int width = StripMargin * 2 + count * CellSize + (count - 1) * StripGap;
            int height = StripMargin * 2 + CellSize;

            using var strip = new Bitmap(width, height, PixelFormat.Format32bppArgb);
            using (var g = Graphics.FromImage(strip))
            {
                g.Clear(Color.White);
                for (int i = 0; i < count; i++)
                    g.DrawImage(cells[offset + i].Clean, StripMargin + i * slotStride, StripMargin);
            }

            var ocr = await RunOcrAsync(strip).ConfigureAwait(false);
            if (ocr is null)
                return;

            foreach (var line in ocr.Lines)
            {
                foreach (var word in line.Words)
                {
                    string text = word.Text;
                    if (string.IsNullOrEmpty(text))
                        continue;
                    var rect = word.BoundingRect;
                    for (int j = 0; j < text.Length; j++)
                    {
                        var normalized = OcrNormalizer.NormalizeChar(text[j]);
                        if (normalized is null)
                            continue;

                        double charCenterX = rect.X + rect.Width * (j + 0.5) / text.Length;
                        int slot = (int)Math.Round(
                            (charCenterX - StripMargin - CellSize / 2.0) / slotStride);
                        if (slot < 0 || slot >= count)
                            continue;
                        double slotCenter = StripMargin + slot * slotStride + CellSize / 2.0;
                        if (Math.Abs(charCenterX - slotCenter) > slotStride / 2.0)
                            continue;

                        float confidence = normalized.Value.Remapped
                            ? OcrNormalizer.MappedConfidence
                            : OcrNormalizer.CleanConfidence;

                        int target = offset + slot;
                        if (confidence > results[target].Confidence)
                            results[target] = new OcrCellResult(normalized.Value.Letter, confidence);
                    }
                }
            }
        }

        private async Task<OcrCellResult> RecognizeSingleAsync(CellImage cell)
        {
            // Variant A: the clean thresholded glyph, upscaled.
            using (var big = new Bitmap(cell.Clean, CellSize * 2, CellSize * 2))
            {
                var r = await RecognizeWholeAsync(big, 1f).ConfigureAwait(false);
                if (r.Letter != '\0')
                    return r;
            }

            // Variant B: the raw tile crop — in case thresholding destroyed the glyph.
            return await RecognizeWholeAsync(cell.Raw, 0.9f).ConfigureAwait(false);
        }

        private async Task<OcrCellResult> RecognizeWholeAsync(Bitmap bmp, float confidenceScale)
        {
            var ocr = await RunOcrAsync(bmp).ConfigureAwait(false);
            if (ocr is null)
                return OcrCellResult.Empty;

            var token = string.Concat(ocr.Lines.SelectMany(l => l.Words).Select(w => w.Text));
            var normalized = OcrNormalizer.NormalizeToken(token);
            if (normalized is null)
                return OcrCellResult.Empty;
            return new OcrCellResult(normalized.Value.Letter, normalized.Value.Confidence * confidenceScale);
        }

        private async Task<Windows.Media.Ocr.OcrResult?> RunOcrAsync(Bitmap bmp)
        {
            using var soft = ToSoftwareBitmap(bmp);
            try
            {
                return await _engine.RecognizeAsync(soft);
            }
            catch (Exception ex)
            {
                Trace.WriteLine($"WinRT OCR call failed: {ex.Message}");
                return null;
            }
        }

        private static SoftwareBitmap ToSoftwareBitmap(Bitmap bmp)
        {
            var rect = new Rectangle(0, 0, bmp.Width, bmp.Height);
            var data = bmp.LockBits(rect, ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
            try
            {
                int rowBytes = bmp.Width * 4;
                var pixels = new byte[rowBytes * bmp.Height];
                if (data.Stride == rowBytes)
                {
                    Marshal.Copy(data.Scan0, pixels, 0, pixels.Length);
                }
                else
                {
                    for (int y = 0; y < bmp.Height; y++)
                        Marshal.Copy(data.Scan0 + y * data.Stride, pixels, y * rowBytes, rowBytes);
                }
                return SoftwareBitmap.CreateCopyFromBuffer(
                    pixels.AsBuffer(), BitmapPixelFormat.Bgra8,
                    bmp.Width, bmp.Height, BitmapAlphaMode.Ignore);
            }
            finally
            {
                bmp.UnlockBits(data);
            }
        }
    }

    /// <summary>
    /// Tesseract 5 fallback (psm 10 = single character, A–Z whitelist).
    /// Requires tessdata/eng.traineddata next to the executable.
    /// </summary>
    private sealed class TesseractBackend : IOcrBackend, IDisposable
    {
        private readonly Tesseract.TesseractEngine _engine;

        private TesseractBackend(Tesseract.TesseractEngine engine) => _engine = engine;

        public static TesseractBackend? TryCreate(string tessdataDir)
        {
            if (!File.Exists(Path.Combine(tessdataDir, "eng.traineddata")))
                return null;
            var engine = new Tesseract.TesseractEngine(tessdataDir, "eng", Tesseract.EngineMode.Default);
            engine.SetVariable("tessedit_char_whitelist", "ABCDEFGHIJKLMNOPQRSTUVWXYZ");
            return new TesseractBackend(engine);
        }

        public Task<OcrCellResult[]> RecognizeAsync(CellImage[] cells)
        {
            // Tesseract is synchronous and single-threaded per engine.
            return Task.Run(() =>
            {
                var results = new OcrCellResult[cells.Length];
                for (int i = 0; i < cells.Length; i++)
                    results[i] = RecognizeCell(cells[i].Clean);
                return results;
            });
        }

        private OcrCellResult RecognizeCell(Bitmap bmp)
        {
            try
            {
                using var ms = new MemoryStream();
                bmp.Save(ms, ImageFormat.Png);
                using var pix = Tesseract.Pix.LoadFromMemory(ms.ToArray());
                using var page = _engine.Process(pix, Tesseract.PageSegMode.SingleChar);
                var normalized = OcrNormalizer.NormalizeToken(page.GetText());
                if (normalized is null)
                    return OcrCellResult.Empty;
                float confidence = Math.Min(normalized.Value.Confidence, page.GetMeanConfidence());
                return new OcrCellResult(normalized.Value.Letter, confidence);
            }
            catch (Exception ex)
            {
                Trace.WriteLine($"Tesseract cell OCR failed: {ex.Message}");
                return OcrCellResult.Empty;
            }
        }

        public void Dispose() => _engine.Dispose();
    }
}
