using PointBlank.Sim.Data;

namespace PointBlank.Sim.Tests.Data;

public class NoPlaceholdersTests
{
    /// <summary>
    /// Item 7.7 switches this on: change the value below from the text to <c>null</c>
    /// (one line). Until then the test is skipped because the data still has placeholders.
    /// </summary>
    private const string? SkipUntilItem77 = "Disabled until item 7.7: data still contains placeholders.";

    [Fact(Skip = SkipUntilItem77)]
    public void Data_files_contain_no_placeholders()
    {
        var report = PlaceholderReport.Build(DataLoader.LoadDirectory(DataPaths.DataDirectory()));
        Assert.True(report.TotalPlaceholders == 0, report.Render());
    }

    [Fact]
    public void The_check_would_fail_while_placeholders_remain()
    {
        var report = PlaceholderReport.Build(DataLoader.LoadDirectory(DataPaths.DataDirectory()));
        Assert.True(report.TotalPlaceholders > 0);
    }
}
