using HamProgramAutoUpdate.Services.Updaters.Programs;

namespace HamProgramAutoUpdate.Tests;

public class ToolLineTests
{
    [Theory]
    [InlineData("[09:10:32] N1MM County Data Updater starting.", "N1MM County Data Updater starting.")]
    [InlineData("[9:10:33] Checked 51 part(ies), 0 changed, 0 published, 0 waiting for review",
        "Checked 51 part(ies), 0 changed, 0 published, 0 waiting for review")]
    [InlineData("no timestamp here", "no timestamp here")]
    [InlineData("[note] not a timestamp", "[note] not a timestamp")]
    public void TidyToolLine_DropsOnlyALeadingTimestamp(string line, string expected)
    {
        Assert.Equal(expected, N1mmCountyDataToolUpdater.TidyToolLine(line));
    }

    [Theory]
    [InlineData("[09:10:52] N1MM County Data Updater completed successfully")]
    [InlineData("N1MM County Data Updater completed successfully")]
    public void TidyToolLine_DropsTheToolsOwnClosingLine(string line)
    {
        Assert.Null(N1mmCountyDataToolUpdater.TidyToolLine(line));
    }
}
