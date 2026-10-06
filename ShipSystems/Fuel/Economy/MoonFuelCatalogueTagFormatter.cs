using System;
using System.Collections.Generic;

namespace Y4NGZCompany.ShipSystems.Fuel.Economy
{
    /// <summary>One moon's catalogue aliases and the fuel figure to print beside them.</summary>
    internal sealed class MoonFuelCatalogueTag
    {
        internal MoonFuelCatalogueTag(IEnumerable<string> aliases, string body)
        {
            Body = body?.Trim() ?? string.Empty;

            var unique = new List<string>();
            if (aliases != null)
            {
                foreach (string alias in aliases)
                {
                    if (string.IsNullOrWhiteSpace(alias))
                        continue;

                    string trimmed = alias.Trim();
                    if (trimmed.Length < 2)
                        continue;

                    bool seen = false;
                    for (int i = 0; i < unique.Count; i++)
                    {
                        if (string.Equals(unique[i], trimmed, StringComparison.OrdinalIgnoreCase))
                        {
                            seen = true;
                            break;
                        }
                    }

                    if (!seen)
                        unique.Add(trimmed);
                }
            }

            Aliases = unique;
        }

        internal string Body { get; }
        internal IReadOnlyList<string> Aliases { get; }
    }

    /// <summary>
    /// #679: appends a fuel figure to the moon rows of the vanilla terminal catalogue.
    ///
    /// <para>Rows are matched by moon NAME, never by index, for the same reason the contract
    /// catalogue formatter does it (#674): LethalLevelLoader, General Improvements, TerminalUtils
    /// and TerminalFormatter all reorder or filter this list, and an index match would put a
    /// price on the wrong moon. The bracketed tag and the vanilla-red hue are the conventions
    /// #674 shipped, so a fuel tag and a contract tag read as one family; vanilla's own weather
    /// note is parenthesised, which is why brackets stay distinguishable.</para>
    ///
    /// <para>The tag is appended at the END of the row rather than inserted after the moon name.
    /// The contract tag inserts immediately after the name, so end-of-row keeps the two from
    /// fighting for the same offset regardless of which module's postfix runs first, and leaves
    /// the moon name byte-for-byte intact for other terminal mods that re-parse it.</para>
    /// </summary>
    internal static class MoonFuelCatalogueTagFormatter
    {
        /// <summary>
        /// The vanilla report accent (#654/#674). It sits in the stock terminal's red family
        /// rather than beside the cyan contract tag, so price and contract never read as the
        /// same kind of annotation.
        /// </summary>
        internal const string FuelTagColor = "#FF5220";

        /// <summary>Printed instead of a number when fuel is not a factor for the moon.</summary>
        internal const string ExemptBody = "FUEL FREE";

        internal static string FormatFuelTag(string body)
        {
            return "<color=" + FuelTagColor + ">" + body + "</color>";
        }

        internal static string FormatFuelBody(float cost, bool isExempt)
        {
            if (isExempt)
                return ExemptBody;

            return "FUEL " + Y4NGZCompany.ShipSystems.Fuel.FuelPresentation.Amount(cost);
        }

        internal static string AddFuelTags(string text, IEnumerable<MoonFuelCatalogueTag> tags, bool detailRows = false)
        {
            if (string.IsNullOrEmpty(text) || tags == null)
                return text;

            text = TerminalCompatibility.MoonCatalogueColumns.RestoreFuel(
                TerminalCompatibility.MoonCatalogueColumns.RemoveLayout(text));
            List<AliasCandidate> candidates = BuildCandidates(tags);
            int NameEnd(string line)
            {
                int start = FindMoonNameStart(line);
                foreach (AliasCandidate candidate in candidates)
                    if (MatchesAtMoonName(line, start, candidate.Alias)) return start + candidate.Alias.Length;
                return -1;
            }
            if (detailRows || TerminalCompatibility.MoonCatalogueDetailRow.HasDetails(text)
                || TerminalCompatibility.MoonCatalogueColumns.RequiresDetails(text, NameEnd))
            {
                text = TerminalCompatibility.MoonCatalogueColumns.PromoteToDetails(text);
                return TerminalCompatibility.MoonCatalogueDetailRow.Annotate(text, line =>
                {
                    int start = FindMoonNameStart(line);
                    foreach (AliasCandidate candidate in candidates)
                        if (MatchesAtMoonName(line, start, candidate.Alias))
                            return candidate.Body.StartsWith("FUEL ", StringComparison.Ordinal)
                                ? candidate.Body.Substring(5) : candidate.Body;
                    return null;
                }, contract: false);
            }

            if (candidates.Count == 0)
                return text;

            string[] lines = text.Split('\n');
            bool changed = false;
            for (int i = 0; i < lines.Length; i++)
            {
                string line = lines[i];
                int moonNameStart = FindMoonNameStart(line);
                if (moonNameStart < 0)
                    continue;

                for (int c = 0; c < candidates.Count; c++)
                {
                    AliasCandidate candidate = candidates[c];
                    if (!MatchesAtMoonName(line, moonNameStart, candidate.Alias))
                        continue;

                    string ending = line.EndsWith("\r", StringComparison.Ordinal) ? "\r" : "";
                    lines[i] = TerminalCompatibility.MoonCatalogueColumns.RemoveFuel(line).TrimEnd()
                        + " " + FormatFuelTag(candidate.Body) + ending;
                    changed |= lines[i] != line;
                    break;
                }
            }

            return changed ? string.Join("\n", lines) : text;
        }

        private static List<AliasCandidate> BuildCandidates(IEnumerable<MoonFuelCatalogueTag> tags)
        {
            var candidates = new List<AliasCandidate>();
            var conflicting = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            foreach (MoonFuelCatalogueTag tag in tags)
            {
                if (tag == null || string.IsNullOrWhiteSpace(tag.Body))
                    continue;

                foreach (string alias in tag.Aliases)
                {
                    int existing = IndexOfAlias(candidates, alias);
                    if (existing < 0)
                    {
                        candidates.Add(new AliasCandidate(alias, tag.Body));
                        continue;
                    }

                    // A very large moon pack can ship two levels with the same display name.
                    // If their prices differ, showing nothing beats pricing the wrong moon.
                    if (!string.Equals(candidates[existing].Body, tag.Body, StringComparison.Ordinal))
                        conflicting.Add(alias);
                }
            }

            candidates.RemoveAll(candidate => conflicting.Contains(candidate.Alias));

            // Longest alias first, so "Experimentation" is tried before a pack's "Exper".
            candidates.Sort((left, right) =>
            {
                int byLength = right.Alias.Length.CompareTo(left.Alias.Length);
                return byLength != 0
                    ? byLength
                    : string.Compare(left.Alias, right.Alias, StringComparison.OrdinalIgnoreCase);
            });

            return candidates;
        }

        private static int IndexOfAlias(List<AliasCandidate> candidates, string alias)
        {
            for (int i = 0; i < candidates.Count; i++)
            {
                if (string.Equals(candidates[i].Alias, alias, StringComparison.OrdinalIgnoreCase))
                    return i;
            }

            return -1;
        }

        private static int FindMoonNameStart(string line)
        {
            if (string.IsNullOrWhiteSpace(line))
                return -1;

            int index = SkipWhitespaceAndRichTextTags(line, 0);
            if (index >= line.Length || line[index] != '*')
                return -1;

            index = SkipWhitespaceAndRichTextTags(line, index + 1);
            return index < line.Length ? index : -1;
        }

        private static int SkipWhitespaceAndRichTextTags(string line, int index)
        {
            while (index < line.Length)
            {
                while (index < line.Length && char.IsWhiteSpace(line[index]))
                    index++;

                if (index >= line.Length || line[index] != '<')
                    return index;

                int tagEnd = line.IndexOf('>', index + 1);
                if (tagEnd < 0)
                    return line.Length;

                index = tagEnd + 1;
            }

            return index;
        }

        private static bool MatchesAtMoonName(string line, int moonNameStart, string alias)
        {
            if (moonNameStart < 0 || moonNameStart + alias.Length > line.Length)
                return false;
            if (line.IndexOf(alias, moonNameStart, StringComparison.OrdinalIgnoreCase) != moonNameStart)
                return false;

            int end = moonNameStart + alias.Length;
            return end >= line.Length || !IsNameCharacter(line[end]);
        }

        private static bool IsNameCharacter(char value)
        {
            return char.IsLetterOrDigit(value) || value == '_' || value == '-';
        }

        private readonly struct AliasCandidate
        {
            internal AliasCandidate(string alias, string body)
            {
                Alias = alias;
                Body = body;
            }

            internal string Alias { get; }
            internal string Body { get; }
        }
    }
}
