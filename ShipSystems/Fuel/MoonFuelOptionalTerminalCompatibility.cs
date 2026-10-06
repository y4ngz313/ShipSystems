using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using HarmonyLib;
using UnityEngine;

using Y4NGZCompany.Bootstrap;

namespace Y4NGZCompany.ShipSystems.Fuel
{
    /// <summary>
    /// Reflection-only adapters for terminal mods that render their own moon catalogue and
    /// therefore never call <see cref="Terminal.TextPostProcess"/>. Keeping these links soft
    /// means ShipSystems does not acquire a load-order or assembly dependency on either mod.
    /// </summary>
    internal static class MoonFuelOptionalTerminalCompatibility
    {
        private static readonly string[] CatalogueTypeNames =
        {
            "TerminalUtils.Nodes.MoonCatalogue",
            "TerminalFormatter.Nodes.Moons",
        };

        private static readonly HashSet<MethodBase> PatchedMethods = new HashSet<MethodBase>();
        private static Harmony _harmony;

        internal static void Initialize(Harmony harmony)
        {
            _harmony = harmony;
            EnsurePatched();
        }

        internal static void EnsurePatched()
        {
            if (_harmony == null)
                return;

            foreach (string typeName in CatalogueTypeNames)
            {
                try
                {
                    Type targetType = AppDomain.CurrentDomain.GetAssemblies()
                        .Select(assembly => assembly.GetType(typeName, throwOnError: false))
                        .FirstOrDefault(type => type != null);
                    MethodInfo getNodeText = targetType?.GetMethod(
                        "GetNodeText",
                        BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance,
                        binder: null,
                        types: new[] { typeof(TerminalNode) },
                        modifiers: null);
                    if (targetType == null) continue;
                    if (getNodeText == null || getNodeText.ReturnType != typeof(string))
                    {
                        ShipSystemsBootstrap.Log?.LogWarning($"[TerminalCompat] Unsupported catalogue signature: {typeName}.GetNodeText(TerminalNode). Detail rows unavailable.");
                        continue;
                    }
                    if (PatchedMethods.Contains(getNodeText)) continue;

                    var postfix = new HarmonyMethod(AccessTools.Method(
                        typeof(MoonFuelOptionalTerminalCompatibility),
                        nameof(CataloguePostfix))) { priority = Priority.Last };
                    _harmony.Patch(getNodeText, postfix: postfix);
                    PatchedMethods.Add(getNodeText);
                    ShipSystemsBootstrap.Log?.LogInfo(
                        $"[ShipSystems.TerminalCompat] Added fuel detail-row adapter for {typeName}.GetNodeText.");
                }
                catch (Exception e)
                {
                    ShipSystemsBootstrap.Log?.LogWarning(
                        $"[ShipSystems.TerminalCompat] Could not patch {typeName}: {e.Message}");
                }
            }
        }

        private static void CataloguePostfix(ref string __result)
        {
            __result = Layout.Patches.AddMoonCatalogueFuelTags(__result, detailRows: true);
        }
    }

}
