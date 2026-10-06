using GameNetcodeStuff;
using HarmonyLib;
using Unity.Netcode;
namespace Y4NGZCompany.ShipSystems.Fuel
{
    [HarmonyPatch(typeof(PlayerControllerB), "GrabObjectServerRpc")]
    internal static class FuelChuteGrabPatch
    {
        private static readonly System.Reflection.MethodInfo Reply = AccessTools.Method(typeof(PlayerControllerB), "GrabObjectClientRpc");
        [HarmonyPrefix]
        private static bool Prefix(PlayerControllerB __instance, NetworkObjectReference grabbedObject)
        {
            if (!__instance.IsServer || Reply == null || !grabbedObject.TryGet(out var net)) return true;
            var transfer=net.GetComponent<FuelChuteTransfer>();
            if(transfer==null || !transfer.InProgress)return true;
            // Vanilla's RPC checks only heldByPlayerOnServer, not grabbable. Reject
            // an in-flight grab request normally rather than racing item disposal.
            Reply.Invoke(__instance,new object[]{false,grabbedObject});
            return false;
        }
    }
}
