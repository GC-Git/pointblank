using System.Runtime.CompilerServices;
using System.Xml.Linq;

namespace PointBlank.Sim.Tests;

/// <summary>
/// Item 2.5a: Godot's headless <c>--build-solutions</c> command must exit. Its Windows console wrapper stays up while
/// any process the build started is alive, so a build here must leave no persistent MSBuild node or compiler server.
/// </summary>
public class BuildSettingsTests
{
    private static string Root([CallerFilePath] string here = "") =>
        Path.GetFullPath(Path.Combine(Path.GetDirectoryName(here)!, "..", ".."));

    [Fact]
    public void Builds_leave_no_msbuild_node_running()
    {
        var switches = File.ReadAllLines(Path.Combine(Root(), "Directory.Build.rsp")).Select(l => l.Trim());
        Assert.Contains("-nodeReuse:false", switches);
    }

    [Fact]
    public void Builds_leave_no_compiler_server_running()
    {
        var props = XDocument.Load(Path.Combine(Root(), "Directory.Build.props"));
        Assert.Equal("false", props.Descendants("UseSharedCompilation").Single().Value);
    }
}
