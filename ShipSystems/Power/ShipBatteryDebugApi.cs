using Y4NGZCompany.ShipSystems.Layout;

namespace Y4NGZCompany.ShipSystems.Power
{
    public static class ShipBatteryDebugApi
    {
        public static void StartPlacement()
        {
            ShipSystemsController.Instance?.StartBatteryDebugPlacement(null);
        }

        public static void StartPlacement(string layoutName)
        {
            ShipSystemsController.Instance?.StartBatteryDebugPlacement(layoutName);
        }

        public static void ShowPreview()
        {
            ShipSystemsController.Instance?.ShowBatteryDebugPreview();
        }

        public static void ClearPreview()
        {
            ShipSystemsController.Instance?.ClearBatteryDebugPreview();
        }

        public static string GetStatus()
        {
            return ShipSystemsController.Instance?.GetBatteryDebugStatus() ?? "ship systems unavailable";
        }

        public static void RotatePlacement(float xDegrees, float yDegrees, float zDegrees)
        {
            ShipSystemsController.Instance?.RotateBatteryDebugPlacement(xDegrees, yDegrees, zDegrees);
        }

        public static void SetPlacementScale(float uniformScale)
        {
            ShipSystemsController.Instance?.SetBatteryDebugPlacementScale(uniformScale);
        }
    }
}
