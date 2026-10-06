using System;

namespace Y4NGZCompany.ShipSystems.Fuel.Economy
{
    /// <summary>Which rule produced a resolved fuel price. Diagnostics and terminal copy only.</summary>
    internal enum MoonFuelPriceSource
    {
        /// <summary>The Company building is never fuel-priced.</summary>
        CompanyMoon,

        /// <summary>A per-moon price the host edited away from its generated default.</summary>
        MoonOverride,

        /// <summary>The moon is on the fuel blacklist: fuel is not a factor for it.</summary>
        Blacklisted,

        /// <summary>An emergency-route moon, priced by the emergency binding.</summary>
        EmergencyMoon,

        /// <summary>A per-constellation price the host edited away from its generated default.</summary>
        ConstellationOverride,

        /// <summary>Nothing was overridden; the risk-tier table decided.</summary>
        TierDefault,
    }

    /// <summary>
    /// Everything the resolver needs about one route, gathered by the caller. Passing values
    /// rather than reading config here is what keeps <see cref="MoonFuelPriceResolver"/> a pure
    /// function the regression suite can exercise without Unity or BepInEx.
    /// </summary>
    internal readonly struct MoonFuelPriceInputs
    {
        internal MoonFuelPriceInputs(
            bool isCompanyMoon,
            bool isBlacklistedMoon,
            bool isEmergencyMoon,
            float emergencyMoonCost,
            bool hasMoonOverride,
            float moonOverride,
            bool hasConstellationOverride,
            float constellationOverride,
            float constellationEntryDefault,
            float tierMoonCost)
        {
            IsCompanyMoon = isCompanyMoon;
            IsBlacklistedMoon = isBlacklistedMoon;
            IsEmergencyMoon = isEmergencyMoon;
            EmergencyMoonCost = emergencyMoonCost;
            HasMoonOverride = hasMoonOverride;
            MoonOverride = moonOverride;
            HasConstellationOverride = hasConstellationOverride;
            ConstellationOverride = constellationOverride;
            ConstellationEntryDefault = constellationEntryDefault;
            TierMoonCost = tierMoonCost;
        }

        internal bool IsCompanyMoon { get; }
        internal bool IsBlacklistedMoon { get; }
        internal bool IsEmergencyMoon { get; }
        internal float EmergencyMoonCost { get; }
        internal bool HasMoonOverride { get; }
        internal float MoonOverride { get; }
        internal bool HasConstellationOverride { get; }
        internal float ConstellationOverride { get; }
        internal float ConstellationEntryDefault { get; }
        internal float TierMoonCost { get; }
    }

    /// <summary>A resolved price and the rule that produced it.</summary>
    internal readonly struct MoonFuelPrice
    {
        internal MoonFuelPrice(float cost, MoonFuelPriceSource source)
        {
            Cost = cost < 0f ? 0f : cost;
            Source = source;
        }

        internal float Cost { get; }
        internal MoonFuelPriceSource Source { get; }

        /// <summary>
        /// True when fuel is not a factor for this route at all - the Company building or a
        /// blacklisted moon. A zero <see cref="Cost"/> from any other rule is still a priced
        /// route that merely happens to cost nothing, which is why this is not just
        /// <c>Cost == 0</c>.
        /// </summary>
        internal bool IsFuelExempt =>
            Source == MoonFuelPriceSource.CompanyMoon || Source == MoonFuelPriceSource.Blacklisted;
    }

    /// <summary>
    /// #679: the single authority for what a route costs in fuel.
    ///
    /// <para><b>Precedence, highest first:</b> explicit per-moon price, then the per-moon fuel
    /// blacklist, then the emergency-moon binding, then an explicit per-constellation price,
    /// then the risk-tier table. "Explicit" means the host edited the generated entry away from
    /// the default that entry was created with; an untouched entry is not an override, which is
    /// what makes generating an entry for every installed moon a no-op until someone edits
    /// one.</para>
    ///
    /// <para>The blacklist deliberately sits BELOW an explicit per-moon price. A host who both
    /// blacklists a moon and then types a price for it has said the more specific thing second,
    /// and the price wins; removing the price restores the exemption.</para>
    /// </summary>
    internal static class MoonFuelPriceResolver
    {
        /// <summary>
        /// A moon inside a constellation costs a quarter of that constellation's entry price,
        /// rounded up. This is the relation the fuel system has always used; it is named here
        /// so the per-constellation override keeps meaning the same thing it did before #679.
        /// </summary>
        internal const float ConstellationMoonCostFactor = 0.25f;

        /// <summary>Fuel spent for landing on the moon itself.</summary>
        internal static MoonFuelPrice ResolveMoonCost(in MoonFuelPriceInputs inputs)
        {
            if (inputs.IsCompanyMoon)
                return new MoonFuelPrice(0f, MoonFuelPriceSource.CompanyMoon);

            if (inputs.HasMoonOverride)
                return new MoonFuelPrice(inputs.MoonOverride, MoonFuelPriceSource.MoonOverride);

            if (inputs.IsBlacklistedMoon)
                return new MoonFuelPrice(0f, MoonFuelPriceSource.Blacklisted);

            if (inputs.IsEmergencyMoon)
                return new MoonFuelPrice(inputs.EmergencyMoonCost, MoonFuelPriceSource.EmergencyMoon);

            if (inputs.HasConstellationOverride)
            {
                return new MoonFuelPrice(
                    DeriveMoonCostFromEntryCost(inputs.ConstellationOverride),
                    MoonFuelPriceSource.ConstellationOverride);
            }

            return new MoonFuelPrice(inputs.TierMoonCost, MoonFuelPriceSource.TierDefault);
        }

        /// <summary>
        /// Fuel spent once for crossing into the constellation. A per-moon price does not reach
        /// this number: a moon price is what one landing costs, while the entry price is a
        /// property of the constellation the route enters. The blacklist does reach it, because
        /// "fuel is not a factor for this moon" has to mean the whole route is free.
        /// </summary>
        internal static MoonFuelPrice ResolveEntryCost(in MoonFuelPriceInputs inputs)
        {
            if (inputs.IsCompanyMoon)
                return new MoonFuelPrice(0f, MoonFuelPriceSource.CompanyMoon);

            if (inputs.IsBlacklistedMoon)
                return new MoonFuelPrice(0f, MoonFuelPriceSource.Blacklisted);

            if (inputs.HasConstellationOverride)
            {
                return new MoonFuelPrice(
                    inputs.ConstellationOverride,
                    MoonFuelPriceSource.ConstellationOverride);
            }

            return new MoonFuelPrice(inputs.ConstellationEntryDefault, MoonFuelPriceSource.TierDefault);
        }

        /// <summary>The constellation-entry-to-moon relation, exposed for callers and tests.</summary>
        internal static float DeriveMoonCostFromEntryCost(float entryCost)
        {
            if (entryCost <= 0f)
                return 0f;

            return (float)Math.Ceiling(entryCost * ConstellationMoonCostFactor);
        }

        /// <summary>
        /// True when <paramref name="current"/> differs from the value the entry was generated
        /// with. Floats round-trip through the config file as text, so an exact comparison would
        /// call an untouched entry edited on some cultures; the tolerance is a tenth of the
        /// smallest fuel unit the terminal can display.
        /// </summary>
        internal static bool IsOverridden(float current, float generatedDefault)
        {
            return Math.Abs(current - generatedDefault) > 0.05f;
        }
    }
}
