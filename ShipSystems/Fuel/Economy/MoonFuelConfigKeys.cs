using System;
using System.Text;

namespace Y4NGZCompany.ShipSystems.Fuel.Economy
{
    /// <summary>
    /// #679: key hygiene for the generated per-moon and per-constellation fuel price
    /// entries. Two different jobs live here on purpose:
    ///
    /// <para><see cref="Sanitize"/> produces the text that goes into the config FILE. BepInEx
    /// rejects a <c>ConfigDefinition</c> whose section or key contains any of
    /// <c>= \n \t \\ " ' [ ]</c>, and silently trims leading/trailing whitespace, so a modded
    /// moon called <c>Galetry [Hard]</c> would throw on bind. The sanitized form is stable for
    /// a given moon name, so the entry a player edits is the entry that comes back next
    /// session.</para>
    ///
    /// <para><see cref="NormalizeLookup"/> produces the in-memory MATCHING key. It is
    /// deliberately identical in behaviour to <c>ShipFuelConfig.NormalizeKey</c> (lower-case,
    /// letters and digits only) so a moon resolves the same way here, in the constellation
    /// map, and in the emergency-moon list. It is duplicated rather than delegated to keep
    /// this file free of the Unity and BepInEx references the regression suites cannot load.
    /// </para>
    /// </summary>
    internal static class MoonFuelConfigKeys
    {
        /// <summary>Substituted for every character BepInEx forbids in a section or key.</summary>
        internal const char ReplacementCharacter = '_';

        /// <summary>Key used when a moon or constellation name is empty after sanitising.</summary>
        internal const string UnnamedKey = "Unnamed";

        private static readonly char[] ForbiddenCharacters = { '=', '\n', '\t', '\\', '"', '\'', '[', ']' };

        /// <summary>
        /// BepInEx-safe rendering of <paramref name="value"/> for use as a config key. Control
        /// characters collapse to a space so a stray newline cannot split one entry into two.
        /// </summary>
        internal static string Sanitize(string value)
        {
            if (string.IsNullOrWhiteSpace(value))
                return UnnamedKey;

            var builder = new StringBuilder(value.Length);
            foreach (char character in value)
            {
                if (IsForbidden(character))
                {
                    builder.Append(ReplacementCharacter);
                    continue;
                }

                if (char.IsControl(character))
                {
                    builder.Append(' ');
                    continue;
                }

                builder.Append(character);
            }

            string sanitized = builder.ToString().Trim();
            return sanitized.Length == 0 ? UnnamedKey : sanitized;
        }

        /// <summary>
        /// Case- and punctuation-insensitive matching key. <c>"41 Experimentation"</c>,
        /// <c>"Experimentation"</c> and <c>"experimentation "</c> do not all normalise to the
        /// same value - the leading route number survives, exactly as it does in
        /// <c>ShipFuelConfig.NormalizeKey</c> - so callers that need the bare planet name
        /// should pass it through <see cref="StripRoutePrefix"/> first.
        /// </summary>
        internal static string NormalizeLookup(string value)
        {
            if (string.IsNullOrWhiteSpace(value))
                return string.Empty;

            var builder = new StringBuilder(value.Length);
            foreach (char character in value)
            {
                if (char.IsLetterOrDigit(character))
                    builder.Append(char.ToLowerInvariant(character));
            }

            return builder.ToString();
        }

        /// <summary>
        /// Drops the numeric route prefix vanilla puts on a planet name ("41 Experimentation").
        /// Modded moons from LethalLevelLoader carry the same shape, so this is the form that
        /// matches what the terminal prints and what a player would type into the blacklist.
        /// </summary>
        internal static string StripRoutePrefix(string planetName)
        {
            if (string.IsNullOrWhiteSpace(planetName))
                return string.Empty;

            string trimmed = planetName.Trim();
            int firstSpace = trimmed.IndexOf(' ');
            if (firstSpace <= 0)
                return trimmed;

            for (int i = 0; i < firstSpace; i++)
            {
                if (!char.IsDigit(trimmed[i]))
                    return trimmed;
            }

            string remainder = trimmed.Substring(firstSpace + 1).Trim();
            return remainder.Length == 0 ? trimmed : remainder;
        }

        private static bool IsForbidden(char character)
        {
            for (int i = 0; i < ForbiddenCharacters.Length; i++)
            {
                if (ForbiddenCharacters[i] == character)
                    return true;
            }

            return false;
        }

        /// <summary>
        /// True when <paramref name="candidate"/> names <paramref name="moonName"/>: an exact
        /// normalised match, or either name containing the other. The containment arm is what
        /// lets a player write "Experimentation" and have it match the catalogue's
        /// "41 Experimentation", and mirrors how the constellation map already matches moons.
        /// </summary>
        internal static bool NameMatches(string moonName, string candidate)
        {
            string moon = NormalizeLookup(moonName);
            string other = NormalizeLookup(candidate);
            if (moon.Length == 0 || other.Length == 0)
                return false;
            if (string.Equals(moon, other, StringComparison.Ordinal))
                return true;

            return moon.Contains(other) || other.Contains(moon);
        }
    }
}
