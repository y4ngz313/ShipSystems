using System;
using System.Collections.Generic;
using System.IO;
using BepInEx;
using BepInEx.Bootstrap;
using Y4NGZCompany.Bootstrap;

namespace Y4NGZCompany.ShipSystems.Layout
{
    /// <summary>
    /// #683: the one place that answers "does this profile actually have constellations?"
    /// and "what does this constellation say about itself?".
    ///
    /// <para>Everything downstream of the fuel system groups moons into a constellation
    /// bucket whether or not a constellations mod is installed, because the entry-fuel
    /// arithmetic needs a group. That is a data model, and it must not decide what the
    /// player reads: on a profile with no constellations mod the vanilla route screens
    /// printed the bucket's synthetic key as though it were a constellation the player had.
    /// Presentation asks here first, and prints nothing when the answer is no.</para>
    ///
    /// <para>Detection is by plugin GUID through the BepInEx chainloader, the same soft
    /// dependency probe <c>FuelMonitorHost.ForeignMonitorWallPresent</c> uses. The
    /// description is read off the mod's own generated config rather than through its
    /// assembly, matching how <c>ShipFuelConstellationMap</c> already reads its moon map:
    /// the field that holds it (<c>ClassMapper.infoText</c>) is internal to
    /// LethalConstellations, so reflecting for it would bind us to its private shape.</para>
    /// </summary>
    internal static class ConstellationsModCompat
    {
        internal const string PluginGuid = "com.github.darmuh.LethalConstellations";

        private const string GeneratedConfigFileName = "LethalConstellations_Generated.cfg";
        private const string InfoTextKeySuffix = " infoText";

        private static bool _probed;
        private static bool _present;

        private static Dictionary<string, Description> _cachedDescriptions;
        private static DateTime _cachedWriteTimeUtc;

        /// <summary>
        /// True only when a constellations mod is loaded in this profile. Computed once:
        /// the chainloader's plugin set is final by the time any terminal screen is drawn.
        /// </summary>
        internal static bool Installed
        {
            get
            {
                if (_probed)
                    return _present;

                _probed = true;
                try
                {
                    _present = Chainloader.PluginInfos != null
                        && Chainloader.PluginInfos.ContainsKey(PluginGuid);
                }
                catch
                {
                    // A chainloader we cannot read is not evidence that the mod is there.
                    // Failing closed is the safe direction: no constellation text at all
                    // beats text naming a constellation the profile may not have.
                    _present = false;
                }

                return _present;
            }
        }

        /// <summary>
        /// True when this quote may show constellation text: the mod is installed and the
        /// quote carries a real constellation, not the ungrouped fuel bucket.
        /// </summary>
        internal static bool CanDisplay(string constellationName, string constellationKey)
        {
            return Installed
                && (!string.IsNullOrWhiteSpace(constellationName)
                    || !string.IsNullOrWhiteSpace(constellationKey));
        }

        /// <summary>
        /// The constellation's own description, as the player configured it. Empty when the
        /// mod is absent, when the constellation has no entry, or when its description is
        /// only the title line.
        /// </summary>
        internal static string GetDescription(string constellationName)
        {
            return Lookup(constellationName).Body;
        }

        /// <summary>
        /// The name the player wrote into the constellation's own title line, when it says
        /// something the constellation's code name does not. LethalConstellations names
        /// constellations by a short identifier ("GHK-CU") and lets the title read however
        /// the player likes ("Frontier"), and that title is often the only place a profile
        /// records what a constellation actually is -- so it is shown beside the identifier
        /// rather than thrown away with the rest of the heading.
        /// </summary>
        internal static string GetSubtitle(string constellationName)
        {
            Description entry = Lookup(constellationName);
            return string.Equals(entry.Subtitle, (constellationName ?? string.Empty).Trim(),
                       StringComparison.OrdinalIgnoreCase)
                ? string.Empty
                : entry.Subtitle;
        }

        private static Description Lookup(string constellationName)
        {
            if (!Installed || string.IsNullOrWhiteSpace(constellationName))
                return Description.Empty;

            Dictionary<string, Description> descriptions = LoadDescriptions();
            return descriptions.TryGetValue(constellationName.Trim(), out Description entry)
                ? entry
                : Description.Empty;
        }

        /// <summary>One constellation's configured info text, split into its title and its body.</summary>
        private struct Description
        {
            internal static readonly Description Empty = new Description(string.Empty, string.Empty);

            internal readonly string Subtitle;
            internal readonly string Body;

            internal Description(string subtitle, string body)
            {
                Subtitle = subtitle ?? string.Empty;
                Body = body ?? string.Empty;
            }
        }

        /// <summary>
        /// Parses <c>&lt;Name&gt; infoText = ...</c> out of the mod's generated config.
        /// The section header is keyed on a configurable word ("Constellation" by default),
        /// so the entry key is matched instead of the section: it carries the constellation
        /// name verbatim and does not move when that word is customised.
        /// </summary>
        private static Dictionary<string, Description> LoadDescriptions()
        {
            string path = Path.Combine(Paths.ConfigPath, GeneratedConfigFileName);
            DateTime writeTime = File.Exists(path) ? File.GetLastWriteTimeUtc(path) : DateTime.MinValue;
            if (_cachedDescriptions != null && writeTime == _cachedWriteTimeUtc)
                return _cachedDescriptions;

            var descriptions = new Dictionary<string, Description>(StringComparer.OrdinalIgnoreCase);
            try
            {
                if (File.Exists(path))
                {
                    foreach (string rawLine in File.ReadAllLines(path))
                    {
                        string line = rawLine.Trim();
                        if (line.Length == 0 || line[0] == '#' || line[0] == '[')
                            continue;

                        int equals = line.IndexOf('=');
                        if (equals < 0)
                            continue;

                        string key = line.Substring(0, equals).TrimEnd();
                        if (!key.EndsWith(InfoTextKeySuffix, StringComparison.Ordinal))
                            continue;

                        string name = key.Substring(0, key.Length - InfoTextKeySuffix.Length).Trim();
                        if (name.Length == 0)
                            continue;

                        Description entry = FormatDescription(line.Substring(equals + 1).Trim());
                        if (entry.Subtitle.Length > 0 || entry.Body.Length > 0)
                            descriptions[name] = entry;
                    }
                }
            }
            catch (Exception ex)
            {
                ShipSystemsBootstrap.Log?.LogWarning(
                    $"[ShipLayout] Failed to read constellation descriptions: {ex.Message}");
            }

            _cachedDescriptions = descriptions;
            _cachedWriteTimeUtc = writeTime;
            return descriptions;
        }

        /// <summary>
        /// Turns one stored config value into displayable prose: escapes become real breaks,
        /// and the mod's own title line is dropped because the screen already prints the
        /// constellation's identity on the line directly above this text.
        ///
        /// <para>The title is recognised structurally, not by name: the stock value opens with
        /// a "&lt;word&gt; - &lt;name&gt;" line followed by a paragraph break, and players rename both
        /// halves of it freely, so matching on the constellation's own name would miss every
        /// customised heading. A first line with no paragraph break after it is prose and is
        /// kept.</para>
        /// </summary>
        private static Description FormatDescription(string raw)
        {
            if (string.IsNullOrWhiteSpace(raw))
                return Description.Empty;

            string unescaped = raw
                .Replace("\\r\\n", "\n")
                .Replace("\\n", "\n")
                .Replace("\\r", "\n")
                .Replace("\\t", " ");

            string[] lines = unescaped.Split('\n');
            var kept = new List<string>(lines.Length);
            string subtitle = string.Empty;
            for (int i = 0; i < lines.Length; i++)
            {
                string trimmed = lines[i].Trim();
                if (trimmed.Length == 0)
                    continue;

                if (kept.Count == 0
                    && subtitle.Length == 0
                    && IsTitleLine(trimmed)
                    && HasLaterContent(lines, i)
                    && i + 1 < lines.Length
                    && lines[i + 1].Trim().Length == 0)
                {
                    // "<word> - <name>": the right-hand side is what the player called this
                    // constellation, and it is kept as a subtitle rather than discarded.
                    int separator = trimmed.IndexOf(" - ", StringComparison.Ordinal);
                    subtitle = trimmed.Substring(separator + 3).Trim();
                    continue;
                }

                kept.Add(trimmed);
            }

            return new Description(subtitle, string.Join(" ", kept.ToArray()).Trim());
        }

        private static bool IsTitleLine(string line)
        {
            return line.IndexOf(" - ", StringComparison.Ordinal) >= 0
                && line.IndexOf('.') < 0
                && line.Length <= 64;
        }

        private static bool HasLaterContent(string[] lines, int index)
        {
            for (int i = index + 1; i < lines.Length; i++)
            {
                if (lines[i].Trim().Length > 0)
                    return true;
            }

            return false;
        }
    }
}
