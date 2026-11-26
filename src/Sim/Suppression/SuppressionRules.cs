using PointBlank.Sim.Data;
using PointBlank.Sim.Fire;

namespace PointBlank.Sim.Suppression;

/// <summary>A soldier's or squad's suppression state. Values are hashed, so never renumber.</summary>
public enum SuppressionState : byte
{
    None = 0,

    /// <summary>At or above <see cref="SuppressionRules.SuppressedThreshold"/>: fires slower and wider, spots worse.</summary>
    Suppressed = 1,

    /// <summary>
    /// At or above <see cref="SuppressionRules.PinnedThreshold"/>, until below <see cref="SuppressionRules.PinnedRelease"/>:
    /// prone, barely fires, crawls; a pinned squad holds its advance orders.
    /// </summary>
    Pinned = 2,
}

/// <summary>
/// The numbers suppression uses, from the <c>suppression</c> table (entry <c>default</c>). A record, so
/// tests can vary one value with <c>with</c>. Levels run from 0 to <see cref="MaxLevel"/>.
/// <para>
/// A near miss at horizontal distance d (near-miss radius R) adds
/// <c>near_miss × calibre weight × (1 − distance_falloff × d / R)</c> to the man, and
/// <c>share_factor</c> of that to his able squad-mates within <c>share_radius</c>. A hit adds
/// <c>hit_nearby</c> to every able man of the hit man's side within <c>hit_radius</c> (the hit man
/// included). Decay is <c>decay_rate</c> per second, × <c>under_fire_decay_factor</c> while the man
/// had incoming fire within <c>under_fire_window</c>. Effects are linear in level/100.
/// </para>
/// </summary>
public sealed record SuppressionRules
{
    public static readonly Fixed MaxLevel = Fixed.FromInt(100);

    private const string EntryId = "default";

    private Fixed[] _calibre = new Fixed[3];

    private SuppressionRules()
    {
    }

    public Fixed NearMiss { get; init; }

    public Fixed DistanceFalloff { get; init; }

    public Fixed ShareFactor { get; init; }

    public Fixed ShareRadius { get; init; }

    public Fixed HitNearby { get; init; }

    public Fixed HitRadius { get; init; }

    /// <summary>Points lost per second.</summary>
    public Fixed DecayRate { get; init; }

    public Fixed UnderFireDecayFactor { get; init; }

    public int UnderFireWindowTicks { get; init; }

    public Fixed SuppressedThreshold { get; init; }

    public Fixed PinnedThreshold { get; init; }

    public Fixed PinnedRelease { get; init; }

    public Fixed DispersionMax { get; init; }

    public Fixed RateMin { get; init; }

    public Fixed RatePinned { get; init; }

    public Fixed ObserverMin { get; init; }

    public Fixed SpeedSuppressed { get; init; }

    public Fixed SpeedPinned { get; init; }

    public Fixed CalibreWeight(CalibreClass c) => _calibre[(int)c];

    /// <summary>A copy with one calibre weight changed.</summary>
    public SuppressionRules WithCalibre(CalibreClass c, Fixed weight)
    {
        var w = (Fixed[])_calibre.Clone();
        w[(int)c] = weight;
        return this with { _calibre = w };
    }

    /// <exception cref="DataException">The entry is missing or a value is out of range.</exception>
    public static SuppressionRules FromData(DataSet data)
    {
        var e = data.Table("suppression").Entries.FirstOrDefault(x => x.Id == EntryId)
            ?? throw new DataException($"suppression: no '{EntryId}' entry.");
        Fixed F(string name, Fixed max)
        {
            var v = e.Get(name).Number ?? throw new DataException($"suppression/{e.Id}/{name}: not a number.");
            return v < Fixed.Zero || v > max ? throw new DataException($"suppression/{e.Id}/{name}: {v} is out of range.") : v;
        }

        var big = Fixed.FromInt(1000);
        Fixed Ratio(string name) => F(name, Fixed.One);
        Fixed Level(string name) => F(name, MaxLevel);
        var rules = new SuppressionRules
        {
            NearMiss = Level("near_miss"),
            DistanceFalloff = Ratio("distance_falloff"),
            ShareFactor = Ratio("share_factor"),
            ShareRadius = F("share_radius", big),
            HitNearby = Level("hit_nearby"),
            HitRadius = F("hit_radius", big),
            DecayRate = F("decay_rate", big),
            UnderFireDecayFactor = Ratio("under_fire_decay_factor"),
            UnderFireWindowTicks = (F("under_fire_window", big) * Fixed.FromInt(World.TicksPerSecond)).ToInt(),
            SuppressedThreshold = Level("suppressed_threshold"),
            PinnedThreshold = Level("pinned_threshold"),
            PinnedRelease = Level("pinned_release"),
            DispersionMax = F("dispersion_max", big),
            RateMin = Ratio("rate_min"),
            RatePinned = Ratio("rate_pinned"),
            ObserverMin = Ratio("observer_min"),
            SpeedSuppressed = Ratio("speed_suppressed"),
            SpeedPinned = Ratio("speed_pinned"),
            _calibre = [F("calibre_pistol", big), F("calibre_rifle", big), F("calibre_heavy", big)],
        };
        if (rules.PinnedRelease > rules.PinnedThreshold || rules.SuppressedThreshold > rules.PinnedThreshold || rules.DispersionMax < Fixed.One)
        {
            throw new DataException("suppression/default: needs suppressed_threshold <= pinned_threshold, pinned_release <= pinned_threshold and dispersion_max >= 1.");
        }

        return rules;
    }
}
