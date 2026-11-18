using System.Text;
using System.Text.RegularExpressions;

namespace Lanyard.Application.Services.BookingImport;

// Tidies the free-text party type Just Step stores so the same thing doesn't come in under several
// spellings. Two layers:
//  1. Mechanical (automatic, for every type): trims, strips a trailing time range
//     ("... 6.30pm - 8.30pm"), drops trailing punctuation, collapses whitespace, and applies a
//     gentle title case - so "Play Party.", "play party" and "PLAY PARTY" all become "Play Party".
//  2. Aliases (explicit): the same product written in genuinely different words only merges when
//     listed here. Edit Groups to merge, split or rename types; the matching ignores case, spaces,
//     punctuation, "&"/"and" and the "Lazer"/"Laser" spelling, so you don't have to list every
//     cosmetic variant - just the real wording differences.
public static partial class PartyTypeNormalizer
{
    // Canonical name -> the other wordings that mean the same thing. The canonical is matched too,
    // so it needn't be repeated. A booking's type becomes the canonical when its (loosely matched)
    // text is the canonical or any of its variants.
    private static readonly (string Canonical, string[] Variants)[] Groups =
    [
        // Groupings confirmed by Ben. Most of the stray names are just different wording for the
        // same party or private hire. The matching is loose (case, spaces, punctuation, "&"/"and",
        // "Lazer"/"Laser"), so each variant below only needs listing once.
        ("Play Party", ["Adventure Play Party"]),
        ("Play and Laser Party", ["Adventure Play and Laser Party"]),
        ("Play & Inflatable Exclusive Hire", ["Play and Inflatable Private Hire", "Play and Inflatable Party"]),
        ("Play, Inflatable & Laser Tag Exclusive Hire", ["Lazer, Play and Inflatable Private Hire", "Lazer, Play and Inflatable Party"]),
        ("1 Hour Laser Tag Private Hire", ["1 Hour Lazer Tag Private Hire & Soft Play"]),
    ];

    private static readonly Dictionary<string, string> AliasByKey = BuildAliasIndex();

    private static readonly HashSet<string> SmallWords =
        ["and", "or", "the", "a", "an", "of", "to", "in", "on", "at", "for", "with", "vs", "&"];

    public static string Normalize(string? raw)
    {
        string cleaned = Clean(raw);

        if (cleaned.Length == 0)
        {
            return cleaned;
        }

        return AliasByKey.TryGetValue(Key(cleaned), out string? canonical) ? canonical : TitleCase(cleaned);
    }

    // Trim, strip a trailing time range (unless that's all there is), collapse whitespace, drop
    // trailing punctuation.
    private static string Clean(string? raw)
    {
        string value = raw?.Trim() ?? string.Empty;

        if (value.Length == 0)
        {
            return string.Empty;
        }

        string withoutTime = TrailingTimeRange().Replace(value, string.Empty).Trim();
        string text = withoutTime.Length == 0 ? value : withoutTime; // keep time-only names rather than blanking them

        text = Whitespace().Replace(text, " ").Trim();
        text = text.TrimEnd('.', ',', ';', '-', '–', ' ');

        return text.Trim();
    }

    // A loose key for matching: lower-case, "&"->"and", "lazer"->"laser", letters and digits only.
    private static string Key(string cleaned)
    {
        string lowered = cleaned.ToLowerInvariant().Replace("&", "and").Replace("lazer", "laser");
        return NonAlphanumeric().Replace(lowered, string.Empty);
    }

    private static string TitleCase(string text)
    {
        string[] words = text.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        StringBuilder result = new(text.Length);

        for (int i = 0; i < words.Length; i++)
        {
            string word = words[i];

            if (i > 0)
            {
                result.Append(' ');
            }

            // Small words stay lower, except the first word of the name.
            if (i > 0 && SmallWords.Contains(word.ToLowerInvariant()))
            {
                result.Append(word.ToLowerInvariant());
            }
            else
            {
                result.Append(Capitalise(word));
            }
        }

        return result.ToString();
    }

    // Lower-cases the word and upper-cases its first character when that is a letter. Words that
    // start with a digit or symbol (e.g. a time like "6.30pm") are left lower, so they aren't mangled.
    private static string Capitalise(string word)
    {
        if (word.Length == 0)
        {
            return word;
        }

        string lower = word.ToLowerInvariant();
        return char.IsLetter(lower[0]) ? char.ToUpperInvariant(lower[0]) + lower[1..] : lower;
    }

    private static Dictionary<string, string> BuildAliasIndex()
    {
        Dictionary<string, string> index = new();

        foreach ((string canonical, string[] variants) in Groups)
        {
            index[Key(Clean(canonical))] = canonical;

            foreach (string variant in variants)
            {
                index[Key(Clean(variant))] = canonical;
            }
        }

        return index;
    }

    [GeneratedRegex(@"\s*\d{1,2}[:.]\d{2}\s*(am|pm)?\s*(-|–|to)\s*\d{1,2}[:.]\d{2}\s*(am|pm)?\s*$", RegexOptions.IgnoreCase)]
    private static partial Regex TrailingTimeRange();

    [GeneratedRegex(@"\s+")]
    private static partial Regex Whitespace();

    [GeneratedRegex("[^a-z0-9]")]
    private static partial Regex NonAlphanumeric();
}
