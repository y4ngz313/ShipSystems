using System;
using System.Collections.Generic;
using System.Reflection;
using GameNetcodeStuff;
using HarmonyLib;
using UnityEngine;
using UnityEngine.AI;
using Y4NGZCompany.Bootstrap;
using Y4NGZCompany.ShipSystems.Fuel;
using Y4NGZCompany.ShipSystems.Layout;

namespace Y4NGZCompany.ShipSystems.Power
{
    internal static class BatterySabotageDirector
    {
        private const int MaskedIdleState = 0;
        private const int MaskedChaseState = 1;
        private const float EntranceTeleportDistance = 1.25f;
        private const float AmbientRollInterval = 1.5f;

        private sealed class SabotageState
        {
            internal EnemyAI Enemy;
            internal bool Rolled;
            internal bool TargetBattery;
            internal bool LoggedUnavailable;
            internal PlayerControllerB InterruptTarget;
        }

        private static readonly Dictionary<int, SabotageState> States = new Dictionary<int, SabotageState>();
        private static readonly MethodInfo MaskedTeleportAndSync =
            AccessTools.Method(typeof(MaskedPlayerEnemy), "TeleportMaskedEnemyAndSync");
        private static float _nextAmbientRollAt;

        internal static bool HasActiveBatterySabotage
        {
            get
            {
                ShipSystemsController controller = ShipSystemsController.Instance;
                if (controller == null || !controller.TryGetBatterySabotagePosition(out Vector3 batteryPosition))
                    return false;

                foreach (SabotageState state in States.Values)
                {
                    if (state?.Enemy == null || state.Enemy.isEnemyDead ||
                        !state.TargetBattery || state.InterruptTarget != null)
                        continue;
                    if (IsEnemySabotagingAtBattery(state.Enemy, batteryPosition, out _))
                        return true;
                }

                // Target rolls are server-owned, but enemy transforms are
                // already network-synchronised. Clients can therefore surface
                // the same pre-critical warning without another message by
                // treating a live saboteur physically inside the attack radius
                // as an active battery threat.
                List<EnemyAI> enemies = RoundManager.Instance?.SpawnedEnemies;
                float radius = Mathf.Max(
                    0.1f,
                    ShipSystemsBootstrap.BatterySabotageRadius?.Value ?? 4.2f);
                float radiusSquared = radius * radius;
                if (enemies != null)
                {
                    for (int i = 0; i < enemies.Count; i++)
                    {
                        EnemyAI enemy = enemies[i];
                        if ((IsSaboteurType(enemy) || IsAdditionalAttacker(enemy)) &&
                            (enemy.transform.position - batteryPosition).sqrMagnitude <= radiusSquared)
                            return true;
                    }
                }
                return false;
            }
        }

        internal static void TickServer()
        {
            if (Time.realtimeSinceStartup < _nextAmbientRollAt)
                return;
            _nextAmbientRollAt = Time.realtimeSinceStartup + AmbientRollInterval;

            CleanupDeadEnemies();
            if (!IsBatteryAvailable())
                return;

            RoundManager round = RoundManager.Instance;
            List<EnemyAI> enemies = round != null ? round.SpawnedEnemies : null;
            if (enemies == null)
                return;

            bool doorClosedPressure = IsPlayerHidingBehindClosedShipDoor();
            float retargetChance01 = Mathf.Clamp01((ShipSystemsBootstrap.BatteryDoorClosedRetargetChancePercent?.Value ?? 0.35f) / 100f);
            for (int i = 0; i < enemies.Count; i++)
            {
                EnemyAI enemy = enemies[i];
                if (!IsSaboteurType(enemy))
                    continue;

                SabotageState state = GetOrCreateState(enemy);
                if (!state.Rolled)
                {
                    TryRollBatteryTarget(enemy);
                    continue;
                }

                // Players hiding in the ship with the door closed slowly tempt
                // saboteurs that originally ignored the battery.
                if (doorClosedPressure &&
                    !state.TargetBattery &&
                    retargetChance01 > 0f &&
                    UnityEngine.Random.value < retargetChance01)
                {
                    state.TargetBattery = true;
                    state.InterruptTarget = null;
                    LogVerbose($"{GetEnemyName(enemy)} re-rolled onto the ship battery (door-closed pressure).");
                }
            }
        }

        internal static bool IsPlayerHidingBehindClosedShipDoor()
        {
            StartOfRound sor = StartOfRound.Instance;
            if (sor == null || !sor.hangarDoorsClosed || sor.allPlayerScripts == null)
                return false;

            for (int i = 0; i < sor.allPlayerScripts.Length; i++)
            {
                PlayerControllerB player = sor.allPlayerScripts[i];
                if (player != null && player.isPlayerControlled && !player.isPlayerDead && player.isInHangarShipRoom)
                    return true;
            }

            return false;
        }

        internal static bool TryRollBatteryTarget(EnemyAI enemy)
        {
            if (!IsSaboteurType(enemy))
                return false;

            SabotageState state = GetOrCreateState(enemy);
            if (state.Rolled)
                return IsBatteryTargetActive(enemy);

            if (!IsBatteryAvailable())
            {
                if (!state.LoggedUnavailable)
                {
                    state.LoggedUnavailable = true;
                    LogVerbose($"{GetEnemyName(enemy)} battery sabotage roll deferred; ship battery is not ready.");
                }

                return false;
            }

            state.Rolled = true;
            float chance01 = Mathf.Clamp01((ShipSystemsBootstrap.BatterySabotageTargetChancePercent?.Value ?? 25f) / 100f);
            if (IsPlayerHidingBehindClosedShipDoor())
                chance01 = Mathf.Clamp01(chance01 * Mathf.Max(1f, ShipSystemsBootstrap.BatteryDoorClosedTargetChanceMultiplier?.Value ?? 2f));
            state.TargetBattery = UnityEngine.Random.value < chance01;

            ShipSystemsBootstrap.Log?.LogInfo($"[LGUShipSystems.BatterySabotage] {GetEnemyName(enemy)} id={enemy.GetInstanceID()} chose {(state.TargetBattery ? "battery" : "normal targeting")} (chance={chance01 * 100f:0.##}%).");

            return state.TargetBattery;
        }

        internal static bool TryForceBatteryTarget(EnemyAI enemy)
        {
            if (!IsSaboteurType(enemy))
                return false;

            SabotageState state = GetOrCreateState(enemy);
            if (!IsBatteryAvailable())
            {
                if (!state.LoggedUnavailable)
                {
                    state.LoggedUnavailable = true;
                    LogVerbose($"{GetEnemyName(enemy)} forced battery sabotage deferred; ship battery is not ready.");
                }

                return false;
            }

            state.Rolled = true;
            state.TargetBattery = true;
            state.InterruptTarget = null;
            LogVerbose($"{GetEnemyName(enemy)} forced ship battery sabotage target.");
            return true;
        }

        internal static bool IsBatteryTargetActive(EnemyAI enemy)
        {
            if (enemy == null || enemy.isEnemyDead)
                return false;

            SabotageState state = GetState(enemy);
            if (state == null || !state.TargetBattery)
                return false;

            if (!IsBatteryAvailable())
            {
                state.TargetBattery = false;
                state.InterruptTarget = null;
                return false;
            }

            return true;
        }

        internal static bool IsBatteryInterruptActive(EnemyAI enemy)
        {
            SabotageState state = GetState(enemy);
            return state != null && state.InterruptTarget != null;
        }

        internal static bool TryGetBatteryDestination(EnemyAI enemy, out Vector3 destination)
        {
            destination = Vector3.zero;
            if (!IsBatteryTargetActive(enemy))
                return false;

            if (TryRouteAcrossMainEntrance(enemy, out destination))
                return true;

            return ShipSystemsController.Instance != null &&
                   ShipSystemsController.Instance.TryGetBatterySabotagePosition(out destination);
        }

        internal static bool TryGetInterruptTarget(EnemyAI enemy, out PlayerControllerB player)
        {
            player = null;
            if (!IsBatteryTargetActive(enemy))
                return false;

            SabotageState state = GetState(enemy);
            if (state == null)
                return false;

            if (IsInterruptTargetValid(enemy, state.InterruptTarget))
            {
                player = state.InterruptTarget;
                return true;
            }

            state.InterruptTarget = null;
            player = FindInterruptPlayer(enemy);
            if (player == null)
                return false;

            state.InterruptTarget = player;
            LogVerbose($"{GetEnemyName(enemy)} battery sabotage interrupted by {PlayerLabel(player)}.");
            return true;
        }

        internal static bool ShouldReleaseInterrupt(EnemyAI enemy, PlayerControllerB player)
        {
            if (enemy == null || player == null || player.isPlayerDead)
                return true;

            if (!enemy.PlayerIsTargetable(player, cannotBeInShip: true, overrideInsideFactoryCheck: false, checkForMineshaftStartTile: false))
                return true;

            float releaseDistance = Mathf.Max(
                GetInterruptRange(enemy),
                ShipSystemsBootstrap.BatterySabotageReleaseDistance?.Value ?? 15f);
            return Vector3.Distance(enemy.transform.position, player.transform.position) >= releaseDistance;
        }

        internal static void ClearInterrupt(EnemyAI enemy)
        {
            SabotageState state = GetState(enemy);
            if (state == null)
                return;

            state.InterruptTarget = null;
        }

        internal static bool IsEnemySabotagingAtBattery(EnemyAI enemy, Vector3 batteryPosition, out float distance)
        {
            distance = float.PositiveInfinity;
            if (IsAdditionalAttacker(enemy) && IsBatteryAvailable())
            {
                distance = Vector3.Distance(enemy.transform.position, batteryPosition);
                return distance <= Mathf.Max(.1f, ShipSystemsBootstrap.BatterySabotageRadius?.Value ?? 4.2f);
            }
            if (!IsBatteryTargetActive(enemy) || IsBatteryInterruptActive(enemy))
                return false;

            distance = Vector3.Distance(enemy.transform.position, batteryPosition);
            return distance <= Mathf.Max(0.1f, ShipSystemsBootstrap.BatterySabotageRadius?.Value ?? 4.2f);
        }

        internal static void ClearBatteryTarget(EnemyAI enemy)
        {
            SabotageState state = GetState(enemy);
            if (state == null)
                return;

            state.TargetBattery = false;
            state.InterruptTarget = null;
        }

        internal static void ClearAllBatteryTargets()
        {
            foreach (SabotageState state in States.Values)
            {
                state.TargetBattery = false;
                state.InterruptTarget = null;
            }
        }

        internal static void CleanupDeadEnemies()
        {
            if (States.Count == 0)
                return;

            var remove = new List<int>();
            foreach (KeyValuePair<int, SabotageState> pair in States)
            {
                if (pair.Value.Enemy == null || pair.Value.Enemy.isEnemyDead)
                    remove.Add(pair.Key);
            }

            for (int i = 0; i < remove.Count; i++)
                States.Remove(remove[i]);
        }

        private static bool TryRouteAcrossMainEntrance(EnemyAI enemy, out Vector3 destination)
        {
            destination = Vector3.zero;
            if (enemy == null || enemy.isOutside)
                return false;

            destination = RoundManager.FindMainEntrancePosition(getTeleportPosition: true, enemy.isOutside);
            if (Vector3.Distance(enemy.transform.position, destination) > EntranceTeleportDistance)
                return true;

            Vector3 exitPosition = RoundManager.FindMainEntrancePosition(getTeleportPosition: true, !enemy.isOutside);
            if (enemy is MaskedPlayerEnemy masked && MaskedTeleportAndSync != null && masked.IsOwner)
            {
                MaskedTeleportAndSync.Invoke(masked, new object[] { exitPosition, !enemy.isOutside });
            }
            else
            {
                TeleportEnemyAcrossEntrance(enemy, exitPosition, !enemy.isOutside);
            }

            destination = exitPosition;
            return false;
        }

        private static void TeleportEnemyAcrossEntrance(EnemyAI enemy, Vector3 position, bool outside)
        {
            if (enemy == null)
                return;

            Vector3 navPosition = RoundManager.Instance != null
                ? RoundManager.Instance.GetNavMeshPosition(position)
                : position;

            if (enemy.agent != null && enemy.agent.enabled)
            {
                enemy.agent.enabled = false;
                enemy.transform.position = navPosition;
                enemy.agent.enabled = true;
                if (enemy.agent.isOnNavMesh)
                    enemy.agent.Warp(navPosition);
            }
            else
            {
                enemy.transform.position = navPosition;
            }

            enemy.serverPosition = navPosition;
            enemy.SetEnemyOutside(outside);
        }

        private static PlayerControllerB FindInterruptPlayer(EnemyAI enemy)
        {
            PlayerControllerB[] players = StartOfRound.Instance?.allPlayerScripts;
            if (players == null || enemy == null)
                return null;

            PlayerControllerB closest = null;
            float closestDistance = Mathf.Max(0.5f, GetInterruptRange(enemy));
            for (int i = 0; i < players.Length; i++)
            {
                PlayerControllerB player = players[i];
                if (!IsInterruptCandidate(enemy, player, closestDistance, out float distance))
                    continue;

                closest = player;
                closestDistance = distance;
            }

            return closest;
        }

        private static bool IsInterruptTargetValid(EnemyAI enemy, PlayerControllerB player)
        {
            return player != null && !ShouldReleaseInterrupt(enemy, player);
        }

        private static bool IsInterruptCandidate(EnemyAI enemy, PlayerControllerB player, float maxDistance, out float distance)
        {
            distance = float.PositiveInfinity;
            if (enemy == null || player == null || player.isPlayerDead || player.isInHangarShipRoom)
                return false;

            if (!enemy.PlayerIsTargetable(player, cannotBeInShip: true, overrideInsideFactoryCheck: false, checkForMineshaftStartTile: false))
                return false;

            distance = Vector3.Distance(enemy.transform.position, player.transform.position);
            if (distance > maxDistance)
                return false;

            Vector3 lookPosition = player.gameplayCamera != null
                ? player.gameplayCamera.transform.position
                : player.transform.position + Vector3.up * 1.6f;

            int range = Mathf.CeilToInt(Mathf.Max(maxDistance + 2f, GetInterruptRange(enemy)));
            return enemy.CheckLineOfSightForPosition(lookPosition, 80f, range, 0f);
        }

        private static float GetInterruptRange(EnemyAI enemy)
        {
            float configured = Mathf.Max(0.5f, ShipSystemsBootstrap.BatterySabotageInterruptRange?.Value ?? 8f);
            string name = GetEnemyName(enemy);
            if (name.IndexOf("bundy", StringComparison.OrdinalIgnoreCase) >= 0)
                return Mathf.Min(configured, 5f);
            return configured;
        }

        private static bool IsBatteryAvailable()
        {
            ShipSystemsController controller = ShipSystemsController.Instance;
            return controller != null &&
                   controller.IsBatterySabotageAvailable() &&
                   controller.TryGetBatterySabotagePosition(out _);
        }

        private static SabotageState GetOrCreateState(EnemyAI enemy)
        {
            int key = enemy.GetInstanceID();
            if (!States.TryGetValue(key, out SabotageState state))
            {
                state = new SabotageState { Enemy = enemy };
                States[key] = state;
            }
            return state;
        }

        private static SabotageState GetState(EnemyAI enemy)
        {
            if (enemy == null)
                return null;

            States.TryGetValue(enemy.GetInstanceID(), out SabotageState state);
            return state;
        }

        private static bool IsAdditionalAttacker(EnemyAI enemy) => enemy != null && !enemy.isEnemyDead && enemy.stunNormalizedTimer <= 0f &&
            BatteryAttackerNames.Matches(ShipSystemsBootstrap.AdditionalBatteryAttackers?.Value, GetEnemyName(enemy), enemy.GetType().Name);

        private static bool IsSaboteurType(EnemyAI enemy)
        {
            if (enemy == null || enemy.isEnemyDead)
                return false;

            if (enemy is MaskedPlayerEnemy)
                return true;

            string name = GetEnemyName(enemy);
            return name.IndexOf("bundy", StringComparison.OrdinalIgnoreCase) >= 0 ||
                   name.IndexOf("masked", StringComparison.OrdinalIgnoreCase) >= 0 ||
                   name.IndexOf("mimic", StringComparison.OrdinalIgnoreCase) >= 0;
        }

        private static string GetEnemyName(EnemyAI enemy)
        {
            if (enemy == null) return "enemy";
            if (enemy.enemyType != null && !string.IsNullOrWhiteSpace(enemy.enemyType.enemyName))
                return enemy.enemyType.enemyName;
            return enemy.name ?? "enemy";
        }

        private static string PlayerLabel(PlayerControllerB player)
        {
            if (player == null) return "<null>";
            return !string.IsNullOrWhiteSpace(player.playerUsername)
                ? player.playerUsername
                : $"player {player.playerClientId}";
        }

        private static void LogVerbose(string message)
        {
            if (ShipSystemsBootstrap.VerboseLogging?.Value == true)
                ShipSystemsBootstrap.Log.LogInfo("[LGUShipSystems] " + message);
        }
    }

    [HarmonyPatch]
    internal static class BatterySabotagePatches
    {
        [HarmonyPostfix]
        [HarmonyPatch(typeof(MaskedPlayerEnemy), "Start")]
        private static void RollMaskedBatteryTarget(MaskedPlayerEnemy __instance)
        {
            try
            {
                BatterySabotageDirector.TryRollBatteryTarget(__instance);
            }
            catch (Exception ex)
            {
                ShipSystemsBootstrap.Log.LogWarning($"[LGUShipSystems] Masked battery target roll failed: {ex.Message}");
            }
        }

        [HarmonyPrefix]
        [HarmonyPatch(typeof(MaskedPlayerEnemy), "DoAIInterval")]
        private static bool DriveMaskedBatteryTarget(MaskedPlayerEnemy __instance)
        {
            try
            {
                return !TryDriveMaskedBatteryTarget(__instance);
            }
            catch (Exception ex)
            {
                ShipSystemsBootstrap.Log.LogWarning($"[LGUShipSystems] Masked battery routing failed: {ex.Message}");
                return true;
            }
        }

        private static bool TryDriveMaskedBatteryTarget(MaskedPlayerEnemy masked)
        {
            if (masked == null || !masked.IsOwner || masked.isEnemyDead)
                return false;

            if (!BatterySabotageDirector.IsBatteryTargetActive(masked) &&
                !BatterySabotageDirector.TryRollBatteryTarget(masked))
            {
                return false;
            }

            if (BatterySabotageDirector.TryGetInterruptTarget(masked, out PlayerControllerB interruptPlayer))
            {
                if (BatterySabotageDirector.ShouldReleaseInterrupt(masked, interruptPlayer))
                {
                    BatterySabotageDirector.ClearInterrupt(masked);
                    masked.targetPlayer = null;
                    masked.movingTowardsTargetPlayer = false;
                }
                else
                {
                    masked.SetMovingTowardsTargetPlayer(interruptPlayer);
                    masked.SwitchToBehaviourState(1);
                    return false;
                }
            }

            if (!BatterySabotageDirector.TryGetBatteryDestination(masked, out Vector3 destination))
                return false;

            if (masked.searchForPlayers != null && masked.searchForPlayers.inProgress)
                masked.StopSearch(masked.searchForPlayers);

            masked.targetPlayer = null;
            masked.movingTowardsTargetPlayer = false;
            masked.SwitchToBehaviourState(0);
            masked.SetDestinationToPosition(destination, checkForPath: false);

            if (masked.agent != null && masked.agent.enabled && masked.agent.isOnNavMesh)
            {
                float distance = Vector3.Distance(masked.transform.position, destination);
                // Walk all the way onto the sabotage stand so attack swings
                // visibly land on the battery instead of stopping metres away.
                if (distance <= 1.15f)
                {
                    masked.agent.isStopped = true;
                    masked.agent.ResetPath();
                }
                else
                {
                    masked.agent.isStopped = false;
                    masked.agent.speed = distance > 12f ? 7.5f : 3.8f;
                    masked.agent.SetDestination(destination);
                }
            }

            return true;
        }
    }
}
