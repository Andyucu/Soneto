namespace s4_inject_win;

internal static class TestData
{
    /// <summary>
    /// Exact test string from the plan's S4 section. Built with an explicit
    /// "\n" (not a raw string literal) so the exact byte content is
    /// independent of git's line-ending normalization on checkout.
    /// </summary>
    internal const string TestString =
        "Ăsta e un test: șoseaua Ștefan cel Mare, țară, îngheț — 100% \"quoted\" & <tagged>.\n" +
        "Line two after a newline.";

    /// <summary>
    /// Byte-level diacritic check: ș/ț must be U+0219/U+021B (comma-below),
    /// never the cedilla forms U+015F/U+0163, and must not be mangled/dropped.
    /// </summary>
    internal static (bool Pass, string Detail) CheckDiacritics(string text)
    {
        bool hasCorrectS = text.Contains('ș') || text.Contains('Ș'); // ș / Ș
        bool hasCorrectT = text.Contains('ț') || text.Contains('Ț'); // ț / Ț
        bool hasCedillaS = text.Contains('ş') || text.Contains('Ş'); // ş / Ş (wrong)
        bool hasCedillaT = text.Contains('ţ') || text.Contains('Ţ'); // ţ / Ţ (wrong)
        bool hasAWithBreve = text.Contains('Ă') || text.Contains('ă'); // Ă / ă

        bool pass = hasCorrectS && hasCorrectT && hasAWithBreve && !hasCedillaS && !hasCedillaT;
        string detail = $"comma-below ș/ț present={hasCorrectS}/{hasCorrectT}, cedilla ş/ţ present={hasCedillaS}/{hasCedillaT} (must be false), ă/Ă present={hasAWithBreve}";
        return (pass, detail);
    }
}
