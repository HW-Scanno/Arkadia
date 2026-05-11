using System.IO;
using System.Text.RegularExpressions;
using Arkadia;
using Xunit;

namespace Arkadia.Tests.Ark;

public sealed class ArkUiHelpersTests
{
    [Fact]
    public void SuggestedArkFileName_HasArkExtension()
    {
        var name = ArkUiHelpers.SuggestedArkFileName();
        Assert.EndsWith(".ark", name);
    }

    [Fact]
    public void SuggestedArkFileName_MatchesExpectedPattern()
    {
        var name = ArkUiHelpers.SuggestedArkFileName();
        Assert.Matches(@"^arkadia-backup-\d{8}-\d{6}\.ark$", name);
    }

    [Fact]
    public void BackupsFolder_ReturnsPathUnderBaseDir()
    {
        var result = ArkUiHelpers.BackupsFolder(@"C:\data");
        Assert.Equal(Path.Combine(@"C:\data", ArkadiaFolders.Backups), result);
    }
}
