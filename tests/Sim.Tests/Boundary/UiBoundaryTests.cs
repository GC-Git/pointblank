using System.Reflection;
using System.Reflection.Metadata;
using System.Reflection.PortableExecutable;
using System.Runtime.CompilerServices;

namespace PointBlank.Sim.Tests.Boundary;

/// <summary>
/// "The UI reads only player-side knowledge" (item 2.5, CLAUDE.md rule 11). Reads the type references of the built UI
/// assemblies: they may name only the Sim types listed here, none of which hands out the truth. A new type must be added
/// on purpose.
/// </summary>
public class UiBoundaryTests
{
    /// <summary>The Sim types UI code may name: the player's battle and view, the view's copies, and plain values.</summary>
    private static readonly HashSet<string> Allowed =
    [
        "PointBlank.Sim.Fixed",
        "PointBlank.Sim.PlayerBattle",
        "PointBlank.Sim.SideView",
        "PointBlank.Sim.SquadView",
        "PointBlank.Sim.SoldierView",
        "PointBlank.Sim.IncomingFireView",
        "PointBlank.Sim.MapView",
        "PointBlank.Sim.MapTileView",
        "PointBlank.Sim.BuildingView",
        "PointBlank.Sim.Map.TerrainType",
        "PointBlank.Sim.Map.TilePos",
        "PointBlank.Sim.Map.Opening",
        "PointBlank.Sim.Map.OpeningKind",
        "PointBlank.Sim.Map.WallSide",
        "PointBlank.Sim.Sight.Posture",
        "PointBlank.Sim.Org.Role",
        "PointBlank.Sim.Org.HealthState",
        "PointBlank.Sim.Org.MoraleState",
        "PointBlank.Sim.Morale.MoraleReason",
        "PointBlank.Sim.Suppression.SuppressionState",
        "PointBlank.Sim.Fire.AmmoState",
        "PointBlank.Sim.Spotting.ContactView",
        "PointBlank.Sim.Spotting.ContactKind",
        "PointBlank.Sim.Spotting.ContactQuality",
        "PointBlank.Sim.Orders.Order",
        "PointBlank.Sim.Orders.OrderKind",
        "PointBlank.Sim.Orders.OrderView",
        "PointBlank.Sim.Orders.OrderStatus",
        "PointBlank.Sim.Orders.OrderNote",
        "PointBlank.Sim.Orders.DelayReason",
        "PointBlank.Sim.Orders.RoeMode",
        "PointBlank.Sim.Orders.IssueResult",
        "PointBlank.Sim.Orders.IssueRejection",
        "PointBlank.Sim.Artillery.FireMissionView",
        "PointBlank.Sim.Artillery.MissionStatus",
        "PointBlank.Sim.Artillery.ShellImpactView",

        // Debug overlays only (item 2.6): Only_the_truth_overlays_call_for_debug_truth holds the truth to one method.
        "PointBlank.Sim.Spotting.SpottingDebug",
        "PointBlank.Sim.Spotting.TruthSoldier",
        "PointBlank.Sim.Spotting.TruthSquad",
        "PointBlank.Sim.Spotting.IntentKind",
    ];

    /// <summary>Namespaces (by prefix) whose types look members up by name at run time.</summary>
    private static readonly string[] ByNameNamespaces =
    [
        "System.Reflection.", "System.Linq.Expressions.", "System.Linq.IQueryable", "System.Dynamic.", "Microsoft.CSharp.",
        "System.Runtime.Loader.", "System.ComponentModel.", "System.Text.Json.", "System.Xml.Serialization.",
    ];

    /// <summary>Types from those namespaces that Godot's generated glue names and that look nothing up.</summary>
    private static readonly HashSet<string> Harmless = ["System.Reflection.Assembly", "System.ComponentModel.EditorBrowsableState"];

    private static readonly HashSet<string> ByNameTypes =
    [
        "System.Activator", "System.Runtime.CompilerServices.UnsafeAccessorAttribute",
        "System.Runtime.CompilerServices.CallSite", "System.Runtime.CompilerServices.CallSiteBinder",
    ];

    private static string Root([CallerFilePath] string here = "") =>
        Path.GetFullPath(Path.Combine(Path.GetDirectoryName(here)!, "..", "..", ".."));

    private static string Configuration =>
        typeof(UiBoundaryTests).Assembly.GetCustomAttribute<AssemblyConfigurationAttribute>()?.Configuration ?? "Debug";

    public static TheoryData<string> UiAssemblies => new() { "presentation", "game" };

    private static string PathOf(string which) => which == "game"
        ? Path.Combine(Root(), "game", ".godot", "mono", "temp", "bin", Configuration, "PointBlank.dll")
        : Path.Combine(Root(), "src", "Presentation", "bin", Configuration, "net8.0", "Presentation.dll");

    [Theory]
    [MemberData(nameof(UiAssemblies))]
    public void UI_code_names_only_player_side_sim_types_and_nothing_that_reaches_members_by_name(string which)
    {
        using var pe = new PEReader(File.OpenRead(PathOf(which)));
        var md = pe.GetMetadataReader();
        var offenders = new List<string>();
        var simTypes = 0;
        foreach (var handle in md.TypeReferences)
        {
            var type = md.GetTypeReference(handle);
            var ns = md.GetString(type.Namespace);
            var name = $"{ns}.{md.GetString(type.Name)}";
            var scope = type.ResolutionScope;
            while (scope.Kind == HandleKind.TypeReference)
            {
                var outer = md.GetTypeReference((TypeReferenceHandle)scope);
                name = $"{md.GetString(outer.Namespace)}.{md.GetString(outer.Name)}+{md.GetString(type.Name)}";
                scope = outer.ResolutionScope;
            }

            if (scope.Kind == HandleKind.AssemblyReference
                && md.GetString(md.GetAssemblyReference((AssemblyReferenceHandle)scope).Name) == "Sim")
            {
                simTypes++;
                if (!Allowed.Contains(name))
                {
                    offenders.Add(name);
                }
            }

            // Reflection, expression trees, dynamic binding and runtime loading all reach private members by name, behind
            // the compiler's back (Expression.Property(battle, "World") hands out the whole truth).
            // Godot's generated glue names the type Assembly; calling anything on it is refused below.
            var outerName = name.Split('+')[0];
            var byName = ByNameNamespaces.Any(n => outerName.StartsWith(n, StringComparison.Ordinal))
                && !Harmless.Contains(name) && !name.EndsWith("Attribute", StringComparison.Ordinal);
            if (byName || ByNameTypes.Contains(name.Split('`')[0]))
            {
                offenders.Add(name);
            }
        }

        foreach (var handle in md.MemberReferences)
        {
            var member = md.GetMemberReference(handle);
            if (member.Parent.Kind != HandleKind.TypeReference)
            {
                continue;
            }

            var parent = md.GetTypeReference((TypeReferenceHandle)member.Parent);
            var owner = $"{md.GetString(parent.Namespace)}.{md.GetString(parent.Name)}";
            var called = md.GetString(member.Name);
            if (owner == "System.Reflection.Assembly"
                || (owner == "System.Delegate" && called is "CreateDelegate" or "DynamicInvoke")
                || (owner == "System.Type" && called is not ("GetTypeFromHandle" or "op_Equality" or "op_Inequality" or "get_Assembly")))
            {
                offenders.Add($"{owner}.{called}");
            }
        }

        Assert.True(simTypes > 0, $"{which} names no Sim type: is this the right file?");
        Assert.Empty(offenders);
    }

    [Theory]
    [MemberData(nameof(UiAssemblies))]
    public void Only_the_truth_overlays_call_for_debug_truth(string which)
    {
        using var pe = new PEReader(File.OpenRead(PathOf(which)));
        var md = pe.GetMetadataReader();
        var truth = md.MemberReferences
            .Where(h => md.GetString(md.GetMemberReference(h).Name) == "DebugTruth")
            .Select(h => System.Reflection.Metadata.Ecma335.MetadataTokens.GetToken(h))
            .ToHashSet();
        var callers = new SortedSet<string>(StringComparer.Ordinal);
        foreach (var handle in md.MethodDefinitions)
        {
            var method = md.GetMethodDefinition(handle);
            if (method.RelativeVirtualAddress == 0)
            {
                continue;
            }

            var il = pe.GetMethodBody(method.RelativeVirtualAddress).GetILBytes()!;
            for (var i = 0; i + 4 < il.Length; i++)
            {
                // call, callvirt and ldtoken; ldftn and ldvirtftn (0x06 and 0x07 after the 0xFE prefix).
                var calls = il[i] is 0x28 or 0x6F or 0xD0 || (i > 0 && il[i - 1] == 0xFE && il[i] is 0x06 or 0x07);
                if (calls && truth.Contains(BitConverter.ToInt32(il, i + 1)))
                {
                    var type = md.GetTypeDefinition(method.GetDeclaringType());
                    callers.Add($"{md.GetString(type.Namespace)}.{md.GetString(type.Name)}.{md.GetString(method.Name)}");
                }
            }
        }

        // The game only paints; in Presentation one method of one class asks for the truth.
        string[] allowed = which == "game" ? [] : ["PointBlank.Presentation.TruthOverlays.Draw"];
        Assert.Equal(allowed, callers.ToArray());
    }

    [Fact]
    public void The_truth_overlays_hand_no_truth_to_other_code()
    {
        var type = typeof(PointBlank.Presentation.GameController).Assembly.GetType("PointBlank.Presentation.TruthOverlays", true)!;
        const BindingFlags all = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static | BindingFlags.Instance | BindingFlags.DeclaredOnly;

        Assert.Empty(type.GetFields(all));
        Assert.Empty(type.GetProperties(all));
        Assert.All(type.GetMethods(all).Where(m => !m.IsPrivate && !m.Name.Contains('<')), m => Assert.Equal(("Draw", typeof(void)), (m.Name, m.ReturnType)));
    }

    [Fact]
    public void Only_the_tests_and_the_headless_runner_see_sim_internals()
    {
        var friends = typeof(World).Assembly.GetCustomAttributes<InternalsVisibleToAttribute>().Select(a => a.AssemblyName);

        Assert.Equal(["Sim.Tests", "runner"], friends.Order(StringComparer.Ordinal));
    }

    [Fact]
    public void The_player_battle_hands_out_nothing_but_the_players_view_and_the_debug_overlays_truth()
    {
        var members = typeof(PlayerBattle).GetMembers(BindingFlags.Public | BindingFlags.Instance | BindingFlags.Static | BindingFlags.DeclaredOnly)
            .Select(m => m.Name).Where(n => !n.StartsWith("get_", StringComparison.Ordinal)).Order(StringComparer.Ordinal);

        Assert.Equal(["Create", "DebugTruth", "PlayerSide", "ScenarioId", "ScenarioIds", "Seed", "Step", "View"], members);
        Assert.Equal("us", PlayerBattle.Create("fix-assault", 1).View.Side);
    }
}
