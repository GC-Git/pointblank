using PointBlank.Sim.Map;
using PointBlank.Sim.Morale;
using PointBlank.Sim.Org;
using PointBlank.Sim.Sight;

namespace PointBlank.Sim.Tests.UnitAi;

/// <summary>
/// A man of a routing squad who has nowhere left to run (he reached the end of his route while others of his squad are
/// still on theirs) gets down under fire and does not get up again while he waits.
/// </summary>
public class RoutingNoRouteTests
{
    [Fact]
    public void A_routing_man_with_nowhere_left_to_run_gets_down_under_fire_and_stays_down()
    {
        var waited = 0;
        var rises = 0;
        for (ulong seed = 1; seed <= 10; seed++)
        {
            var rig = AiRig.Create(seed, new GameMap("open", 200, 60), noCasualties: true);
            var de = rig.Org.SpawnSquad("de_gruppe", AiRig.M(150), AiRig.M(60), Posture.Prone);
            var us = rig.Org.SpawnSquad("us_rifle_squad", AiRig.M(260), AiRig.M(60), Posture.Kneeling);
            foreach (var s in us.Soldiers.Skip(1))
            {
                us.SetHealth(s.Id, HealthState.Dead); // one rifle: it keeps heads down without pinning
            }

            rig.Org.SpawnSquad("de_zugtrupp", AiRig.M(30), AiRig.M(60), Posture.Prone); // somewhere to run to
            var last = de.Soldiers[^1];
            last.X = AiRig.M(220); // far behind the others, so they finish their runs while he is still on his
            var morale = rig.World.GetSystem<MoraleSystem>();
            var still = de.Soldiers.ToDictionary(s => s.Id, _ => 0);
            var posture = de.Soldiers.ToDictionary(s => s.Id, s => s.Posture);
            for (var i = 0; i < 900; i++)
            {
                morale.SetLevel(de.Id, AiRig.M(5));
                rig.World.Step();
                foreach (var s in de.Soldiers)
                {
                    var stopped = rig.Move.IsMoving(de.Id) && !rig.Move.IsSoldierMoving(s.Id) && rig.Move.GoalOf(s.Id) == null;
                    still[s.Id] = stopped ? still[s.Id] + 1 : 0;
                    if (still[s.Id] > 8 * World.TicksPerSecond)
                    {
                        waited++;
                        rises += s.Posture == Posture.Standing && posture[s.Id] != Posture.Standing ? 1 : 0;
                    }

                    posture[s.Id] = s.Posture;
                }
            }
        }

        Assert.True(waited > 100, $"men waited with nowhere to run for only {waited} man-ticks");
        Assert.Equal(0, rises);
    }
}
