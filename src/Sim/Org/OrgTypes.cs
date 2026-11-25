using PointBlank.Sim.Sight;

namespace PointBlank.Sim.Org;

/// <summary>A soldier's job in his squad. Values are hashed, so never renumber.</summary>
public enum Role : byte
{
    SquadLeader = 0,
    AssistantLeader = 1,
    Rifleman = 2,
    BarGunner = 3,
    BarAssistant = 4,
    AmmoBearer = 5,
    MgGunner = 6,
    MgAssistant = 7,
    MgLoader = 8,
}

/// <summary>Wounded soldiers still fight and lead; incapacitated and dead ones do neither.</summary>
public enum HealthState : byte
{
    Ok = 0,
    Wounded = 1,
    Incapacitated = 2,
    Dead = 3,
}

public sealed class Soldier
{
    internal Soldier(int id, string key, Role role, int rank, int successionOrder, string weaponId, int teamIndex)
    {
        Id = id;
        Key = key;
        Role = role;
        Rank = rank;
        SuccessionOrder = successionOrder;
        WeaponId = weaponId;
        TeamIndex = teamIndex;
    }

    /// <summary>Unique in the world, assigned in spawn order.</summary>
    public int Id { get; }

    /// <summary>The slot's name in the organisation table, such as <c>asl</c>.</summary>
    public string Key { get; }

    public Role Role { get; }

    /// <summary>Seniority; higher is more senior.</summary>
    public int Rank { get; }

    /// <summary>The place in the line of succession; the lowest eligible number leads next.</summary>
    public int SuccessionOrder { get; }

    /// <summary>An id in <c>weapons.json</c>.</summary>
    public string WeaponId { get; }

    public int TeamIndex { get; }

    /// <summary>Metres from the map's north-west corner (x east, y south).</summary>
    public Fixed X { get; set; }

    public Fixed Y { get; set; }

    public Posture Posture { get; set; }

    public HealthState Health { get; internal set; }

    public bool CanLead => Health is HealthState.Ok or HealthState.Wounded;
}

public sealed class Team(string id, IReadOnlyList<Soldier> members)
{
    public string Id { get; } = id;

    /// <summary>In slot order; the first is the team's lead.</summary>
    public IReadOnlyList<Soldier> Members { get; } = members;
}

/// <summary>
/// A squad: soldiers grouped in teams, with one leader. When the leader falls the squad has
/// no effective leader for <see cref="SuccessionDelayTicks"/>, then the next eligible soldier
/// in the line of succession takes over.
/// </summary>
public sealed class Squad
{
    private readonly Fixed _leaderBonus;

    internal Squad(
        int id, string orgId, string side, IReadOnlyList<Soldier> soldiers, IReadOnlyList<Team> teams,
        int delayTicks, Fixed leaderBonus, Soldier leader)
    {
        Id = id;
        OrgId = orgId;
        Side = side;
        Soldiers = soldiers;
        Teams = teams;
        SuccessionDelayTicks = delayTicks;
        _leaderBonus = leaderBonus;
        Leader = leader;
    }

    public int Id { get; }

    public string OrgId { get; }

    public string Side { get; }

    public IReadOnlyList<Soldier> Soldiers { get; }

    public IReadOnlyList<Team> Teams { get; }

    public int SuccessionDelayTicks { get; }

    /// <summary>The soldier in command, or null while the post is vacant.</summary>
    public Soldier? Leader { get; private set; }

    public bool HasEffectiveLeader => Leader != null;

    /// <summary>
    /// What a leader adds to the squad (0 to 1, from data), and zero while there is none. Later
    /// morale (1.5) and order (2.2) code reads it; nothing applies it yet.
    /// </summary>
    public Fixed LeaderBonus => HasEffectiveLeader ? _leaderBonus : Fixed.Zero;

    /// <summary>Ticks until the next leader takes over, 0 when none is pending.</summary>
    public int TicksToSuccession { get; private set; }

    /// <summary>
    /// Sets a soldier's health. Command is never handed back: once a leader has gone down (dead or
    /// incapacitated) the post is vacant and passes down the line of succession, so a soldier
    /// restored to Ok or Wounded afterwards stays a follower. A restored former leader (succession
    /// order 0) is not in the line and never leads again; a restored deputy can lead only if the
    /// post is vacant again later.
    /// </summary>
    public void SetHealth(int soldierId, HealthState health)
    {
        var soldier = Soldiers.FirstOrDefault(s => s.Id == soldierId)
            ?? throw new ArgumentException($"Soldier {soldierId} is not in squad {Id}.");
        soldier.Health = health;
        CheckLeader();
    }

    internal void Tick()
    {
        CheckLeader();
        if (TicksToSuccession > 0 && --TicksToSuccession == 0)
        {
            // Chosen now, so a candidate who fell during the delay is skipped.
            Leader = NextInSuccession();
        }
    }

    private void CheckLeader()
    {
        if (Leader is { CanLead: false })
        {
            Leader = null;
        }

        if (Leader == null && TicksToSuccession == 0 && NextInSuccession() != null)
        {
            TicksToSuccession = Math.Max(1, SuccessionDelayTicks);
        }
    }

    private Soldier? NextInSuccession()
    {
        Soldier? best = null;
        foreach (var s in Soldiers)
        {
            if (s.CanLead && s.SuccessionOrder > 0 && (best == null || s.SuccessionOrder < best.SuccessionOrder))
            {
                best = s;
            }
        }

        return best;
    }
}
