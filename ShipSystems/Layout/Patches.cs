using System;
using System.Collections.Generic;
using GameNetcodeStuff;
using HarmonyLib;
using TMPro;
using UnityEngine;
using Y4NGZCompany.Bootstrap;
using Y4NGZCompany.ShipSystems.Fuel;
using Y4NGZCompany.ShipSystems.Fuel.Economy;
using Y4NGZCompany.ShipSystems.Power;

namespace Y4NGZCompany.ShipSystems.Layout
{
    internal static class Patches
    {
        // Harmony sorts every postfix on a method by priority, across owners, and only falls
        // back to registration order between equal priorities. Declaring the priority is what
        // keeps the Y4NGZ postfixes on SetNewProfitQuota in a fixed order instead of whatever
        // order the chainloader happened to initialise the plugins in: this rollover refill
        // first (Priority.First), the quota milestone grants second (Priority.Normal,
        // Takeover/Patches.cs), the quota-overhaul rewrite last (Priority.Last,
        // MoonContractPatches.cs). #666: the vote-arming prefix and company-sale hooks that
        // used to share these patch sites moved to Y4NGZTerminalUpgrades' CommandPatches.
        // Planet-info panel layout. The label column is wide enough for "CONSTELLATION: "
        // to sit beside it, and the value column is placed so the widest realistic pair
        // ("TOTAL" with "AFTER 100/100") still lands inside the 30-column panel.
        private const string ConstellationLabelPrefix = "CONSTELLATION: ";
        private const int FuelLabelColumnWidth = 7;
        private const int FuelValueColumn = 17;

        [HarmonyPatch(typeof(TimeOfDay), nameof(TimeOfDay.SetNewProfitQuota))]
        [HarmonyPostfix]
        [HarmonyPriority(Priority.First)]
        private static void OnSetNewProfitQuota()
        {
            ShipSystemsController.Instance?.OnProfitQuotaRolledOver();
        }

        [HarmonyPatch(typeof(StartOfRound), "SetShipReadyToLand")]
        [HarmonyPostfix]
        private static void OnSetShipReadyToLand()
        {
            ShipSystemsController.Instance?.OnShipReadyToLand();
        }

        [HarmonyPatch(typeof(StartOfRound), nameof(StartOfRound.ChangeLevelServerRpc), new[] { typeof(int), typeof(int) })]
        [HarmonyPrefix]
        [HarmonyPriority(Priority.Last)]
        private static bool OnChangeLevelServerRpcPrefix(int levelID)
        {
            ShipSystemsController controller = GetController();
            if (controller == null || controller.TryAuthorizeRouteChangeFromServer(levelID, out string denialText))
                return true;

            return false;
        }

        [HarmonyPatch(typeof(StartOfRound), nameof(StartOfRound.ChangeLevelServerRpc), new[] { typeof(int), typeof(int) })]
        [HarmonyPostfix]
        private static void OnChangeLevelServerRpcPostfix(int levelID, bool __runOriginal)
        {
            GetController()?.CompleteAuthorizedRouteChangeFromServer(levelID, __runOriginal);
        }

        [HarmonyPatch(typeof(StartOfRound), "Start")]
        [HarmonyPostfix]
        private static void OnStartOfRoundStart(StartOfRound __instance)
        {
            FuelPumpRegistry.EnsureRegistered();
            FuelLootRegistry.EnsureRegistered();
            FuelChuteFurnitureRegistry.EnsureRegistered();

            // #679: the first point where StartOfRound.levels is final - LethalLevelLoader has
            // injected its moons by now, so this is where the generated per-moon fuel price
            // entries can be created for the moons this profile actually has.
            MoonFuelCatalogue.EnsureBuilt("StartOfRound.Start");
        }

        // #666: the start-lever blocking (votes, queued refits) moved to CommandPatches with
        // the systems that hold the ship. This side gates only the lever DEPARTURE on power:
        // autopilot still leaves at end of day even with the battery destroyed.
        [HarmonyPatch(typeof(StartOfRound), nameof(StartOfRound.EndGameServerRpc))]
        [HarmonyPrefix]
        private static bool OnEndGameServerRpc()
        {
            return !ShouldBlockShipDeparture();
        }

        [HarmonyPatch(typeof(StartOfRound), "ShipLeave")]
        [HarmonyPostfix]
        private static void OnShipLeave()
        {
            ShipSystemsController.Instance?.OnShipLeaving();
        }

        [HarmonyPatch(typeof(GameNetworkManager), nameof(GameNetworkManager.Disconnect))]
        [HarmonyPostfix]
        private static void OnDisconnect()
        {
            ShipSystemsController.Instance?.OnDisconnected();

            // #610: every monitor presentation ends with the session, whether or not its owner
            // got to say so — a coroutine killed by the scene change never reaches its release.
            // This is the ship-layout owner's patch, so it is the neutral place to sweep: it is
            // not behind any single presentation module's enable flag. Surface registrations,
            // the layout subscription and the claim probes deliberately survive; they are
            // plugin-lifetime and nothing re-establishes them per lobby.
            //
            // Safe for the monitor row specifically: a blanket release restores the row's
            // pre-claim materials and leaves its lease handle unheld, and the row's next
            // BeginBanner/BeginTakeover re-acquires cleanly because TryAcquireRowLease's
            // same-owner fast path tests IsActive, which a released lease fails.
            int released = Y4NGZCore.Modules.Monitor.MonitorArbiter.ReleaseAllLeases("disconnect");
            if (released > 0)
                ShipSystemsBootstrap.Log?.LogInfo($"[ShipLayout] Released {released} monitor lease(s) on disconnect.");
        }

        [HarmonyPatch(typeof(GameNetworkManager), "Start")]
        [HarmonyPostfix]
        private static void OnGameNetworkManagerStart()
        {
            SpareBatteryItemRegistry.EnsureRegistered();
            FuelPumpRegistry.EnsureRegistered();
            FuelLootRegistry.EnsureRegistered();
            FuelSourceSpawner.EnsureRegistered();
            FuelChuteFurnitureRegistry.EnsureRegistered();
        }

        [HarmonyPatch(typeof(Terminal), nameof(Terminal.BeginUsingTerminal))]
        [HarmonyPrefix]
        private static bool OnBeginUsingTerminal(Terminal __instance)
        {
            ShipSystemsController controller = ShipSystemsController.Instance;
            if (controller == null)
                return true;

            if (controller.ShouldBlockShipInteraction(__instance != null ? __instance.transform : null))
            {
                controller.NotifyBlockedShipInteraction();
                return false;
            }

            if (!controller.TrySpendTerminalBootPower())
            {
                HUDManager.Instance?.DisplayTip("SHIP POWER LOW", "Terminal boot denied until ship power is restored.", isWarning: true);
                return false;
            }

            MoonFuelOptionalTerminalCompatibility.EnsurePatched();
            FuelPumpRegistry.InjectIntoTerminal(__instance);
            return true;
        }

        [HarmonyPatch(typeof(Terminal), "Start")]
        [HarmonyPostfix]
        private static void OnTerminalStart(Terminal __instance)
        {
            FuelPumpRegistry.InjectIntoTerminal(__instance);

            // #679: a second additive pass. Some moon loaders finish populating the catalogue
            // after StartOfRound.Start; EnsureBuilt only binds moons it has not already seen,
            // so running it twice costs one dictionary probe per moon.
            MoonFuelCatalogue.EnsureBuilt("Terminal.Start");
        }

        [HarmonyPatch(typeof(RoundManager), nameof(RoundManager.SpawnScrapInLevel))]
        [HarmonyPrefix]
        private static void OnSpawnScrapInLevelPrefix()
        {
            FuelLootRegistry.InjectIntoCurrentLevelScrap();
        }

        [HarmonyPatch(typeof(StartOfRound), nameof(StartOfRound.openingDoorsSequence))]
        [HarmonyPostfix]
        private static void ObserveLandingDoors(StartOfRound __instance, ref System.Collections.IEnumerator __result)
        {
            FuelMonitorDisplay.BeginLanding();
            __result = FuelLandingDoorSequence.Observe(__result,
                () => __instance.shipDoorsAnimator != null && __instance.shipDoorsAnimator.GetBool("Closed"),
                () => { FuelMonitorDisplay.LandingDoorsOpened(); ShipSystemsController.Instance?.OnShipLanded(); });
        }

        [HarmonyPatch(typeof(StartOfRound), nameof(StartOfRound.OnShipLandedMiscEvents))]
        [HarmonyPostfix]
        private static void OnShipLandedMiscEventsPostfix()
        {
            FuelSourceSpawner.TrySpawnCurrentLevelSource();
            FuelMonitorDisplay.LandingDoorsOpened();
            ShipSystemsController.Instance?.OnShipLanded();
            ShipSystemsController.Instance?.EnsureInteriorSpareBatteryForRound();
        }

        [HarmonyPatch(typeof(Terminal), "LoadNewNodeIfAffordable")]
        [HarmonyPrefix]
        [HarmonyPriority(Priority.Last)]
        private static bool OnTerminalLoadNewNodeIfAffordable(Terminal __instance, TerminalNode node)
        {
            if (node == null)
                return true;

            // The pump stays registered so buy indexes match across peers; the purchase
            // itself is what the fuel toggle withholds.
            if (!ShipSystemsController.IsFuelSystemEnabled() && FuelPumpRegistry.IsPumpBuyNode(node))
            {
                if (__instance != null)
                    __instance.useCreditsCooldown = false;
                HUDManager.Instance?.DisplayTip(
                    "ITEM UNAVAILABLE",
                    "The Company is not supplying fuel pumps on this contract.",
                    isWarning: true);
                return false;
            }

            if (node.buyRerouteToMoon < 0 || node.buyRerouteToMoon == -2)
                return true;

            ShipSystemsController controller = GetController();
            if (controller == null)
                return true;

            ShipFuelQuote quote = controller.GetServerRouteQuote(node.buyRerouteToMoon);
            if (!quote.FuelSystemAvailable || quote.IsAffordable)
                return true;

            if (__instance != null)
                __instance.useCreditsCooldown = false;
            HUDManager.Instance?.DisplayTip(
                "SHIP FUEL LOW",
                string.IsNullOrWhiteSpace(quote.DenialText) ? "Insufficient ship fuel for that route." : quote.DenialText,
                isWarning: true);
            return false;
        }

        [HarmonyPatch(typeof(Terminal), "TextPostProcess")]
        [HarmonyPostfix]
        [HarmonyPriority(Priority.Last)]
        private static void OnTerminalTextPostProcess(Terminal __instance, ref string __result, TerminalNode node)
        {
            FuelTerminalSprite.Install(__instance);
            if (node == null || string.IsNullOrWhiteSpace(__result))
                return;

            ShipSystemsController controller = GetController();
            if (controller == null)
                return;

            // #679: the moons catalogue gets a per-moon fuel tag. This lives on the fuel owner's
            // own TextPostProcess postfix rather than in the contracts module, so the contracts
            // side keeps no dependency on Ship Systems in either direction - the same seam that
            // already puts the planet-info fuel panel on this screen.
            if (IsMoonCatalogueNode(__instance, node))
            {
                __result = AddMoonCatalogueFuelTags(__result);
                return;
            }

            // #685: the route-confirm node carries buyRerouteToMoon AND, on profiles whose moon
            // loader wires the planet preview to it, displayPlanetInfo. The planet-info branch
            // used to be tested first, so it claimed the confirmation screen and rebuilt it as a
            // panel: that builder treats source line 0 as a heading and inserts its block after
            // it, which on the confirm node is the middle of the vanilla sentence ("...is $0.
            // It is" | "currently mild weather on this moon."). Routing is checked first now, so
            // the confirmation is only ever appended to, never re-laid-out.
            if (node.buyRerouteToMoon >= 0 && node.buyRerouteToMoon != -2)
            {
                ShipFuelQuote quote = controller.GetServerRouteQuote(node.buyRerouteToMoon);
                if (quote.FuelSystemAvailable)
                    __result = AppendRoutingFuelText(__result, quote);
                return;
            }

            if (node.displayPlanetInfo >= 0)
            {
                // #685 belt and braces: a confirmation prompt is a sentence, not a panel. If a
                // terminal mod hands one to us without buyRerouteToMoon set, leave it alone
                // rather than break the sentence across a fuel grid.
                if (IsRouteConfirmText(__result))
                    return;

                ShipFuelQuote quote = controller.GetServerRouteQuote(node.displayPlanetInfo);
                // #679: the same one binding hides the fuel figures on both moon screens.
                if (quote.FuelSystemAvailable && ShowFuelOnMoonScreens)
                    __result = BuildPlanetInfoFuelText(__result, quote);
            }
        }

        [HarmonyPatch(typeof(StartOfRound), nameof(StartOfRound.SetShipDoorsClosed))]
        [HarmonyPrefix]
        private static void OnSetShipDoorsClosedPrefix(bool closed, out bool __state)
        {
            __state = StartOfRound.Instance != null && StartOfRound.Instance.hangarDoorsClosed;
        }

        [HarmonyPatch(typeof(StartOfRound), nameof(StartOfRound.SetShipDoorsClosed))]
        [HarmonyPostfix]
        private static void OnSetShipDoorsClosedPostfix(bool closed, bool __state)
        {
            ShipSystemsController.Instance?.OnShipDoorsToggled(closed, __state);
        }

        [HarmonyPatch(typeof(InteractTrigger), "Interact")]
        [HarmonyPrefix]
        private static bool OnInteractTriggerInteract(InteractTrigger __instance)
        {
            ShipSystemsController controller = ShipSystemsController.Instance;
            if (controller == null)
                return true;

            if (!controller.ShouldBlockShipInteraction(__instance != null ? __instance.transform : null))
                return true;

            controller.NotifyBlockedShipInteraction();
            return false;
        }

        [HarmonyPatch(typeof(PlayerControllerB), "SetHoverTipAndCurrentInteractTrigger")]
        [HarmonyPostfix]
        private static void OnSetHoverTipAndCurrentInteractTrigger(PlayerControllerB __instance)
        {
            ShipSystemsController controller = ShipSystemsController.Instance;
            if (controller == null || __instance == null || __instance != GameNetworkManager.Instance?.localPlayerController)
                return;

            InteractTrigger trigger = __instance.hoveringOverTrigger;
            if (trigger == null || !controller.ShouldBlockShipInteraction(trigger.transform))
                return;

            if (__instance.cursorIcon != null)
                __instance.cursorIcon.enabled = false;

            TextMeshProUGUI cursorTip = __instance.cursorTip;
            if (cursorTip != null)
                cursorTip.text = "Emergency protocol active";
        }

        private static bool ShouldBlockShipDeparture()
        {
            ShipSystemsController controller = GetController();
            if (controller == null || !controller.ShouldBlockShipDeparture())
                return false;

            HUDManager.Instance?.DisplayTip("SHIP LOCKED", controller.GetShipLockReason(), isWarning: true);
            return true;
        }

        /// <summary>
        /// #679: appends "[FUEL n]" (or "[FUEL FREE]" for a blacklisted or Company moon) to each
        /// row of the vanilla moons catalogue. Rows are matched by moon name in
        /// <see cref="MoonFuelCatalogueTagFormatter"/>, so a reordered or filtered list from LLL,
        /// General Improvements or a terminal formatter still prices the right moon.
        /// </summary>
        private static bool ShowFuelOnMoonScreens =>
            ShipSystemsBootstrap.ShowFuelInMoonCatalogue == null
            || ShipSystemsBootstrap.ShowFuelInMoonCatalogue.Value;

        internal static string AddMoonCatalogueFuelTags(string text, bool detailRows = false)
        {
            if (!ShowFuelOnMoonScreens || !ShipSystemsController.IsFuelSystemEnabled())
                return text;

            try
            {
                MoonFuelCatalogue.EnsureBuilt("moons catalogue");

                IReadOnlyList<MoonFuelCatalogueEntry> moons = MoonFuelCatalogue.DetectedMoons;
                if (moons.Count == 0)
                    return text;

                var tags = new List<MoonFuelCatalogueTag>(moons.Count);
                for (int i = 0; i < moons.Count; i++)
                {
                    MoonFuelCatalogueEntry entry = moons[i];
                    MoonFuelPrice price = MoonFuelCatalogue.ResolveCatalogueCost(entry);
                    tags.Add(new MoonFuelCatalogueTag(
                        entry.Aliases,
                        MoonFuelCatalogueTagFormatter.FormatFuelBody(price.Cost, price.IsFuelExempt)));
                }

                if (detailRows)
                    return MoonFuelCatalogueTagFormatter.AddFuelTags(text, tags, detailRows: true);
                return FuelTerminalSprite.AlignCatalogue(MoonFuelCatalogueTagFormatter.AddFuelTags(text, tags));
            }
            catch (Exception ex)
            {
                ShipSystemsBootstrap.Log?.LogWarning(
                    $"[ShipSystems.Fuel] Failed to annotate the moons catalogue: {ex.GetType().Name}: {ex.Message}");
                return text;
            }
        }

        /// <summary>
        /// The vanilla "moons" keyword's node, identified the same two ways the contracts
        /// catalogue patch identifies it (#674): by node name, and by the keyword's own
        /// <c>specialKeywordResult</c> for terminal mods that rename the node.
        /// </summary>
        private static bool IsMoonCatalogueNode(Terminal terminal, TerminalNode node)
        {
            if (node == null)
                return false;

            string nodeName = node.name ?? string.Empty;
            if (nodeName.IndexOf("moon", StringComparison.OrdinalIgnoreCase) >= 0
                && nodeName.IndexOf("catalog", StringComparison.OrdinalIgnoreCase) >= 0)
            {
                return true;
            }

            TerminalKeyword[] keywords = terminal?.terminalNodes?.allKeywords;
            if (keywords == null)
                return false;

            for (int i = 0; i < keywords.Length; i++)
            {
                TerminalKeyword keyword = keywords[i];
                if (keyword != null
                    && string.Equals(keyword.word, "moons", StringComparison.OrdinalIgnoreCase))
                {
                    return ReferenceEquals(keyword.specialKeywordResult, node);
                }
            }

            return false;
        }

        private static string BuildPlanetInfoFuelText(string text, ShipFuelQuote quote)
        {
            const int width = 30;
            const int maxLines = 13;
            List<string> source = SplitTerminalLines(text);
            List<string> output = new List<string>();

            // The heading, the routing block and the vanilla body each get their own band,
            // so the screen reads as three sections instead of one stack of similar lines.
            // The band costs the line the old blank spacer used, keeping the same budget.
            AddWrapped(output, FirstNonEmpty(source) ?? "ORBITING:", width, maxLines);
            AddRule(output, width, maxLines);

            // #683: no constellations mod, no constellation line. The fuel system still
            // groups moons internally so it can bill an entry cost once, but that bucket is
            // not a place, and printing its key here named a constellation the profile did
            // not have.
            if (ShowConstellation(quote))
            {
                AddWrapped(output, ConstellationLabelPrefix
                    + FormatConstellationLabel(quote, width - ConstellationLabelPrefix.Length), width, maxLines);
            }

            AddWrapped(output, FuelRow(
                "FUEL",
                EntryFuelLabel(quote) + " " + FormatFuelPart(quote.EntryFuelCost, quote.IsCurrentConstellation),
                "MOON " + FormatFuelAmount(quote.MoonFuelCost)), width, maxLines);
            AddWrapped(output, FuelRow(
                "TOTAL",
                FormatFuelAmount(quote.TotalFuelCost),
                "AFTER " + FormatFuelAmount(Mathf.Max(0f, quote.FuelAfterRoute))
                    + "/" + FormatFuelAmount(quote.FuelCapacity)), width, maxLines);
            AddRule(output, width, maxLines);

            string contractLine = FirstLineContaining(source, "contract:");
            if (!string.IsNullOrWhiteSpace(contractLine))
                AddWrapped(output, contractLine.Trim(), width, maxLines);

            for (int i = 1; i < source.Count && output.Count < maxLines; i++)
            {
                string line = source[i];
                if (string.IsNullOrWhiteSpace(line))
                    continue;
                if (line.IndexOf("fuel", StringComparison.OrdinalIgnoreCase) >= 0)
                    continue;
                if (line.IndexOf("contract:", StringComparison.OrdinalIgnoreCase) >= 0)
                    continue;

                AddWrapped(output, line.Trim(), width, maxLines);
            }

            return string.Join("\n", output.ToArray()) + "\n";
        }

        /// <summary>
        /// #685: the route confirmation is a sentence the player is reading, so everything this
        /// adds is placed after it, in one block, ahead of the CONFIRM/DENY prompt. The fuel
        /// half is a single line — current tank and what this route costs — because the
        /// per-leg breakdown belongs on the planet-info panel, which still carries it.
        /// </summary>
        private static string AppendRoutingFuelText(string text, ShipFuelQuote quote)
        {
            if (text.IndexOf(RouteFuelLinePrefix, StringComparison.OrdinalIgnoreCase) >= 0)
                return text;

            // #683: the constellation block only exists when a constellations mod does. Its
            // description is the constellation's own configured text, so the confirmation says
            // where the ship is going rather than only naming it.
            string constellationBlock = string.Empty;
            if (ShowConstellation(quote))
            {
                constellationBlock = "Constellation: " + FormatConstellationLabel(quote);

                string subtitle = ConstellationsModCompat.GetSubtitle(quote.ConstellationName);
                if (!string.IsNullOrWhiteSpace(subtitle))
                    constellationBlock += " (" + subtitle + ")";

                string description = ConstellationsModCompat.GetDescription(quote.ConstellationName);
                if (!string.IsNullOrWhiteSpace(description))
                    constellationBlock += "\n" + description;
            }

            string block = string.Empty;
            if (constellationBlock.Length > 0)
                block = block + constellationBlock;

            if (ShouldShowRouteFuelLine(quote))
            {
                if (block.Length > 0)
                    block += "\n\n";
                block += RouteFuelLinePrefix + " "
                    + FormatFuelAmount(quote.FuelAvailable) + "/" + FormatFuelAmount(quote.FuelCapacity)
                    + "   " + FuelTerminalSprite.Icon + " COST: " + FormatFuelAmount(quote.TotalFuelCost)
                    + "\n" + (quote.FuelAfterRoute < 0f ? "SHORTFALL: " + FormatFuelAmount(-quote.FuelAfterRoute)
                        : "AFTER ROUTE: " + FormatFuelAmount(quote.FuelAfterRoute));
            }

            if (block.Length == 0)
                return text;

            return InsertBeforeConfirmPrompt(text, block);
        }

        private const string RouteFuelLinePrefix = "FUEL:";

        /// <summary>
        /// Nothing is printed when this route is not actually gated by fuel: the system off, an
        /// unresolvable route, or a moon the price catalogue exempts (the Company building, a
        /// blacklisted moon) all quote a zero total, and a "COST: 0" line is noise on a screen
        /// the player reads once per route.
        /// </summary>
        private static bool ShouldShowRouteFuelLine(ShipFuelQuote quote)
        {
            return quote.FuelSystemAvailable
                && ShipSystemsController.IsFuelSystemEnabled()
                && Mathf.CeilToInt(Mathf.Max(0f, quote.TotalFuelCost)) > 0;
        }

        /// <summary>
        /// #685: places an addition after the whole vanilla sentence and before the
        /// "Please CONFIRM or DENY." prompt, so the prompt stays the last thing on screen.
        /// Falls back to appending when no prompt is found (the post-purchase routing node).
        /// </summary>
        private static string InsertBeforeConfirmPrompt(string text, string block)
        {
            int anchor = IndexOfConfirmPrompt(text);
            if (anchor < 0)
                return text.TrimEnd() + "\n\n" + block + "\n";

            string head = text.Substring(0, anchor).TrimEnd();
            string tail = text.Substring(anchor);
            return head + "\n\n" + block + "\n\n" + tail;
        }

        private static int IndexOfConfirmPrompt(string text)
        {
            if (string.IsNullOrEmpty(text))
                return -1;

            int index = text.IndexOf("Please CONFIRM", StringComparison.OrdinalIgnoreCase);
            if (index < 0)
                index = text.IndexOf("CONFIRM or DENY", StringComparison.OrdinalIgnoreCase);
            if (index < 0)
                return -1;

            // Anchor at the start of the prompt's own line so a prompt that a terminal mod
            // prefixed ("> Please CONFIRM...") is not split from its own leading characters.
            int lineStart = text.LastIndexOf('\n', Math.Max(0, index - 1));
            return lineStart < 0 ? index : lineStart + 1;
        }

        private static bool IsRouteConfirmText(string text)
        {
            return !string.IsNullOrEmpty(text)
                && text.IndexOf("CONFIRM", StringComparison.OrdinalIgnoreCase) >= 0
                && text.IndexOf("DENY", StringComparison.OrdinalIgnoreCase) >= 0;
        }

        private static List<string> SplitTerminalLines(string text)
        {
            string[] raw = (text ?? string.Empty).Replace("\r", string.Empty).Split('\n');
            List<string> lines = new List<string>(raw.Length);
            foreach (string line in raw)
            {
                string trimmed = line.TrimEnd();
                if (trimmed.Length > 0)
                    lines.Add(trimmed);
            }

            return lines;
        }

        private static string FirstNonEmpty(List<string> lines)
        {
            if (lines == null)
                return null;

            foreach (string line in lines)
            {
                if (!string.IsNullOrWhiteSpace(line))
                    return line.Trim();
            }

            return null;
        }

        private static string FirstLineContaining(List<string> lines, string token)
        {
            if (lines == null || string.IsNullOrWhiteSpace(token))
                return null;

            foreach (string line in lines)
            {
                if (!string.IsNullOrWhiteSpace(line)
                    && line.IndexOf(token, StringComparison.OrdinalIgnoreCase) >= 0)
                {
                    return line;
                }
            }

            return null;
        }

        private static void AddRule(List<string> output, int width, int maxLines)
        {
            if (output.Count < maxLines)
                output.Add(new string('-', width));
        }

        /// <summary>
        /// A fixed label column plus two value columns, so the routing figures line up
        /// vertically rather than reading as one run-on line. A first value long enough to
        /// reach the second column (for example "CONST IN ORBIT") pushes it along by a
        /// single space instead of wrapping the row.
        /// </summary>
        private static string FuelRow(string label, string first, string second)
        {
            string row = label.PadRight(FuelLabelColumnWidth) + first;
            row = row.Length >= FuelValueColumn ? row + " " : row.PadRight(FuelValueColumn);
            return row + second;
        }

        private static void AddWrapped(List<string> output, string text, int width, int maxLines)
        {
            if (output.Count >= maxLines || string.IsNullOrWhiteSpace(text))
                return;

            // A line hoisted from another module's text can already carry <color=...> tags.
            // Those characters are invisible on screen but would count against the wrap
            // width, so such a line is placed whole rather than broken inside a tag.
            if (text.IndexOf('<') >= 0)
            {
                output.Add(text.Trim());
                return;
            }

            string remaining = text.Trim();
            while (remaining.Length > 0 && output.Count < maxLines)
            {
                if (remaining.Length <= width)
                {
                    output.Add(remaining);
                    return;
                }

                int breakAt = remaining.LastIndexOf(' ', Math.Min(width, remaining.Length - 1));
                if (breakAt <= 0)
                    breakAt = width;

                output.Add(remaining.Substring(0, breakAt).TrimEnd());
                remaining = remaining.Substring(breakAt).TrimStart();
            }
        }

        /// <summary>
        /// #683: whether this quote's constellation is a real one the player's profile has.
        /// The fuel system always groups moons -- it needs a bucket to bill entry fuel once
        /// against -- but a bucket invented in the absence of a constellations mod is not a
        /// place, and the screens used to print its key as if it were.
        /// </summary>
        private static bool ShowConstellation(ShipFuelQuote quote)
        {
            return ConstellationsModCompat.CanDisplay(quote.ConstellationName, quote.ConstellationKey);
        }

        /// <summary>
        /// The entry-fuel column's label. It is a constellation crossing only when there are
        /// constellations; otherwise the same number is simply the route's entry cost, and
        /// calling it "CONST" would be the last piece of constellation wording on a screen
        /// that has none.
        /// </summary>
        private static string EntryFuelLabel(ShipFuelQuote quote)
        {
            return ShowConstellation(quote) ? "CONST" : "ENTRY";
        }

        private static string FormatConstellationLabel(ShipFuelQuote quote, int maxLength = 18)
        {
            string value = !string.IsNullOrWhiteSpace(quote.ConstellationName)
                ? quote.ConstellationName
                : quote.ConstellationKey;
            if (string.IsNullOrWhiteSpace(value))
                value = "UNKNOWN";
            return Trim(value.ToUpperInvariant(), maxLength);
        }

        private static string FormatFuelPart(float fuel, bool isCurrentConstellation)
        {
            return isCurrentConstellation ? "IN ORBIT" : FormatFuelAmount(fuel);
        }

        private static string FormatFuelAmount(float fuel)
        {
            return Y4NGZCompany.ShipSystems.Fuel.FuelPresentation.Amount(fuel);
        }

        private static string Trim(string value, int maxLength)
        {
            if (string.IsNullOrEmpty(value) || value.Length <= maxLength)
                return value ?? string.Empty;
            return value.Substring(0, Math.Max(0, maxLength - 1)) + ".";
        }

        private static ShipSystemsController GetController()
        {
            if (ShipSystemsBootstrap.Enabled != null && !ShipSystemsBootstrap.Enabled.Value)
                return null;

            ShipSystemsController.EnsureInstance();
            return ShipSystemsController.Instance;
        }
    }
}
