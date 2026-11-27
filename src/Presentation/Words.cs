using System.Text;

namespace PointBlank.Presentation;

/// <summary>Plain words for the states the player reads: "MgGunner" becomes "MG gunner".</summary>
public static class Words
{
    /// <summary>
    /// A value's name in sentence case; several flags are joined with commas. <paramref name="value"/> must be a named
    /// value or a combination of named flags.
    /// </summary>
    public static string Of(Enum value)
    {
        var text = string.Join(", ", value.ToString().Split(", ").Select(Phrase));
        return char.ToUpperInvariant(text[0]) + text[1..];
    }

    /// <summary>A unit's data id as a name: "us_platoon_hq" becomes "US platoon HQ".</summary>
    public static string Unit(string id)
    {
        var german = id.StartsWith("de_", StringComparison.Ordinal);
        var words = id.Split('_').Select((w, i) => w switch
        {
            "us" => "US",
            "de" => "German",
            "hq" => "HQ",
            _ when german && i == 1 => char.ToUpperInvariant(w[0]) + w[1..], // German unit words are nouns
            _ => w,
        });
        return string.Join(' ', words);
    }

    private static string Phrase(string name) => name switch
    {
        "Ok" => "OK",
        "Incapacitated" => "down",
        "NotImplemented" => "not available yet",
        "OffMap" => "off the map",
        "InvalidRoe" => "invalid rules of engagement",
        "NotObserver" => "not an observer",
        "BarGunner" => "BAR gunner",
        "BarAssistant" => "BAR assistant",
        _ => Split(name),
    };

    private static string Split(string name)
    {
        var spaced = new StringBuilder();
        foreach (var letter in name)
        {
            if (char.IsUpper(letter) && spaced.Length > 0)
            {
                spaced.Append(' ');
            }

            spaced.Append(char.ToLowerInvariant(letter));
        }

        return string.Join(' ', spaced.ToString().Split(' ').Select(w => w switch { "mg" => "MG", "hq" => "HQ", _ => w }));
    }
}
