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

    [Fact]
    public void Data_report_prints_a_bad_value_cleanly()
    {
        var dir = Path.Combine(Path.GetTempPath(), "pb-data-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            foreach (var f in Directory.GetFiles(DataPaths.DataDirectory()))
            {
                File.Copy(f, Path.Combine(dir, Path.GetFileName(f)));
            }

            var path = Path.Combine(dir, "ammo.json");
            File.WriteAllText(path, File.ReadAllText(path).Replace("\"7.6\"", "\"99999999999\""));
            var err = new StringWriter();
            var code = RunnerApp.Run(["--data-report", dir], new StringWriter(), err);
            Assert.Equal(2, code);
            Assert.Contains("ammo.json", err.ToString());
            Assert.Contains("ammo/us_rifle_round_sample/caliber", err.ToString());
        }
        finally
        {
            Directory.Delete(dir, true);
        }
    }
}
