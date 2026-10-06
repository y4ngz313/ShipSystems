namespace Y4NGZCompany.ShipSystems.Fuel
{
    // One pixel droplet master for fuel costs, scans and the fillable monitor.
    internal static class FuelIconPixels
    {
        internal const int Size = 24;
        internal const int InteriorTop = 5, InteriorBottom = 19;
        private static readonly int[] Left = { 11, 10, 10, 9, 8, 8, 7, 6, 5, 4, 4, 3, 3, 3, 3, 4, 4, 5, 6, 8 };
        private static bool IsShape(int x, int y) => y >= 2 && y <= 21 && x >= Left[y-2] && x <= 23-Left[y-2];
        internal static bool IsInterior(int x, int y) => IsShape(x,y) && IsShape(x-2,y) && IsShape(x+2,y) && IsShape(x,y-2) && IsShape(x,y+2);
        internal static bool IsInk(int x, int y) => IsShape(x,y) && !IsInterior(x,y);
    }
}
