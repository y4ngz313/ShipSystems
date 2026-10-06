using UnityEngine;
using UnityEngine.AI;
namespace Y4NGZCompany.ShipSystems.Power
{
    internal static class BatterySabotagePosition
    {
        internal static bool TryResolve(Transform battery, Vector3 socket, out Vector3 stand)
        {
            // A model's local down can point sideways after saved rotations. Start
            // below roof height and find actual floor before sampling the NavMesh.
            stand = socket - Vector3.up * 1.1f;
            Vector3 forward = Vector3.ProjectOnPlane(battery.forward, Vector3.up);
            if (forward.sqrMagnitude < .05f) forward = Vector3.forward;
            forward.Normalize();
            int mask = StartOfRound.Instance != null ? StartOfRound.Instance.collidersAndRoomMaskAndDefault : Physics.DefaultRaycastLayers;
            for (int ring = 0; ring < 2; ring++) for (int direction = 0; direction < 8; direction++)
            {
                Vector3 offset = Quaternion.Euler(0f, direction * 45f, 0f) * forward * (.8f + ring * .6f);
                Vector3 origin = socket + offset + Vector3.up * .25f;
                foreach (var floor in Physics.RaycastAll(origin, Vector3.down, 3f, mask, QueryTriggerInteraction.Ignore))
                {
                    if (floor.normal.y < .65f || floor.transform.IsChildOf(battery)) continue;
                    if (!NavMesh.SamplePosition(floor.point, out var nav, .6f, NavMesh.AllAreas)) continue;
                    // SamplePosition can select a different storey. Only accept the
                    // nearby walk surface, never the roof or a remote fallback.
                    if (Mathf.Abs(nav.position.y - floor.point.y) > .35f) continue;
                    if (Vector3.Distance(nav.position + Vector3.up * 1.35f, socket) > 2.4f) continue;
                    stand = nav.position;
                    return true;
                }
            }
            return false;
        }
    }
}
