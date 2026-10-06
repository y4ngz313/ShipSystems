using UnityEngine;

namespace Y4NGZCompany.ShipSystems.Power
{
    /// <summary>Cosmetic fall into a measured ground pose; leaves the repair target stable.</summary>
    internal sealed class BatteryDropMotion : MonoBehaviour
    {
        private Vector3 _start, _end;
        private Quaternion _from, _to;
        private float _elapsed;
        internal bool HasLanded => _elapsed >= .65f;
        internal void Initialize(Vector3 start, Quaternion rotation, Vector3 end, Quaternion target)
        {
            _start = start; _end = end; _from = rotation; _to = target;
            transform.SetPositionAndRotation(start, rotation);
        }
        private void Update()
        {
            _elapsed += Time.deltaTime;
            float t = Mathf.Clamp01(_elapsed / .65f);
            Vector3 position = Vector3.Lerp(_start, _end, t);
            position.y = Mathf.Lerp(_start.y, _end.y, t * t);
            transform.SetPositionAndRotation(position, Quaternion.Slerp(_from, _to, t));
            if (t >= 1f) enabled = false;
        }
    }
}
