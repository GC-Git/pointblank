using System.Text.Json;

namespace PointBlank.Sim.Data;

/// <summary>
/// Reads the JSON data files (<c>data/&lt;table&gt;.json</c>). Rejects any value without a
/// valid source tag. Numbers go from decimal text to <see cref="Fixed"/> with integer
/// arithmetic only.
/// </summary>
public static class DataLoader
{
    /// <summary>The tables, in the fixed order used by reports.</summary>
    public static readonly string[] TableNames =
        ["units", "weapons", "ammo", "materials", "organisation", "terrain", "postures", "sight", "movement", "formation", "command", "spotting", "fire", "suppression", "morale", "unit_ai", "unit_ai_tasks", "artillery"];

    private const int MaxIntegerDigits = 9;
    private const int MaxFractionDigits = 9;

    private const string N = "n"; // numeric field: strict decimal, converted to Fixed
    private const string T = "t"; // text field: kept as written

    /// <summary>Required values per table, each with its declared type (n or t).</summary>
    private static readonly Dictionary<string, (string Name, string Type)[]> Required = new()
    {
        ["units"] = [("name", T), ("side", T), ("weapon", T), ("crew", N)],
        ["weapons"] =
        [
            ("name", T), ("ammo", T), ("muzzle_velocity", N), ("rate_of_fire", N),
            ("magazine_capacity", N), ("mass", N), ("dispersion", N), ("max_range", N), ("burst_length", N),
            ("burst_pause", N), ("reload_time", N), ("carried_rounds", N),
        ],
        ["ammo"] = [("caliber", N), ("bullet_mass", N), ("calibre_class", T)],
        ["materials"] = [("name", T), ("cover_protection", N), ("penetration_resistance", N)],
        ["organisation"] = [("name", T), ("kind", T)],
        ["terrain"] =
        [
            ("name", T), ("cover", N), ("concealment", N), ("move_cost_walk", N),
            ("move_cost_fast", N), ("move_cost_sneak", N), ("block_height", N), ("los_obscurement", N),
        ],
        ["postures"] = [("name", T), ("eye_height", N)],
        ["sight"] =
        [
            ("name", T), ("floor_height", N), ("block_threshold", N), ("hedge_far_range", N), ("hedge_near_range", N),
            ("hedge_near_obscurement", N), ("hedge_adjacent_range", N), ("hedge_adjacent_obscurement", N),
        ],
        ["movement"] = [("name", T), ("speed", N), ("posture", T), ("terrain_cost", T)],
        ["formation"] = [("name", T), ("spacing", N), ("lateral", N), ("leash", N), ("replan_ticks", N)],
        ["command"] =
        [
            ("name", T), ("base_delay", N), ("no_leader_delay", N), ("out_of_contact_delay", N), ("shaken_delay", N),
            ("broken_delay", N), ("spread_min", N), ("spread_max", N), ("command_radius", N), ("hesitate_chance", N),
            ("hesitate_delay", N), ("shaken_refuse_chance", N), ("broken_refuse_chance", N), ("low_morale_delay", N),
        ],
        ["spotting"] =
        [
            ("name", T), ("refresh_ticks", N), ("base_rate", N), ("near_range", N), ("max_range", N),
            ("posture_standing", N), ("posture_kneeling", N), ("posture_prone", N), ("motion_still", N),
            ("motion_sneak", N), ("motion_move", N), ("motion_move_fast", N), ("firing_factor", N),
            ("firing_window", N), ("lost_after", N), ("forget_after", N), ("hedge_position_distance", N), ("hedge_still_range", N),
            ("hedge_moving_range", N), ("close_range", N), ("close_rate", N),
        ],
        ["fire"] =
        [
            ("name", T), ("near_miss_radius", N), ("hedge_block_chance", N), ("body_width", N),
            ("body_height_standing", N), ("body_height_kneeling", N), ("body_height_prone", N),
            ("dispersion_standing", N), ("dispersion_kneeling", N), ("dispersion_prone", N), ("moving_factor", N),
            ("dispersion_per_100m", N), ("area_aim_height", N), ("kill_chance", N), ("incapacitate_chance", N), ("mask_clearance", N),
        ],
        ["suppression"] =
        [
            ("name", T), ("near_miss", N), ("calibre_pistol", N), ("calibre_rifle", N), ("calibre_heavy", N),
            ("distance_falloff", N), ("share_factor", N), ("share_radius", N), ("hit_nearby", N), ("hit_radius", N),
            ("decay_rate", N), ("under_fire_decay_factor", N), ("under_fire_window", N), ("suppressed_threshold", N),
            ("pinned_threshold", N), ("pinned_release", N), ("dispersion_max", N), ("rate_min", N), ("rate_pinned", N),
            ("observer_min", N), ("speed_suppressed", N), ("speed_pinned", N),
        ],
        ["morale"] =
        [
            ("name", T), ("start_level", N), ("suppression_drain", N), ("casualty_shock", N), ("shock_spread_min", N),
            ("shock_spread_max", N), ("leader_shock", N), ("support_shock_reduction", N), ("casualty_ceiling", N),
            ("leaderless_ceiling", N), ("isolated_ceiling", N), ("out_of_contact_ceiling", N), ("drift_rate", N),
            ("recovery_rate", N), ("leader_recovery_weight", N), ("support_recovery_bonus", N), ("hq_rally_bonus", N),
            ("support_radius", N), ("hq_rally_radius", N), ("loss_window", N), ("leader_killed_memory", N),
            ("heavy_casualty_fraction", N), ("shaken_below", N), ("shaken_release", N), ("broken_below", N),
            ("broken_release", N), ("rout_below", N), ("steady_above", N), ("rout_distance", N), ("rout_threat_radius", N),
        ],
        ["unit_ai"] =
        [
            ("name", T), ("reaction_time", N), ("under_fire_memory", N), ("distance_band", N), ("effective_range_factor", N),
            ("cover_min", N), ("cover_search_radius", N), ("line_spacing", N), ("line_reform_sectors", N), ("bound_distance", N),
            ("stall_time", N), ("rush_time", N), ("down_time", N), ("posture_open", T), ("posture_cover", T), ("posture_contact", T),
            ("defend_search_radius", N), ("fire_hold_suppression", N), ("open_delay", N), ("join_delay_min", N), ("join_delay_max", N),
            ("silent_chance", N), ("shaken_silent_chance", N), ("defend_open_range", N),
        ],
        ["unit_ai_tasks"] = [("name", T), ("role", T), ("target_kind", T), ("flank_offset", N), ("search_radius", N)],
        ["artillery"] =
        [
            ("name", T), ("side", T), ("missions", N), ("call_time", N), ("flight_time", N), ("guns", N), ("rounds_per_gun", N),
            ("duration", N), ("aim_error", N), ("scatter", N), ("lethal_radius", N), ("casualty_radius", N), ("lethal_chance", N),
            ("direct_hit_radius", N), ("kneeling_factor", N), ("prone_factor", N), ("shelter_factor", N), ("building_factor", N),
            ("cover_weight", N), ("kill_chance", N), ("incapacitate_chance", N), ("suppression", N), ("suppression_radius", N),
            ("morale_shock", N), ("morale_radius", N), ("impact_memory", N),
        ],
    };

    public static DataSet LoadDirectory(string directory)
    {
        var tables = new List<DataTable>();
        foreach (var name in TableNames)
        {
            var path = Path.Combine(directory, name + ".json");
            if (!File.Exists(path))
            {
                throw new DataException($"Data file not found: {path}");
            }

            try
            {
                tables.Add(ParseTable(File.ReadAllText(path), name));
            }
            catch (DataException e)
            {
                throw new DataException($"{name}.json: {e.Message}");
            }
        }

        return new DataSet(tables);
    }

    public static DataTable ParseTable(string json, string expectedTable)
    {
        if (!Required.TryGetValue(expectedTable, out var required))
        {
            throw new DataException($"Unknown table '{expectedTable}'.");
        }

        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(json);
        }
        catch (JsonException e)
        {
            throw new DataException($"{expectedTable}: invalid JSON: {e.Message}");
        }

        using (document)
        {
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object
                || !root.TryGetProperty("table", out var t)
                || t.ValueKind != JsonValueKind.String
                || t.GetString() != expectedTable)
            {
                throw new DataException($"Expected a file with \"table\": \"{expectedTable}\".");
            }

            if (!root.TryGetProperty("entries", out var entries)
                || entries.ValueKind != JsonValueKind.Array)
            {
                throw new DataException($"{expectedTable}: missing \"entries\" array.");
            }

            var ids = new HashSet<string>();
            var list = new List<DataEntry>();
            foreach (var e in entries.EnumerateArray())
            {
                var entry = ParseEntry(expectedTable, e, required);
                if (!ids.Add(entry.Id))
                {
                    throw new DataException($"{expectedTable}: duplicate id '{entry.Id}'.");
                }

                list.Add(entry);
            }

            return new DataTable(expectedTable, list);
        }
    }

    private static DataEntry ParseEntry(string table, JsonElement e, (string Name, string Type)[] required)
    {
        if (e.ValueKind != JsonValueKind.Object
            || !e.TryGetProperty("id", out var idElement)
            || idElement.ValueKind != JsonValueKind.String
            || string.IsNullOrWhiteSpace(idElement.GetString()))
        {
            throw new DataException($"{table}: every entry needs a non-empty string \"id\".");
        }

        var id = idElement.GetString()!;
        if (!e.TryGetProperty("values", out var values) || values.ValueKind != JsonValueKind.Object)
        {
            throw new DataException($"{table}/{id}: missing \"values\" object.");
        }

        var parsed = new SortedDictionary<string, DataValue>(StringComparer.Ordinal);
        foreach (var p in values.EnumerateObject())
        {
            var declared = required.FirstOrDefault(r => r.Name == p.Name).Type;
            parsed[p.Name] = ParseValue($"{table}/{id}/{p.Name}", p.Value, declared);
        }

        foreach (var (name, _) in required)
        {
            if (!parsed.ContainsKey(name))
            {
                throw new DataException($"{table}/{id}: missing required value '{name}'.");
            }
        }

        return new DataEntry(id, parsed);
    }

    /// <param name="declared">n, t, or null for a value the schema does not list (numeric unless its unit is "text").</param>
    private static DataValue ParseValue(string where, JsonElement v, string? declared)
    {
        if (v.ValueKind != JsonValueKind.Object)
        {
            throw new DataException($"{where}: a value must be an object with value, unit and source.");
        }

        if (!v.TryGetProperty("source", out var source))
        {
            throw new DataException($"{where}: no source tag.");
        }

        var parsedSource = ParseSource(where, source);

        if (!v.TryGetProperty("value", out var raw)
            || raw.ValueKind is not (JsonValueKind.String or JsonValueKind.Number))
        {
            throw new DataException($"{where}: \"value\" must be a string or a number.");
        }

        var text = raw.ValueKind == JsonValueKind.String ? raw.GetString()! : raw.GetRawText();
        if (!v.TryGetProperty("unit", out var unit)
            || unit.ValueKind != JsonValueKind.String
            || string.IsNullOrWhiteSpace(unit.GetString()))
        {
            throw new DataException($"{where}: \"unit\" is required (use \"text\" for non-numbers).");
        }

        var unitText = unit.GetString()!;
        var numeric = (declared ?? (unitText == "text" ? T : N)) == N;
        if (!numeric)
        {
            if (raw.ValueKind != JsonValueKind.String)
            {
                throw new DataException($"{where}: a text value must be a JSON string.");
            }

            return new DataValue(text, unitText, parsedSource, null);
        }

        if (!TryParseFixed(text, out var number))
        {
            throw new DataException(
                $"{where}: '{text}' is not a valid number (optional '-', digits, optional '.' and "
                + $"digits; at most {MaxIntegerDigits} integer and {MaxFractionDigits} fractional digits).");
        }

        return new DataValue(text, unitText, parsedSource, number);
    }

    private static DataSource ParseSource(string where, JsonElement s)
    {
        if (s.ValueKind != JsonValueKind.Object
            || !s.TryGetProperty("kind", out var kind)
            || kind.ValueKind != JsonValueKind.String)
        {
            throw new DataException($"{where}: source must be an object with a \"kind\".");
        }

        switch (kind.GetString())
        {
            case "reference":
                return new DataSource(DataSourceKind.Reference, Need(where, s, "ref"));
            case "estimated":
                return new DataSource(DataSourceKind.Estimated, Need(where, s, "reasoning"));
            case "placeholder":
                var note = s.TryGetProperty("note", out var n) && n.ValueKind == JsonValueKind.String
                    ? n.GetString()!
                    : string.Empty;
                return new DataSource(DataSourceKind.Placeholder, note);
            default:
                throw new DataException(
                    $"{where}: unknown source kind '{kind.GetString()}' "
                    + "(use reference, estimated or placeholder).");
        }
    }

    private static string Need(string where, JsonElement s, string name)
    {
        if (s.TryGetProperty(name, out var p)
            && p.ValueKind == JsonValueKind.String
            && !string.IsNullOrWhiteSpace(p.GetString()))
        {
            return p.GetString()!;
        }

        throw new DataException($"{where}: source needs a non-empty \"{name}\".");
    }

    /// <summary>
    /// Strictly parses an optionally negative plain decimal: digits, optionally a point and
    /// more digits. No plus sign, exponent, comma or whitespace; at most 9 integer and 9
    /// fractional digits (so the result always fits Q31.32). Integer arithmetic only.
    /// Rounding rule: the value is divided as an exact ratio and the result is truncated
    /// toward zero to the nearest Q31.32 step (2^-32), so 0.1 becomes 429496729/2^32 and
    /// -0.1 becomes -429496729/2^32. Never rounds away from zero.
    /// </summary>
    public static bool TryParseFixed(string text, out Fixed value)
    {
        value = default;
        var s = text.AsSpan();
        var negative = s.Length > 0 && s[0] == '-';
        if (negative)
        {
            s = s[1..];
        }

        var point = s.IndexOf('.');
        var whole = point < 0 ? s : s[..point];
        var fraction = point < 0 ? default : s[(point + 1)..];
        if (whole.Length is 0 or > MaxIntegerDigits
            || (point >= 0 && (fraction.Length == 0 || fraction.Length > MaxFractionDigits)))
        {
            return false;
        }

        long scaled = 0;
        foreach (var c in whole)
        {
            if (c is < '0' or > '9')
            {
                return false;
            }

            scaled = (scaled * 10) + (c - '0');
        }

        long denominator = 1;
        foreach (var c in fraction)
        {
            if (c is < '0' or > '9')
            {
                return false;
            }

            scaled = (scaled * 10) + (c - '0');
            denominator *= 10;
        }

        value = Fixed.FromRatio(negative ? -scaled : scaled, denominator);
        return true;
    }
}
