using UnityEngine;
using Y4NGZCompany.Bootstrap;
using Y4NGZCompany.ShipSystems.Layout;
namespace Y4NGZCompany.ShipSystems.Power
{
    internal sealed partial class ExternalBatteryStation
    {
        private BoxCollider _damageCollider;
        internal Vector3 DamagePoint(Vector3 origin) => _damageCollider != null ? _damageCollider.ClosestPoint(origin) : _socketWorldPosition;
        internal bool DamageAvailable => _root != null && _damageCollider != null && _damageCollider.enabled;
        private void BuildDamageReceiver()
        {
            var hit = new GameObject("BatteryDamageReceiver");
            hit.transform.SetParent(_root.transform, false);
            hit.layer = 19; // Vanilla shovel/knife hit mask, without an EnemyAI impostor.
            _damageCollider = hit.AddComponent<BoxCollider>();
            _damageCollider.isTrigger = true;
            if (BatteryAssetLoader.TryGetLocalRendererBounds(_root.transform, _batteryBody, out Bounds bounds))
            { _damageCollider.center = bounds.center; _damageCollider.size = bounds.size; }
            hit.AddComponent<BatteryDamageReceiver>().Station = this;
            UpdateDamageReceiver();
        }
        private void UpdateDamageReceiver()
        {
            if (_damageCollider != null) _damageCollider.enabled = !_controller.BatteryBroken;
        }
        internal bool CanHitFrom(Vector3 origin, float reach)
        {
            if (!DamageAvailable || !IsFinite(origin)) return false;
            Vector3 end = DamagePoint(origin);
            if (Vector3.Distance(origin, end) > reach) return false;
            int mask = StartOfRound.Instance != null ? StartOfRound.Instance.collidersAndRoomMaskAndDefault : ~0;
            return !Physics.Linecast(origin, end, mask, QueryTriggerInteraction.Ignore);
        }
        private static bool IsFinite(Vector3 p) => !float.IsNaN(p.x) && !float.IsNaN(p.y) && !float.IsNaN(p.z)
            && !float.IsInfinity(p.x) && !float.IsInfinity(p.y) && !float.IsInfinity(p.z);
        internal bool ApplyDamage(int damage, string reason, bool sabotage = false)
        {
            if (!ShipSystemsController.IsServerRuntime() || !DamageAvailable || damage <= 0) return false;
            DecaySabotageHits();
            _sabotageHits += Mathf.Clamp(damage, 1, 100);
            _lastSabotageHitAt = Time.unscaledTime;
            int required = GetRequiredSabotageHits();
            bool critical = _sabotageHits * 2 >= required;
            ShipSystemsBootstrap.Log?.LogInfo($"[LGUShipSystems.BatteryDamage] {reason}: damage={damage}, accumulated={_sabotageHits}/{required}, critical={critical}.");
            if (sabotage) _controller.PlayBatterySabotageFeedback(_socketWorldPosition, _currentSaboteurs.Count, critical);
            else _controller.PlayBatteryDamageFeedback(critical);
            if (!_criticalAnnounced && critical)
            { _criticalAnnounced = true; _controller.NotifyBatteryCriticalFromServer(); }
            if (_sabotageHits >= required)
            {
                _sabotageHits = 0;
                _controller.BreakBattery(reason);
                UpdateDamageReceiver();
                ClearActiveSaboteurs();
            }
            return true;
        }
    }
}
