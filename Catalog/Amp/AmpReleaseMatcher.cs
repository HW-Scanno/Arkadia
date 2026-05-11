using System;
using System.Collections.Generic;

namespace Arkadia;

public enum AmpReleaseMatchKind { None, ReleaseId, DatName }

public sealed record AmpReleaseMatchResult(AmpReleaseInfo? Release, AmpReleaseMatchKind Kind);

public static class AmpReleaseMatcher
{
    public static AmpReleaseMatchResult FindRelease(
        IReadOnlyList<AmpReleaseInfo> releases,
        string                        releaseId,
        string                        datName)
    {
        foreach (var r in releases)
        {
            if (string.Equals(r.ReleaseId, releaseId, StringComparison.Ordinal))
                return new AmpReleaseMatchResult(r, AmpReleaseMatchKind.ReleaseId);
        }

        foreach (var r in releases)
        {
            if (string.Equals(r.DatName, datName, StringComparison.Ordinal))
                return new AmpReleaseMatchResult(r, AmpReleaseMatchKind.DatName);
        }

        return new AmpReleaseMatchResult(null, AmpReleaseMatchKind.None);
    }
}
