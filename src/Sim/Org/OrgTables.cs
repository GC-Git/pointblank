using PointBlank.Sim.Data;

namespace PointBlank.Sim.Org;

public sealed record SlotTemplate(string Key, string TeamId, Role Role, string WeaponId, int Rank, int SuccessionOrder);

public sealed record SquadTemplate(
    string Id, string Name, string Side, int DelayTicks, Fixed LeaderBonus, IReadOnlyList<SlotTemplate> Slots,
    bool IsHq = false, bool HasRadio = false);

/// <summary>The squad structures from the <c>organisation</c> table, checked.</summary>
public sealed class OrgTables
{
    /// <summary>The role named by a data text (as in <c>organisation.json</c>), or null.</summary>
    internal static Role? RoleOf(string text) => Roles.Any(r => r.Text == text) ? Roles.First(r => r.Text == text).Role : null;

    private static readonly (string Text, Role Role)[] Roles =
    [
        ("squad_leader", Role.SquadLeader), ("assistant_leader", Role.AssistantLeader),
        ("rifleman", Role.Rifleman), ("bar_gunner", Role.BarGunner), ("bar_assistant", Role.BarAssistant),
        ("ammo_bearer", Role.AmmoBearer), ("mg_gunner", Role.MgGunner), ("mg_assistant", Role.MgAssistant),
        ("mg_loader", Role.MgLoader),
    ];

    private readonly Dictionary<string, SquadTemplate> _squads = new();

    private OrgTables()
    {
    }

    public IReadOnlyList<SquadTemplate> Squads { get; private set; } = [];

    public SquadTemplate Get(string id) =>
        _squads.TryGetValue(id, out var t) ? t : throw new ArgumentException($"No squad '{id}' in the organisation data.");

    /// <exception cref="DataException">A row is missing, malformed or inconsistent.</exception>
    public static OrgTables FromData(DataSet data)
    {
        var table = data.Table("organisation");
        var weapons = data.Table("weapons").Entries.Select(e => e.Id).ToHashSet();
        var tables = new OrgTables();
        var list = new List<SquadTemplate>();

        foreach (var row in table.Entries.Where(e => e.Get("kind").Text == "squad"))
        {
            var side = row.Get("side").Text;
            if (side is not ("us" or "de"))
            {
                throw new DataException($"organisation/{row.Id}: side must be 'us' or 'de', not '{side}'.");
            }

            var bonus = Number(row, "leader_bonus");
            if (bonus < Fixed.Zero || bonus > Fixed.One)
            {
                throw new DataException($"organisation/{row.Id}: leader_bonus must be between 0 and 1.");
            }

            var delay = Number(row, "succession_delay");
            if (delay < Fixed.Zero)
            {
                throw new DataException($"organisation/{row.Id}: succession_delay cannot be negative.");
            }

            var unitType = row.Values.TryGetValue("unit_type", out var ut) ? ut.Text : "squad";
            if (unitType is not ("squad" or "hq"))
            {
                throw new DataException($"organisation/{row.Id}: unit_type must be 'squad' or 'hq', not '{unitType}'.");
            }

            var radio = row.Values.ContainsKey("radio") ? Whole(row, "radio") : 0;
            if (radio is not (0 or 1))
            {
                throw new DataException($"organisation/{row.Id}: radio must be 0 or 1.");
            }

            long ticks = (delay.Raw * World.TicksPerSecond + Fixed.One.Raw - 1) / Fixed.One.Raw;
            var slots = new List<SlotTemplate>();
            foreach (var slot in table.Entries.Where(e => e.Get("kind").Text == "slot" && e.Get("squad").Text == row.Id))
            {
                var roleText = slot.Get("role").Text;
                var role = Roles.FirstOrDefault(r => r.Text == roleText);
                if (role.Text == null)
                {
                    throw new DataException($"organisation/{slot.Id}: unknown role '{roleText}'.");
                }

                var weapon = slot.Get("weapon").Text;
                if (!weapons.Contains(weapon))
                {
                    throw new DataException($"organisation/{slot.Id}: weapon '{weapon}' is not in weapons.json.");
                }

                slots.Add(new SlotTemplate(
                    slot.Id[(row.Id.Length + 1)..], slot.Get("team").Text, role.Role, weapon,
                    Whole(slot, "rank"), Whole(slot, "succession")));
            }

            Validate(row.Id, slots);
            var template = new SquadTemplate(
                row.Id, row.Get("name").Text, side, (int)ticks, bonus, slots, unitType == "hq", radio == 1);
            tables._squads[row.Id] = template;
            list.Add(template);
        }

        if (list.Count == 0)
        {
            throw new DataException("organisation: no squad rows.");
        }

        tables.Squads = list;
        return tables;
    }

    private static void Validate(string squad, List<SlotTemplate> slots)
    {
        var leaders = slots.Where(s => s.Role == Role.SquadLeader).ToList();
        if (leaders.Count != 1 || leaders[0].SuccessionOrder != 0)
        {
            throw new DataException($"organisation/{squad}: needs exactly one squad_leader (the leader), with succession 0.");
        }

        var others = slots.Where(s => s.Role != Role.SquadLeader).Select(s => s.SuccessionOrder).ToList();
        if (others.Any(o => o < 1) || others.Distinct().Count() != others.Count)
        {
            throw new DataException($"organisation/{squad}: succession numbers of the other soldiers must be unique and at least 1.");
        }
    }

    private static Fixed Number(DataEntry e, string name) =>
        e.Get(name).Number ?? throw new DataException($"organisation/{e.Id}/{name}: not a number.");

    private static int Whole(DataEntry e, string name)
    {
        var n = Number(e, name);
        return n.Raw % Fixed.One.Raw == 0 ? n.ToInt() : throw new DataException($"organisation/{e.Id}/{name}: not a whole number.");
    }
}
