using System.Reflection;

namespace Soneto.Core.Dictionary;

/// <summary>
/// Phase 2 work item 10 (§2.10): the embedded seed dictionary, shipped as the default
/// <c>dictionary.json</c> the first time <see cref="DictionaryService.LoadAsync"/> finds no file
/// on disk -- mirrors the established <c>warmup-en.wav</c>/<c>silero_vad.onnx</c>/
/// <c>common-words-en.txt</c> embedded-resource convention (see <see cref="WordFrequencyList"/>'s
/// doc comment) rather than downloading anything at runtime; this file is small enough (a couple
/// dozen entries) to ship as a plain embedded resource, same as those.
///
/// <para>
/// <b>Contents (§2.10's literal vocabulary list):</b> 24 <see cref="VocabularyTerm"/> entries
/// (casing-correction only -- every one of these is an already-correctly-cased technical
/// term/product name/acronym, not a mis-transcription pattern, so no explicit
/// <see cref="CorrectionPair"/> is needed for any of them: <c>webMethods</c>, <c>Integration
/// Server</c>, <c>Trading Networks</c>, <c>Enterprise Gateway</c>, <c>Universal Messaging</c>,
/// <c>MFT</c>, <c>GoAnywhere</c>, <c>AS2</c>, <c>EDIINT</c>, <c>Informatica</c>,
/// <c>PowerCenter</c>, <c>IDMC</c>, <c>BusinessObjects</c>, <c>LoadRunner</c>,
/// <c>SonarQube</c>, <c>QuerySurge</c>, <c>Spotfire</c>, <c>Proxmox</c>, <c>Unraid</c>,
/// <c>Avalonia</c>, <c>keystore</c>, <c>truststore</c>, <c>PKCS#12</c>, <c>JKS</c>) plus 4
/// <see cref="SpokenCommand"/> entries.
/// </para>
///
/// <para>
/// <b>Design decision on the 4 <see cref="SpokenCommand"/> entries -- deliberately DO overlap
/// <see cref="SpokenCommandsExtensionProcessor.BuiltInDefaults"/>'s phrases, with distinct
/// <c>Id</c>s (<c>seed.spoken-command.*</c> vs. <c>builtin.spoken-command.*</c>):</b> per that
/// class's own documented collision policy ("a user-provided entry whose phrase collides with a
/// built-in's phrase wins," keyed on <c>Phrase</c> not <c>Id</c>), once this seed dictionary is
/// loaded, these 4 seed entries silently take over from the hardcoded
/// <c>BuiltInDefaults</c> table for <c>AppliedRule</c> provenance purposes (a fired rule's
/// <c>Rule</c>/Id shows <c>"seed.spoken-command.*"</c> instead of <c>"builtin.spoken-command.*"</c>)
/// -- functionally identical output either way (same <c>Phrase</c>, same <c>Emits</c>), just a
/// different <c>Id</c> in any future rule-fired history/diff. <b>This is considered CORRECT, not
/// a bug to avoid:</b> §2.10 itself frames folding these into the seed file as the intended end
/// state, and the seed dictionary becoming the "real," user-editable source of truth for these 4
/// commands the moment it exists on disk is arguably more honest than leaving them silently
/// hardcoded forever. <c>BuiltInDefaults</c> is kept as-is (not removed) as the zero-dependency
/// fallback for the case where <c>dictionary.json</c> is missing, unreadable, or has all its
/// <see cref="SpokenCommand"/> entries individually rejected -- a case that is now much rarer
/// since <see cref="DictionaryService"/> always writes this seed file on first run, but still
/// possible (e.g. a permission-denied dictionary directory).
/// </para>
/// </summary>
public static class SeedDictionary
{
    private const string ResourceName = "Soneto.Core.Dictionary.Resources.seed-dictionary.json";

    /// <summary>
    /// The raw <c>seed-dictionary.json</c> text, read fresh from the embedded resource on every
    /// access. Unlike <see cref="WordFrequencyList.Instance"/>'s lazy-singleton caching, this
    /// isn't cached -- it's read at most once per process (by <see cref="DictionaryService"/>'s
    /// first-run path), so the extra simplicity of "just re-read the resource" outweighs any
    /// benefit from caching a value nothing re-reads in a hot loop.
    /// </summary>
    public static string Json
    {
        get
        {
            var asm = Assembly.GetExecutingAssembly();
            using var stream = asm.GetManifestResourceStream(ResourceName)
                ?? throw new InvalidOperationException(
                    $"Embedded seed dictionary resource '{ResourceName}' not found in {asm.FullName}.");
            using var reader = new StreamReader(stream);
            return reader.ReadToEnd();
        }
    }
}
