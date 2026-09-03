using System.Text;
using Soneto.Core.Abstractions;

namespace Soneto.Core.PostProcessing;

/// <summary>
/// Order 10 stage of the plan §1.7 post-processing chain: NFC-normalises the transcript
/// and maps the Turkish/legacy cedilla forms of ş/ţ (U+015F / U+0163) — which some
/// Romanian-locale keyboards/fonts and older text sources still use — to the correct
/// Romanian comma-below forms ș/ț (U+0219 / U+021B).
///
/// Per plan §1.7 this stage is "Always on": <see cref="Configuration.PostProcessConfig.NormalizeUnicode"/>
/// exists in the config schema but is deliberately NOT wired to a disable switch here —
/// the plan's prose is treated as authoritative over the flag's mere presence. This
/// processor therefore has no constructor toggle at all (unlike the other three stages).
/// </summary>
public sealed class UnicodeNormalizerProcessor : IPostProcessor
{
    public int Order => 10;
    public string Name => "UnicodeNormalizer";

    public PostProcessResult Process(PostProcessResult input)
    {
        var text = input.Text;
        if (string.IsNullOrEmpty(text))
            return input;

        text = MapCedillaToCommaBelow(text);
        text = text.Normalize(NormalizationForm.FormC);

        return text == input.Text ? input : input with { Text = text };
    }

    private static string MapCedillaToCommaBelow(string text)
    {
        // Only allocate a new string when a replacement is actually needed.
        if (text.IndexOf('ş') < 0 && text.IndexOf('ţ') < 0)
            return text;

        var sb = new StringBuilder(text.Length);
        foreach (var c in text)
        {
            sb.Append(c switch
            {
                'ş' => 'ș', // ş -> ș
                'ţ' => 'ț', // ţ -> ț
                _ => c,
            });
        }
        return sb.ToString();
    }
}
