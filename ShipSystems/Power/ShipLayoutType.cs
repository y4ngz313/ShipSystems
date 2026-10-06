namespace Y4NGZCompany.ShipSystems.Power
{
    /// <summary>
    /// The four ship layouts, as the battery station places against them (#666).
    ///
    /// <para>A deliberate mirror of the command plugin's
    /// <c>Y4NGZCompany.ShipSystems.Layout.ShipLayoutType</c> — different namespace, same
    /// member names and numeric values, because the values ride the upgrade-state wire
    /// snapshot (clamped 0..3) and the names round-trip through the Core layout-name seam
    /// (<c>ShipUpgradeState.CurrentLayoutName</c>). The layout system itself lives entirely
    /// in Y4NGZTerminalUpgrades; this side only ever parses a name into a placement key.
    /// </para>
    /// </summary>
    internal enum ShipLayoutType
    {
        Vanilla = 0,
        Wider = 1,
        TwoStory = 2,
        WiderAndTwoStory = 3,
    }
}
