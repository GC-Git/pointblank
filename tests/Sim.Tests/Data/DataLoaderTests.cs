using PointBlank.Sim.Data;

namespace PointBlank.Sim.Tests.Data;

public class DataLoaderTests
{
    private const string Ref = """{"kind":"reference","ref":"Some Manual, War Dept, p. 4"}""";

    private static string Ammo(string source) =>
        "{\"table\":\"ammo\",\"entries\":[{\"id\":\"a1\",\"values\":{"
        + "\"caliber\":{\"value\":\"7.62\",\"unit\":\"mm\",\"source\":" + source + "},"
        + "\"bullet_mass\":{\"value\":\"9.7\",\"unit\":\"g\",\"source\":{\"kind\":\"placeholder\"}}}}]}";

    [Fact]
    public void Loads_a_table_and_converts_numbers_to_Fixed()
    {
        var table = DataLoader.ParseTable(Ammo(Ref), "ammo");
        var value = table.Entries[0].Get("caliber");
        Assert.Equal("mm", value.Unit);
        Assert.Equal(Fixed.FromRatio(762, 100), value.Number);
        Assert.Equal(DataSourceKind.Reference, value.Source.Kind);
    }

    [Theory]
    [InlineData("0", 0)]
    [InlineData("853", 853)]
    [InlineData("-2", -2)]
    public void Parses_integers_exactly(string text, int whole)
    {
        Assert.True(DataLoader.TryParseFixed(text, out var f));
        Assert.Equal(Fixed.FromInt(whole), f);
    }

    [Fact]
    public void Parses_decimals_without_floating_point()
    {
        Assert.True(DataLoader.TryParseFixed("0.5", out var half));
        Assert.Equal(Fixed.FromRatio(1, 2), half);
        Assert.True(DataLoader.TryParseFixed("-1.25", out var f));
        Assert.Equal(Fixed.FromRatio(-5, 4), f);
    }

    [Theory]
    [InlineData("")]
    [InlineData("abc")]
    [InlineData("1e3")]
    [InlineData("1.")]
    [InlineData(".5")]
    [InlineData("1,5")]
    [InlineData("1234567890123456")]
    public void Rejects_malformed_numbers(string text) =>
        Assert.False(DataLoader.TryParseFixed(text, out _));

    [Fact]
    public void Rejects_a_value_with_no_source()
    {
        var json = Ammo(Ref).Replace(",\"source\":" + Ref, "");
        var e = Assert.Throws<DataException>(() => DataLoader.ParseTable(json, "ammo"));
        Assert.Contains("source", e.Message);
        Assert.Contains("caliber", e.Message);
    }

    [Theory]
    [InlineData("""{"kind":"reference"}""")]
    [InlineData("""{"kind":"reference","ref":"  "}""")]
    [InlineData("""{"kind":"estimated"}""")]
    [InlineData("""{"kind":"estimated","reasoning":""}""")]
    [InlineData("""{"kind":"guess"}""")]
    [InlineData("""{"ref":"x"}""")]
    [InlineData("\"placeholder\"")]
    [InlineData("null")]
    public void Rejects_a_bad_source(string source) =>
        Assert.Throws<DataException>(() => DataLoader.ParseTable(Ammo(source), "ammo"));

    [Fact]
    public void Accepts_estimated_with_reasoning_and_placeholder_with_or_without_note()
    {
        var est = DataLoader.ParseTable(
            Ammo("""{"kind":"estimated","reasoning":"Sources disagree; midpoint."}"""), "ammo");
        Assert.Equal(DataSourceKind.Estimated, est.Entries[0].Get("caliber").Source.Kind);
        var ph = DataLoader.ParseTable(
            Ammo("""{"kind":"placeholder","note":"to research"}"""), "ammo");
        Assert.Equal(DataSourceKind.Placeholder, ph.Entries[0].Get("caliber").Source.Kind);
        Assert.Equal(DataSourceKind.Placeholder, ph.Entries[0].Get("bullet_mass").Source.Kind);
    }

    [Fact]
    public void Rejects_missing_unit()
    {
        Assert.Throws<DataException>(() => DataLoader.ParseTable(
            Ammo(Ref).Replace("\"unit\":\"mm\",", ""), "ammo"));
    }

    [Fact]
    public void Rejects_wrong_table_name_missing_required_value_and_bad_json()
    {
        Assert.Throws<DataException>(() => DataLoader.ParseTable(Ammo(Ref), "weapons"));
        Assert.Throws<DataException>(() => DataLoader.ParseTable(
            Ammo(Ref).Replace("\"bullet_mass\"", "\"other\""), "ammo"));
        Assert.Throws<DataException>(() => DataLoader.ParseTable("{not json", "ammo"));
    }

    [Fact]
    public void Rejects_duplicate_ids()
    {
        var one = Ammo(Ref);
        var start = one.IndexOf("{\"id\"", StringComparison.Ordinal);
        var entry = one[start..^2];
        var dup = one.Replace(entry, entry + "," + entry);
        Assert.Throws<DataException>(() => DataLoader.ParseTable(dup, "ammo"));
    }

    [Fact]
    public void Accepts_json_numbers_as_values()
    {
        var json = Ammo(Ref).Replace("\"7.62\"", "762");
        var table = DataLoader.ParseTable(json, "ammo");
        Assert.Equal(Fixed.FromInt(762), table.Entries[0].Get("caliber").Number);
    }

    [Fact]
    public void Text_values_have_no_number()
    {
        var json = Ammo(Ref).Replace("\"7.62\"", "\"hello\"").Replace("\"mm\"", "\"text\"");
        var v = DataLoader.ParseTable(json, "ammo").Entries[0].Get("caliber");
        Assert.Equal("hello", v.Text);
        Assert.Null(v.Number);
    }

    [Fact]
    public void Report_counts_placeholders_per_table_in_a_fixed_order()
    {
        var set = DataLoader.LoadDirectory(DataPaths.DataDirectory());
        var report = PlaceholderReport.Build(set);
        Assert.Equal(DataLoader.TableNames, report.Tables.Select(t => t.Table).ToArray());
        Assert.Equal(report.Tables.Sum(t => t.Placeholders), report.TotalPlaceholders);
        Assert.True(report.TotalPlaceholders > 0);
        Assert.Equal(report.Render(), PlaceholderReport.Build(set).Render());
        Assert.Contains("Total placeholders: " + report.TotalPlaceholders, report.Render());
    }

    [Fact]
    public void Sample_data_has_the_required_content()
    {
        var set = DataLoader.LoadDirectory(DataPaths.DataDirectory());
        Assert.Equal(2, set.Table("weapons").Entries.Count);
        Assert.Equal(2, set.Table("ammo").Entries.Count);
        Assert.Single(set.Table("materials").Entries);
        Assert.NotEmpty(set.Table("units").Entries);
        Assert.NotEmpty(set.Table("organisation").Entries);
        Assert.All(
            set.Tables.SelectMany(t => t.Entries).SelectMany(e => e.Values.Values),
            v => Assert.Equal(DataSourceKind.Placeholder, v.Source.Kind));
    }

    [Fact]
    public void Missing_file_is_a_DataException() =>
        Assert.Throws<DataException>(() => DataLoader.LoadDirectory(
            Path.Combine(DataPaths.DataDirectory(), "nope")));
}
