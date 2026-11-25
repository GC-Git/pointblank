using PointBlank.Runner;
using PointBlank.Sim.Tests.Data;

namespace PointBlank.Sim.Tests.Runner;

public class DataReportTests
{
    private static (int Code, string Out) Run(params string[] args)
    {
        var o = new StringWriter();
        var code = RunnerApp.Run(args, o, new StringWriter());
        return (code, o.ToString());
    }

    [Fact]
    public void Data_report_is_deterministic_and_lists_placeholders()
    {
        var a = Run("--data-report", DataPaths.DataDirectory());
        var b = Run("--data-report", DataPaths.DataDirectory());
        Assert.Equal(0, a.Code);
        Assert.Equal(a.Out, b.Out);
        Assert.Contains("Total placeholders:", a.Out);
    }

    [Fact]
    public void Data_report_fails_on_a_missing_directory() =>
        Assert.Equal(2, Run("--data-report", "no-such-dir").Code);
}
