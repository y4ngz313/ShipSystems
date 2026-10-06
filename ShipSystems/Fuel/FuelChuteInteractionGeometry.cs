using UnityEngine;

namespace Y4NGZCompany.ShipSystems.Fuel
{
    internal static class FuelChuteInteractionGeometry
    {
        internal static void Configure(Transform root, InteractTrigger trigger)
        {
            // Frame plus a shallow centered face. Objects resting on the lid sit above
            // the face, so vanilla selects the item before the chute.
            trigger.transform.SetParent(root, false);
            trigger.transform.localPosition = new Vector3(0f, .10f, 0f);
            trigger.transform.localRotation = Quaternion.identity;
            trigger.transform.localScale = Vector3.one;
            trigger.gameObject.layer = FuelRuntimeUtility.InteractLayer;
            FuelRuntimeUtility.SetTagSafe(trigger.gameObject, "InteractTrigger");
            foreach (Collider collider in trigger.GetComponents<Collider>()) collider.enabled = false;
            BoxCollider[] boxes = trigger.GetComponents<BoxCollider>();
            for (int i = 0; i < 4; i++)
            {
                BoxCollider box = i < boxes.Length ? boxes[i] : trigger.gameObject.AddComponent<BoxCollider>();
                bool side = i >= 2;
                float offset = i % 2 == 0 ? .65f : -.65f;
                box.center = side ? new Vector3(offset, 0, 0) : new Vector3(0, 0, offset);
                box.size = side ? new Vector3(.22f, .18f, 1.08f) : new Vector3(1.52f, .18f, .22f);
                box.isTrigger = true;
                box.enabled = true;
            }
            BoxCollider face = boxes.Length > 4 ? boxes[4] : trigger.gameObject.AddComponent<BoxCollider>();
            face.center = new Vector3(0f, -.062f, 0f); // root-local .038, just above lid support
            face.size = new Vector3(1.08f, .006f, 1.08f);
            face.isTrigger = true;
            face.enabled = true;
            // Vanilla resolves InteractTrigger through hit.transform, which otherwise may
            // refer to a parent Rigidbody added by furniture/ship mods.
            Rigidbody body = trigger.GetComponent<Rigidbody>();
            if (body == null) body = trigger.gameObject.AddComponent<Rigidbody>();
            body.isKinematic = true;
            body.useGravity = false;
        }
    }
}
