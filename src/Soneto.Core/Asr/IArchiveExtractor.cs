namespace Soneto.Core.Asr;

/// <summary>
/// Extracts a downloaded model archive. Abstracted (like <see cref="IModelArchiveDownloader"/>)
/// so <see cref="ModelManager"/>'s "verify all required files present after extraction"
/// behaviour can be unit-tested with a fake that doesn't require the real ~640MB model or
/// an external <c>tar</c> process.
/// </summary>
public interface IArchiveExtractor
{
    /// <summary>
    /// Extracts <paramref name="archivePath"/> into <paramref name="destinationParentDir"/>.
    /// The archive's own top-level entry is expected to be the model folder itself (this
    /// matches the actual sherpa-onnx release tarball layout — see
    /// spikes/s1-asr/README.md "Getting the model"), so the caller should look for the
    /// model folder as a child of <paramref name="destinationParentDir"/> afterward.
    /// </summary>
    void Extract(string archivePath, string destinationParentDir);
}
