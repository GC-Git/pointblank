using System.Text;

namespace PointBlank.Sim.Tests.Determinism;

/// <summary>
/// Blanks the comments, string text and char literals of C# source, keeping line breaks and
/// the code inside interpolation holes.
/// </summary>
public static class CSharpCodeOnly
{
    public static string Strip(string source)
    {
        var output = new StringBuilder(source.Length);
        int position = 0;
        Code(source, ref position, output, closingBraces: 0);
        return output.ToString();
    }

    /// <summary>Copies code up to the end of the file or, inside an interpolation hole, up
    /// to and including its closing braces.</summary>
    private static void Code(string s, ref int i, StringBuilder output, int closingBraces)
    {
        int depth = 0;
        while (i < s.Length)
        {
            if (closingBraces > 0 && depth == 0 && s[i] == '}')
            {
                Blank(s, ref i, closingBraces, output);
                return;
            }

            if (StartsWith(s, i, "//"))
            {
                while (i < s.Length && s[i] != '\n')
                {
                    Blank(s, ref i, 1, output);
                }
            }
            else if (StartsWith(s, i, "/*"))
            {
                int end = s.IndexOf("*/", i + 2, StringComparison.Ordinal);
                Blank(s, ref i, (end < 0 ? s.Length : end + 2) - i, output);
            }
            else if (s[i] == '\'')
            {
                Quoted(s, ref i, output, verbatim: false, interpolated: false, quote: '\'');
            }
            else if (s[i] is '"' or '$' or '@')
            {
                Literal(s, ref i, output);
            }
            else
            {
                depth += s[i] == '{' ? 1 : s[i] == '}' ? -1 : 0;
                output.Append(s[i++]);
            }
        }
    }

    private static void Literal(string s, ref int i, StringBuilder output)
    {
        int dollars = 0;
        bool verbatim = false;
        int start = i;
        while (start < s.Length && s[start] is '$' or '@')
        {
            dollars += s[start] == '$' ? 1 : 0;
            verbatim |= s[start] == '@';
            start++;
        }

        if (start >= s.Length || s[start] != '"')
        {
            output.Append(s[i++]); // an @identifier, not a string
            return;
        }

        Blank(s, ref i, start - i, output);
        int quotes = Count(s, i, '"');
        if (quotes >= 3)
        {
            Raw(s, ref i, output, quotes, dollars);
        }
        else
        {
            Quoted(s, ref i, output, verbatim, interpolated: dollars > 0, quote: '"');
        }
    }

    private static void Quoted(
        string s, ref int i, StringBuilder output, bool verbatim, bool interpolated, char quote)
    {
        Blank(s, ref i, 1, output);
        while (i < s.Length)
        {
            if (!verbatim && s[i] == '\\')
            {
                Blank(s, ref i, 2, output);
            }
            else if (verbatim && s[i] == quote && Count(s, i, quote) >= 2)
            {
                Blank(s, ref i, 2, output);
            }
            else if (s[i] == quote)
            {
                Blank(s, ref i, 1, output);
                return;
            }
            else if (interpolated && (StartsWith(s, i, "{{") || StartsWith(s, i, "}}")))
            {
                Blank(s, ref i, 2, output);
            }
            else if (interpolated && s[i] == '{')
            {
                Blank(s, ref i, 1, output);
                Code(s, ref i, output, closingBraces: 1);
            }
            else
            {
                Blank(s, ref i, 1, output);
            }
        }
    }

    /// <summary>A raw string. With n dollar signs, a run of at least n braces opens a hole.</summary>
    private static void Raw(string s, ref int i, StringBuilder output, int quotes, int dollars)
    {
        Blank(s, ref i, quotes, output);
        while (i < s.Length)
        {
            if (Count(s, i, '"') >= quotes)
            {
                Blank(s, ref i, quotes, output);
                return;
            }

            int braces = Count(s, i, '{');
            if (dollars > 0 && braces >= dollars)
            {
                Blank(s, ref i, braces, output);
                Code(s, ref i, output, closingBraces: dollars);
            }
            else
            {
                Blank(s, ref i, Math.Max(braces, 1), output);
            }
        }
    }

    private static void Blank(string s, ref int i, int count, StringBuilder output)
    {
        for (int end = Math.Min(i + count, s.Length); i < end; i++)
        {
            output.Append(s[i] == '\n' ? '\n' : ' ');
        }
    }

    private static int Count(string s, int i, char c)
    {
        int n = 0;
        while (i + n < s.Length && s[i + n] == c)
        {
            n++;
        }

        return n;
    }

    private static bool StartsWith(string s, int i, string text) =>
        string.CompareOrdinal(s, i, text, 0, text.Length) == 0;
}
