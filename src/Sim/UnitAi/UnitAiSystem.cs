using PointBlank.Sim.Fire;
using PointBlank.Sim.Map;
using PointBlank.Sim.Movement;
using PointBlank.Sim.Orders;
using PointBlank.Sim.Org;
using PointBlank.Sim.Sight;

namespace PointBlank.Sim.UnitAi;

/// <summary>
/// The unit AI (item 2.3): every tick, for each side in turn (in order of their first squad), the same
/// <see cref="UnitAiBrain"/> thinks for that side's squads. Its only input is that side's <see cref="SideView"/>
/// (fog of war, CLAUDE.md rule 11), and its outputs go through an effects object bound to that side, which throws on
/// any unit of another side. Effects use the internal paths orders use: posture, <see cref="MovementSystem.MoveSoldier"/>,
/// <see cref="MovementSystem.SetHeldTeams"/> and <see cref="FireSystem.SetAiFire"/>.
/// <para>Order and AI priority: a player order always wins. A player Fire order replaces AI fire while it runs, and the
/// AI never starts, stops or replaces a move. Within an order the squad still reacts: it keeps returning fire during a
/// move or a Hold, takes cover when stopped, held or pinned, and shapes a Move or Sneak in contact into fire and
/// movement with rushes under fire. A MoveFast (also FallBack and a routing flee) is left a straight dash.</para>
/// Register after <see cref="Spotting.SpottingSystem"/> and before <see cref="FireSystem"/>. Movement runs earlier in the
/// tick, so the AI's moves start on the next tick. Needs a <see cref="CommandSystem"/> in the world (ROE and order status
/// through the view).
/// </summary>
public sealed class UnitAiSystem : ISimSystem
{
    private readonly OrgSystem _org;
    private readonly MovementSystem _move;
    private readonly FireSystem _fire;
    private readonly UnitAiBrain _brain;
    private readonly List<string> _sides = [];

    public UnitAiSystem(GameMap map, OrgSystem org, MovementSystem move, FireSystem fire, CommandSystem command, UnitAiRules rules)
    {
        ArgumentNullException.ThrowIfNull(command);
        _org = org;
        _move = move;
        _fire = fire;
        Rules = rules;
        _brain = new UnitAiBrain(map, move.Rules, fire.Rules, rules);
    }

    public UnitAiRules Rules { get; }

    public void Tick(World world)
    {
        _sides.Clear();
        foreach (var q in _org.Squads)
        {
            if (!_sides.Contains(q.Side))
            {
                _sides.Add(q.Side);
            }
        }

        foreach (var side in _sides)
        {
            var effects = new Effects(world, this, side);
            _brain.Think(world.ViewFor(side), effects);
            effects.ApplyPostures();
        }
    }

    public void AppendState(StateHasher hasher) => _brain.AppendState(hasher);

    /// <summary>The brain's hands, for one side only.</summary>
    private sealed class Effects(World world, UnitAiSystem ai, string side) : IAiEffects
    {
        private readonly SortedDictionary<int, (Posture Posture, bool Timed, bool Wait)> _postures = [];

        /// <summary>
        /// Hands each man's last decision of this tick to <see cref="PostureChange"/>: with his own reaction delay, or at
        /// once when the brain has already waited it (<c>timed</c>).
        /// </summary>
        public void ApplyPostures()
        {
            foreach (var (id, p) in _postures)
            {
                var s = ai._org.FindSoldier(id)!;
                if (s.CanLead)
                {
                    PostureChange.Request(s, p.Posture, world.Tick, p.Timed ? 0 : ai.Rules.AiPostureDelayTicks, p.Wait);
                }
            }
        }

        public void SetPosture(int soldierId, Posture posture, bool timed = false, bool wait = false)
        {
            if (ai._org.FindSquadOf(soldierId) is not { } q || q.Side != side)
            {
                throw new InvalidOperationException($"Soldier {soldierId} is not on side {side}.");
            }

            _postures[soldierId] = (posture, timed, wait);
        }

        public bool MoveSoldier(int squadId, int soldierId, TilePos goal, bool fast) =>
            Own(squadId).Soldiers.Any(s => s.Id == soldierId)
            && ai._move.MoveSoldier(squadId, soldierId, goal, fast ? MoveMode.MoveFast : MoveMode.Move);

        public void SetHeldTeams(int squadId, int teamMask, bool rush) => ai._move.SetHeldTeams(Own(squadId).Id, teamMask, rush);

        public void SetHeldSoldier(int squadId, int soldierId, bool held)
        {
            if (Own(squadId).Soldiers.Any(s => s.Id == soldierId))
            {
                ai._move.SetHeldSoldier(soldierId, held);
            }
        }

        public void SetFire(int squadId, int targetId, Fixed x, Fixed y, int teamMask) =>
            ai._fire.SetAiFire(world, Own(squadId), targetId, x, y, teamMask);

        public void StopFire(int squadId) => ai._fire.StopAiFire(Own(squadId).Id);

        public void SetSilent(int squadId, int soldierId, bool silent)
        {
            var q = Own(squadId);
            if (q.Soldiers.Any(s => s.Id == soldierId))
            {
                ai._fire.SetAiSilent(soldierId, silent);
            }
        }

        private Squad Own(int squadId) =>
            ai._org.Squads.FirstOrDefault(q => q.Id == squadId && q.Side == side)
            ?? throw new InvalidOperationException($"Squad {squadId} is not on side {side}.");
    }
}
