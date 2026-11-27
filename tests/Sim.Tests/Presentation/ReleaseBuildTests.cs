using System.Collections;
using System.Diagnostics;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.Loader;

namespace PointBlank.Sim.Tests.Presentation;

/// <summary>
/// Item 2.6, CLAUDE.md rule 11: the truth bridge <c>PlayerBattle.DebugTruth</c> works only in development builds. The
/// game project is built as Godot's exports build it, and the assemblies that come out are loaded and driven.
/// </summary>
[Collection("dotnet-build")]
public class ReleaseBuildTests
{
    private static string Root([CallerFilePath] string here = "") =>
        Path.GetFullPath(Path.Combine(Path.GetDirectoryName(here)!, "..", "..", ".."));

    private static string Build(string configuration)
    {
        var start = new ProcessStartInfo("dotnet") { RedirectStandardOutput = true, RedirectStandardError = true, WorkingDirectory = Root() };
        foreach (var a in new[] { "build", Path.Combine("game", "PointBlank.csproj"), "-c", configuration, "--disable-build-servers", "-nologo" })
        {
            start.ArgumentList.Add(a);
        }

        using var process = Process.Start(start)!;
        var stdout = process.StandardOutput.ReadToEndAsync();
        var stderr = process.StandardError.ReadToEndAsync();
        Assert.True(process.WaitForExit(180_000), "the build did not finish within three minutes");
        Assert.True(process.ExitCode == 0, stdout.Result + stderr.Result);
        return Path.Combine(Root(), "game", ".godot", "mono", "temp", "bin", configuration);
    }

    [Theory]
    [InlineData("ExportRelease", false)]
    [InlineData("ExportDebug", true)]
    public void The_truth_bridge_and_its_overlays_work_in_development_builds_only(string configuration, bool development)
    {
        var output = Build(configuration);
        var context = new AssemblyLoadContext(configuration, isCollectible: true);
        try
        {
            var sim = context.LoadFromAssemblyPath(Path.Combine(output, "Sim.dll"));
            var ui = context.LoadFromAssemblyPath(Path.Combine(output, "Presentation.dll"));
            context.Resolving += (_, name) => name.Name == "Sim" ? sim : null;
            Assert.NotSame(typeof(PlayerBattle).Assembly, sim);

            var battleType = sim.GetType("PointBlank.Sim.PlayerBattle", true)!;
            var battle = battleType.GetMethod("Create")!.Invoke(null, ["fix-assault", 1UL, true])!;
            var controller = ui.GetType("PointBlank.Presentation.GameController", true)!;
            dynamic game = Activator.CreateInstance(controller, battle, null)!;
            var overlayType = ui.GetType("PointBlank.Presentation.Overlay", true)!;
            for (var tick = 0; tick < 600; tick++)
            {
                game.Step();
            }

            var truth = battleType.GetMethod("DebugTruth")!;
            Assert.Equal(development, truth.Invoke(battle, null) != null);

            var palette = ui.GetType("PointBlank.Presentation.Palette", true)!;
            var prim = ui.GetType("PointBlank.Presentation.Prim", true)!.GetProperty("Colour")!;
            int Drawn(string name)
            {
                var colour = palette.GetField(name, BindingFlags.Public | BindingFlags.Static)!.GetValue(null);
                return ((IEnumerable)controller.GetMethod("Frame")!.Invoke((object)game, null)!).Cast<object>()
                    .Count(p => Equals(prim.GetValue(p), colour));
            }

            // The top strip's key help takes a colour while its overlay is on: that is the one thing drawn in a release.
            foreach (var name in new[] { "Values", "Truth", "Intent" })
            {
                controller.GetMethod("Toggle")!.Invoke((object)game, [Enum.Parse(overlayType, name)]);
                var drawn = Drawn(name);
                Assert.True(development ? drawn > 1 : drawn == 1, $"{configuration}, {name}: {drawn} shapes");
            }
        }
        finally
        {
            context.Unload();
        }
    }
}
