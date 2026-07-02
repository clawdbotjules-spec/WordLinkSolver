namespace WordLinkSolver.Core;

/// <summary>
/// Maps raw OCR output to a single board letter A–Z.
///
/// The board only ever contains uppercase letters, so common OCR confusions
/// (digits, pipes, lowercase) can be corrected deterministically. Corrections
/// are reported with reduced confidence so the overlay can flag the cell.
/// </summary>
public static class OcrNormalizer
{
    /// <summary>Confidence assigned to a clean single A–Z (or a–z) read.</summary>
    public const float CleanConfidence = 1.0f;

    /// <summary>Confidence when the character had to be remapped from a lookalike.</summary>
    public const float MappedConfidence = 0.55f;

    /// <summary>Extra penalty multiplier when the OCR token had more than one character.</summary>
    public const float MultiCharPenalty = 0.8f;

    /// <summary>
    /// Normalizes one character to A–Z.
    /// Returns null when the character has no sensible letter interpretation.
    /// The bool is true when the value came from a lookalike remap.
    /// </summary>
    public static (char Letter, bool Remapped)? NormalizeChar(char c)
    {
        if (c is >= 'A' and <= 'Z')
            return (c, false);
        if (c is >= 'a' and <= 'z')
            return ((char)(c - 32), false);

        // Lookalikes seen on stylized game tiles.
        char? mapped = c switch
        {
            '0' => 'O',
            '1' => 'I',
            '|' => 'I',
            '!' => 'I',
            ']' => 'I',
            '[' => 'I',
            '2' => 'Z',
            '4' => 'A',
            '5' => 'S',
            '6' => 'G',
            '7' => 'T',
            '8' => 'B',
            '9' => 'G',
            '$' => 'S',
            '&' => 'B',
            _ => null,
        };
        return mapped is null ? null : (mapped.Value, true);
    }

    /// <summary>
    /// Extracts the single most plausible letter from an OCR token
    /// (e.g. "M", "m", "|", "N.", "!M").
    /// Returns null when the token contains nothing letter-like.
    /// </summary>
    public static (char Letter, float Confidence)? NormalizeToken(string? token)
    {
        if (string.IsNullOrWhiteSpace(token))
            return null;

        token = token.Trim();

        // Prefer a clean letter anywhere in the token before falling back to remaps.
        (char Letter, bool Remapped)? best = null;
        foreach (var c in token)
        {
            var n = NormalizeChar(c);
            if (n is null)
                continue;
            if (!n.Value.Remapped)
            {
                best = n;
                break;
            }
            best ??= n;
        }

        if (best is null)
            return null;

        float confidence = best.Value.Remapped ? MappedConfidence : CleanConfidence;
        if (token.Length > 1)
            confidence *= MultiCharPenalty;
        return (best.Value.Letter, confidence);
    }
}
