using PointBlank.Sim.Data;

namespace PointBlank.Sim.Orders;

/// <summary>The chain-of-command numbers, from the <c>command</c> table (entry <c>chain_of_command</c>).</summary>
public sealed class CommandRules
{
    private const string EntryId = "chain_of_command";
    private static readonly Fixed MaxSeconds = Fixed.FromInt(3600);

    private CommandRules()
    {
    }

    /// <summary>Delay in seconds for a steady squad with its leader, in contact with its HQ.</summary>
    public Fixed BaseDelay { get; private init; }

    public Fixed NoLeaderDelay { get; private init; }

    public Fixed OutOfContactDelay { get; private init; }

    public Fixed ShakenDelay { get; private init; }

    public Fixed BrokenDelay { get; private init; }

    /// <summary>The delay is multiplied by a random factor between these.</summary>
    public Fixed SpreadMin { get; private init; }

    public Fixed SpreadMax { get; private init; }

    /// <summary>Metres within which an HQ of the squad's side puts it in contact.</summary>
    public Fixed CommandRadius { get; private init; }

    public Fixed HesitateChance { get; private init; }

    public int HesitateTicks { get; private init; }

    public Fixed ShakenRefuseChance { get; private init; }

    public Fixed BrokenRefuseChance { get; private init; }

    /// <exception cref="DataException">The entry is missing or a value is out of range.</exception>
    public static CommandRules FromData(DataSet data)
    {
        var e = data.Table("command").Entries.FirstOrDefault(x => x.Id == EntryId)
            ?? throw new DataException($"command: no '{EntryId}' entry.");
        Fixed Seconds(string name) => Range(e, name, Fixed.Zero, MaxSeconds);
        Fixed Chance(string name) => Range(e, name, Fixed.Zero, Fixed.One);

        var rules = new CommandRules
        {
            BaseDelay = Seconds("base_delay"),
            NoLeaderDelay = Seconds("no_leader_delay"),
            OutOfContactDelay = Seconds("out_of_contact_delay"),
            ShakenDelay = Seconds("shaken_delay"),
            BrokenDelay = Seconds("broken_delay"),
            SpreadMin = Range(e, "spread_min", Fixed.Zero, Fixed.FromInt(4)),
            SpreadMax = Range(e, "spread_max", Fixed.Zero, Fixed.FromInt(4)),
            CommandRadius = Range(e, "command_radius", Fixed.Zero, Fixed.FromInt(10_000)),
            HesitateChance = Chance("hesitate_chance"),
            HesitateTicks = ToTicks(Seconds("hesitate_delay")),
            ShakenRefuseChance = Chance("shaken_refuse_chance"),
            BrokenRefuseChance = Chance("broken_refuse_chance"),
        };
        if (rules.SpreadMin > rules.SpreadMax)
        {
            throw new DataException($"command/{EntryId}: spread_min cannot exceed spread_max.");
        }

        return rules;
    }

    /// <summary>The delay in seconds before the random spread.</summary>
    public Fixed DelaySeconds(DelayReason reasons)
    {
        var s = BaseDelay;
        s += reasons.HasFlag(DelayReason.NoLeader) ? NoLeaderDelay : Fixed.Zero;
        s += reasons.HasFlag(DelayReason.OutOfContact) ? OutOfContactDelay : Fixed.Zero;
        s += reasons.HasFlag(DelayReason.Shaken) ? ShakenDelay : Fixed.Zero;
        s += reasons.HasFlag(DelayReason.Broken) ? BrokenDelay : Fixed.Zero;
        return s;
    }

    /// <summary>Rolls the delay in ticks: the seconds times a spread factor, rounded up. Draws once from the RNG.</summary>
    public int RollDelayTicks(DelayReason reasons, SimRng rng)
    {
        var spread = SpreadMin + ((SpreadMax - SpreadMin) * rng.NextFixed());
        return ToTicks(DelaySeconds(reasons) * spread);
    }

    /// <summary>Seconds to ticks, rounded up.</summary>
    public static int ToTicks(Fixed seconds) =>
        (int)(((seconds.Raw * World.TicksPerSecond) + Fixed.One.Raw - 1) / Fixed.One.Raw);

    private static Fixed Range(DataEntry e, string name, Fixed min, Fixed max)
    {
        var v = e.Get(name).Number ?? throw new DataException($"command/{e.Id}/{name}: not a number.");
        return v >= min && v <= max ? v : throw new DataException($"command/{e.Id}/{name}: must be between {min} and {max}.");
    }
}
