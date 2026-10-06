using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using BepInEx;
using BepInEx.Configuration;
using BepInEx.Logging;
using Y4NGZCore.Configuration;

namespace Y4NGZCompany.ShipSystems
{
    /// <summary>
    /// Imports the fuel and power/battery settings that existed before the DLL split.
    /// #666: the terminal, shop, ship-upgrade and monitor-row sections migrated to
    /// Y4NGZTerminalUpgrades' own migration with the systems that bind them.
    /// </summary>
    internal static class ShipSystemsConfigMigration
    {
        internal const string DestinationFileName = "com.y4ngz.company.shipsystems.cfg";

        internal const string BatterySection = "Battery";

        /// <summary>
        /// #684: the section names this plugin binds today. The pre-#684 spellings carried a
        /// "78 - Ship Systems - " prefix that only repeated the plugin header a config manager
        /// already draws above them.
        /// </summary>
        private static readonly string[] FixedSections =
        {
            "Shared",
            "Power",
            "Fuel",
            "Fuel Monitor",
            // #679: generated at launch from the installed moon list, not bound in BindConfig.
            // They are listed here so a one-time import from a pre-split file carries a host's
            // hand-edited prices across the split instead of regenerating tier defaults.
            "Fuel Moon Prices",
            "Fuel Constellations",
            "Fuel Sources",
            "Fuel Loot",
            BatterySection
        };

        /// <summary>
        /// #684: every pre-#684 section header and the header that replaced it.
        ///
        /// <para>This drives two different jobs. <see cref="RenameLegacySections"/> rewrites these
        /// headers in place inside this plugin's OWN config file, and <see cref="MapLegacy"/> uses
        /// the same pairs so a one-time import out of a pre-split monolith - which still spells the
        /// sections the old way - lands on the current names.</para>
        ///
        /// <para>Only Ship Systems' own sections appear here. <c>78 - Ship Systems - Monitor Row</c>
        /// and <c>78 - Ship Systems - Monitor Row Content</c> are deliberately absent: those belong
        /// to Y4NGZTerminalUpgrades, which keeps the legacy spelling and imports them by that name.
        /// Renaming them here would move a section out from under its owner.</para>
        /// </summary>
        private static readonly KeyValuePair<string, string>[] RenamedSections =
        {
            Renamed("78 - Ship Systems - Shared", "Shared"),
            Renamed("78 - Ship Systems - Power", "Power"),
            Renamed("78 - Ship Systems - Fuel", "Fuel"),
            Renamed("78 - Ship Systems - Fuel Monitor", "Fuel Monitor"),
            Renamed("78 - Ship Systems - Fuel Moon Prices", "Fuel Moon Prices"),
            Renamed("78 - Ship Systems - Fuel Constellations", "Fuel Constellations"),
            Renamed("78 - Ship Systems - Fuel Sources", "Fuel Sources"),
            Renamed("78 - Ship Systems - Fuel Loot", "Fuel Loot"),
            Renamed("78 - Ship Systems - Battery", BatterySection)
        };

        internal static void MigrateConfigSafely(ConfigFile config, ManualLogSource log)
        {
            // Order matters. The rename runs first so the one-time legacy import below sees the
            // player's existing values under their CURRENT names and lets them win at key
            // granularity, exactly as it would for a config that never carried the old spelling.
            RenameLegacySectionsSafely(config, log);

            SplitConfigImporter.ImportOnceSafely(
                config,
                log,
                "Y4NGZShipSystems",
                DestinationFileName,
                new SplitConfigImportSource(
                    "com.y4ngz.company.cfg",
                    MapLegacy,
                    IsPotentiallyOwnedLegacySection),
                new SplitConfigImportSource(
                    "com.y4ngz.lgushipsystems.cfg",
                    MapLegacy,
                    IsPotentiallyOwnedLegacySection));
            MigratePlayerSchema(config, log);
        }

        private static void MigratePlayerSchema(ConfigFile config, ManualLogSource log)
        {
            try
            {
                string path = config.ConfigFilePath;
                if (!File.Exists(path)) return;
                string original = File.ReadAllText(path);
                string terminalPath = Path.Combine(Path.GetDirectoryName(path), "com.y4ngz.company.terminalupgrades.cfg");
                bool terminalMigrated = File.Exists(terminalPath) &&
                    System.Text.RegularExpressions.Regex.IsMatch(File.ReadAllText(terminalPath), @"(?m)^LegacyConfigImportVersion\s*=\s*[1-9][0-9]*\s*$");
                string rewritten = ShipSystemsConfigSchema.Rewrite(original, terminalMigrated);
                if (rewritten == original) return;
                string backup = path + ".pre-player-config-v2.bak";
                if (!File.Exists(backup)) File.Copy(path, backup);
                WriteAtomic(path, rewritten);
                config.Reload();
                log?.LogInfo("Ship Systems config updated to player-readable names; existing values preserved and old file backed up.");
            }
            catch (Exception ex) { log?.LogWarning("Ship Systems config migration failed: " + ex.GetType().Name); }
        }

        private static SplitConfigTarget MapLegacy(string section, string key)
        {
            string destination = ResolveSection(section);
            if (destination == null)
                return null;

            // The retired battery keys must be dropped whichever spelling they arrive under.
            // Since #684 the destination name and the pre-split source name are the same string
            // ("Battery"), so this can no longer be a case inside the legacy switch alone.
            if (string.Equals(destination, BatterySection, StringComparison.Ordinal)
                && IsRetiredBatteryKey(key))
            {
                return null;
            }

            return new SplitConfigTarget(destination, key);
        }

        /// <summary>
        /// Maps any spelling this plugin has ever owned onto its current section name, or null
        /// when the section is not ours.
        /// </summary>
        private static string ResolveSection(string section)
        {
            if (IsOwnedSection(section))
                return section;

            foreach (KeyValuePair<string, string> rename in RenamedSections)
            {
                if (string.Equals(section, rename.Key, StringComparison.Ordinal))
                    return rename.Value;
            }

            // Pre-split, per-feature plugin files used bare feature names. Every one of those but
            // "General" now collides with - and therefore resolves through - IsOwnedSection above.
            return string.Equals(section, "General", StringComparison.Ordinal)
                ? "Shared"
                : null;
        }

        private static bool IsOwnedSection(string section)
        {
            foreach (string fixedSection in FixedSections)
            {
                if (string.Equals(section, fixedSection, StringComparison.Ordinal))
                    return true;
            }

            return false;
        }

        private static bool IsPotentiallyOwnedLegacySection(string section)
        {
            return ResolveSection(section) != null;
        }

        private static bool IsRetiredBatteryKey(string key)
        {
            return key == "Restore Hold Seconds"
                || key == "Sabotage Minimum Fulfilled Quota"
                || key == "Sabotage Seconds"
                || key == "Sabotage Seconds With Plating";
        }

        private static KeyValuePair<string, string> Renamed(string legacy, string current)
        {
            return new KeyValuePair<string, string>(legacy, current);
        }

        private static void RenameLegacySectionsSafely(ConfigFile config, ManualLogSource log)
        {
            try
            {
                RenameLegacySections(config, log);
            }
            catch (Exception ex)
            {
                // System.IO messages routinely contain the player's absolute profile path; the
                // exception type is enough to diagnose and keeps machine-local detail out of logs.
                log?.LogError(
                    $"Y4NGZShipSystems config section rename did not finish ({ex.GetType().Name}); "
                    + "startup continues on the existing config file, which is unchanged.");
            }
        }

        /// <summary>
        /// #684: rewrites the pre-#684 section headers inside this plugin's own config file.
        ///
        /// <para><b>Why whole sections and not a key list.</b> Two of the renamed sections are
        /// GENERATED - <c>Fuel Moon Prices</c> and <c>Fuel Constellations</c> get one entry per
        /// moon and per constellation the profile happens to have installed, so their keys are
        /// unknowable at compile time. A key-by-key remap would silently lose every per-moon price
        /// a host had edited. Moving the header carries all of them, known and generated alike.</para>
        ///
        /// <para><b>Why it cannot duplicate or resurrect anything.</b> The rewrite is destructive
        /// in the file: the old header is gone once it is written, so a second launch finds nothing
        /// to rename and writes nothing at all - idempotence comes from the file's own content, not
        /// from a version stamp that a config reset could drop. When a section already exists under
        /// BOTH spellings, the current-name value wins per key and the legacy duplicate is dropped
        /// rather than appended, so a stale edit can never overwrite a live one. And the config is
        /// reloaded immediately after the write, so BepInEx's orphan set is rebuilt from the
        /// renamed file - without that reload BepInEx would still be holding the old entries and
        /// would write them straight back out on the next Save().</para>
        /// </summary>
        private static void RenameLegacySections(ConfigFile config, ManualLogSource log)
        {
            string path = Path.Combine(Paths.ConfigPath, DestinationFileName);
            if (!File.Exists(path))
                return;

            string original = File.ReadAllText(path);
            if (!ContainsAnyLegacySection(original))
                return;

            if (!TryRewriteSections(original, out string rewritten, out int renamedSections, out int mergedKeys))
            {
                log?.LogWarning(
                    "Y4NGZShipSystems found a malformed line in its config file and left the "
                    + "pre-#684 section names in place rather than risk rewriting it.");
                return;
            }

            WriteAtomic(path, rewritten);
            config?.Reload();
            log?.LogInfo(
                $"Y4NGZShipSystems config section rename: sections={renamedSections}, "
                + $"droppedStaleDuplicateKeys={mergedKeys}. Every value, including the generated "
                + "per-moon and per-constellation prices, moved with its section.");
        }

        private static bool ContainsAnyLegacySection(string text)
        {
            foreach (string rawLine in SplitLines(text))
            {
                if (TryReadSection(rawLine.Trim(), out string section) && IsLegacySection(section))
                    return true;
            }

            return false;
        }

        private static bool IsLegacySection(string section)
        {
            foreach (KeyValuePair<string, string> rename in RenamedSections)
            {
                if (string.Equals(section, rename.Key, StringComparison.Ordinal))
                    return true;
            }

            return false;
        }

        private static string RenameSection(string section)
        {
            foreach (KeyValuePair<string, string> rename in RenamedSections)
            {
                if (string.Equals(section, rename.Key, StringComparison.Ordinal))
                    return rename.Value;
            }

            return section;
        }

        private static bool TryRewriteSections(
            string text,
            out string rewritten,
            out int renamedSections,
            out int droppedKeys)
        {
            rewritten = null;
            renamedSections = 0;
            droppedKeys = 0;

            string newline = text.IndexOf("\r\n", StringComparison.Ordinal) >= 0
                ? "\r\n"
                : Environment.NewLine;

            var preamble = new List<string>();
            var order = new List<string>();
            var groups = new Dictionary<string, SectionGroup>(StringComparer.Ordinal);
            SectionGroup current = null;
            bool currentOpenedItsGroup = false;

            foreach (string rawLine in SplitLines(text))
            {
                string trimmed = rawLine.Trim();
                if (TryReadSection(trimmed, out string section))
                {
                    bool legacy = IsLegacySection(section);
                    if (legacy)
                        renamedSections++;

                    string name = RenameSection(section);
                    currentOpenedItsGroup = !groups.TryGetValue(name, out current);
                    if (currentOpenedItsGroup)
                    {
                        current = new SectionGroup(name);
                        groups[name] = current;
                        order.Add(name);
                    }

                    current.OwnedByLegacy = legacy;
                    continue;
                }

                // A line that opens like a header but is not one would silently change which
                // section the following keys belong to. Refuse the whole rewrite instead.
                if (trimmed.Length > 0 && trimmed[0] == '[')
                    return false;

                if (current == null)
                {
                    preamble.Add(rawLine);
                    continue;
                }

                if (TryReadKey(trimmed, out string key))
                {
                    droppedKeys += current.AddKeyLine(key, rawLine, current.OwnedByLegacy) ? 0 : 1;
                    continue;
                }

                // Comments and blank lines from a merged duplicate section are dropped; keeping
                // them would stack two copies of the same BepInEx description block.
                if (currentOpenedItsGroup)
                    current.AddPlainLine(rawLine);
            }

            var builder = new StringBuilder();
            foreach (string line in preamble)
                builder.Append(line).Append(newline);

            foreach (string name in order)
            {
                SectionGroup group = groups[name];
                builder.Append('[').Append(name).Append(']').Append(newline);
                foreach (string line in group.Lines)
                    builder.Append(line).Append(newline);
            }

            rewritten = builder.ToString();
            return true;
        }

        private static void WriteAtomic(string path, string text)
        {
            string directory = Path.GetDirectoryName(path);
            Directory.CreateDirectory(directory);
            string tempPath = Path.Combine(
                directory,
                Path.GetFileName(path) + "." + Guid.NewGuid().ToString("N") + ".tmp");

            try
            {
                File.WriteAllText(tempPath, text, new UTF8Encoding(false));
                if (File.Exists(path))
                    File.Replace(tempPath, path, null);
                else
                    File.Move(tempPath, path);
            }
            finally
            {
                if (File.Exists(tempPath))
                    File.Delete(tempPath);
            }
        }

        private static bool TryReadSection(string line, out string section)
        {
            if (line.Length >= 2 && line[0] == '[' && line[line.Length - 1] == ']')
            {
                section = line.Substring(1, line.Length - 2).Trim();
                return section.Length > 0;
            }

            section = null;
            return false;
        }

        private static bool TryReadKey(string line, out string key)
        {
            if (line.Length > 0 && line[0] != '#' && line[0] != ';')
            {
                int separator = line.IndexOf('=');
                if (separator > 0)
                {
                    key = line.Substring(0, separator).Trim();
                    if (key.Length > 0)
                        return true;
                }
            }

            key = null;
            return false;
        }

        private static IEnumerable<string> SplitLines(string text)
        {
            return (text ?? string.Empty).Split(new[] { "\r\n", "\n", "\r" }, StringSplitOptions.None);
        }

        /// <summary>
        /// One output section. Duplicate keys collapse here: the first writer wins unless it wrote
        /// under a legacy header and the newcomer writes under the current one, which is what makes
        /// "the value you can see in the config manager today wins" true regardless of file order.
        /// </summary>
        private sealed class SectionGroup
        {
            private readonly Dictionary<string, int> _keyLines = new Dictionary<string, int>(StringComparer.Ordinal);
            private readonly HashSet<string> _legacyKeys = new HashSet<string>(StringComparer.Ordinal);

            internal SectionGroup(string name)
            {
                Name = name;
            }

            internal string Name { get; }
            internal bool OwnedByLegacy { get; set; }
            internal List<string> Lines { get; } = new List<string>();

            /// <summary>Returns false when the incoming line was dropped as a stale duplicate.</summary>
            internal bool AddKeyLine(string key, string rawLine, bool fromLegacySection)
            {
                if (!_keyLines.TryGetValue(key, out int index))
                {
                    _keyLines[key] = Lines.Count;
                    if (fromLegacySection)
                        _legacyKeys.Add(key);
                    Lines.Add(rawLine);
                    return true;
                }

                if (!fromLegacySection && _legacyKeys.Remove(key))
                {
                    Lines[index] = rawLine;
                    return true;
                }

                return false;
            }

            internal void AddPlainLine(string rawLine)
            {
                Lines.Add(rawLine);
            }
        }
    }
}
