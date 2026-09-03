using Soneto.Core.Dictionary;

namespace Soneto.Core.Tests.Dictionary;

/// <summary>
/// Phase 4 item 3 (§4.4): unit tests for the dictionary-side <see cref="PerAppOverrideResolver"/>
/// -- the pure lookup counterpart to <c>Configuration.PerAppOverrideResolverTests</c> (Phase 4
/// item 2), covering the exact same null/empty/no-match fall-through contract that class's own
/// tests established, without any merge logic (this resolver has none -- see its own doc
/// comment).
/// </summary>
public class PerAppOverrideResolverTests
{
    private static PerAppOverride Profile(string processName, bool autoCapitalize = true, bool trailingPunctuation = true, bool enabled = true) =>
        new()
        {
            Id = "test." + processName,
            ProcessName = processName,
            AutoCapitalize = autoCapitalize,
            TrailingPunctuation = trailingPunctuation,
            Enabled = enabled,
        };

    [Fact]
    public void Resolve_NullTable_ReturnsNull()
    {
        Assert.Null(PerAppOverrideResolver.Resolve("wt.exe", null));
    }

    [Fact]
    public void Resolve_EmptyTable_ReturnsNull()
    {
        var table = new Dictionary<string, PerAppOverride>();
        Assert.Null(PerAppOverrideResolver.Resolve("wt.exe", table));
    }

    [Fact]
    public void Resolve_NullProcessName_ReturnsNull()
    {
        var table = new Dictionary<string, PerAppOverride> { ["wt.exe"] = Profile("wt.exe") };
        Assert.Null(PerAppOverrideResolver.Resolve(null, table));
    }

    [Fact]
    public void Resolve_NoMatch_ReturnsNull()
    {
        var table = new Dictionary<string, PerAppOverride> { ["wt.exe"] = Profile("wt.exe") };
        Assert.Null(PerAppOverrideResolver.Resolve("Teams.exe", table));
    }

    [Fact]
    public void Resolve_Match_ReturnsTheMatchingEntry()
    {
        var wtProfile = Profile("wt.exe", autoCapitalize: false, trailingPunctuation: true);
        var table = new Dictionary<string, PerAppOverride> { ["wt.exe"] = wtProfile };

        var resolved = PerAppOverrideResolver.Resolve("wt.exe", table);

        Assert.Same(wtProfile, resolved);
    }

    [Fact]
    public void Resolve_CaseSensitivity_IsTheCallersResponsibility()
    {
        // This class applies no comparer of its own -- an OrdinalIgnoreCase-keyed dictionary
        // (as the composition root builds) resolves case-insensitively; a default-comparer
        // dictionary does not. Proves both directions rather than assuming one.
        var profile = Profile("wt.exe");

        var caseInsensitiveTable = new Dictionary<string, PerAppOverride>(StringComparer.OrdinalIgnoreCase)
        {
            ["wt.exe"] = profile,
        };
        Assert.Same(profile, PerAppOverrideResolver.Resolve("WT.EXE", caseInsensitiveTable));

        var defaultComparerTable = new Dictionary<string, PerAppOverride> { ["wt.exe"] = profile };
        Assert.Null(PerAppOverrideResolver.Resolve("WT.EXE", defaultComparerTable));
    }
}
