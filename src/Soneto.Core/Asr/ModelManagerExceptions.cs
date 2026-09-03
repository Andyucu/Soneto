namespace Soneto.Core.Asr;

/// <summary>
/// Thrown when a resolved model directory (config override or the standard location) is
/// missing one or more of the four required model files, or when extraction of a freshly
/// downloaded archive still leaves files missing. Plan §1.12: "Model files missing" is a
/// startup-detected, fatal-until-fixed condition — never silently proceed.
/// </summary>
public sealed class ModelFilesMissingException : Exception
{
    public ModelFilesMissingException(string message) : base(message)
    {
    }
}

/// <summary>
/// Thrown when a downloaded model archive fails SHA-256 verification twice in a row (the
/// initial attempt plus one retry). Plan §1.6 / §1.12: "delete, retry once, then fatal
/// with clear message" — never run inference against unverified weights.
/// </summary>
public sealed class ModelHashMismatchException : Exception
{
    public ModelHashMismatchException(string message) : base(message)
    {
    }
}
