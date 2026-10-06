using UnityEngine;
using UnityEngine.Rendering.HighDefinition;
namespace Y4NGZCompany.ShipSystems.Power
{
    /// <summary>Original green tubes and nearby surfaces share a restrained heartbeat.</summary>
    internal sealed class BatteryHeartbeat : MonoBehaviour
    {
        private Renderer _renderer;
        private int _slot = -1;
        private bool _working = true;
        private MaterialPropertyBlock _block;
        private readonly Light[] _lights = new Light[2];
        private readonly HDAdditionalLightData[] _lightData = new HDAdditionalLightData[2];
        private void Awake() { EnsureInitialized(); }
        private void EnsureInitialized()
        {
            if (_block != null) return;
            _block = new MaterialPropertyBlock();
            _renderer = GetComponent<Renderer>();
            if (_renderer == null) return;
            var materials = _renderer.sharedMaterials;
            for (int i=0;i<materials.Length;i++) if (materials[i] != null && materials[i].name.StartsWith("ShipBattery_Tubes", System.StringComparison.Ordinal)) _slot=i;
            if (_slot < 0) return;
            // Anchor the small spill lights inside the actual rod submesh. Model bounds
            // include the end caps and place lights on surrounding walls after rotation.
            var filter = GetComponent<MeshFilter>();
            if (filter == null || filter.sharedMesh == null) return;
            var mesh = filter.sharedMesh;
            var indices = mesh.GetIndices(_slot);
            if (indices.Length == 0) return;
            var vertices = mesh.vertices;
            Bounds coils = new Bounds(vertices[indices[0]], Vector3.zero);
            foreach (int index in indices) coils.Encapsulate(vertices[index]);
            Vector3 axis = coils.size.y >= coils.size.x && coils.size.y >= coils.size.z ? Vector3.up
                : coils.size.x >= coils.size.z ? Vector3.right : Vector3.forward;
            float halfLength = Vector3.Dot(coils.extents, axis);
            for (int i=0;i<2;i++)
            {
                string name = "BatteryHeartbeatLight" + i;
                Transform existing = transform.Find(name);
                GameObject go = existing != null ? existing.gameObject : new GameObject(name);
                go.transform.SetParent(transform, false);
                go.transform.localPosition = coils.center + axis * ((i == 0 ? -1f : 1f) * halfLength * .35f);
                _lights[i] = go.GetComponent<Light>();
                if (_lights[i] == null) _lights[i] = go.AddComponent<Light>();
                _lights[i].type = LightType.Point;
                _lights[i].color = new Color(.3f, .85f, .12f);
                _lights[i].range = .55f;
                _lights[i].shadows = LightShadows.None;
                _lightData[i] = go.GetComponent<HDAdditionalLightData>();
                if (_lightData[i] == null) _lightData[i] = go.AddComponent<HDAdditionalLightData>();
            }
        }
        internal void SetWorking(bool working) { EnsureInitialized(); _working=working; ApplyAt(Time.time); enabled=working; }
        private void Update() { ApplyAt(Time.time); }
        private void OnDisable() { foreach(var light in _lights) if(light != null) light.enabled=false; }
        internal void ApplyAt(float seconds)
        {
            EnsureInitialized();
            if (_renderer == null || _slot < 0) return;
            float phase = Mathf.Repeat(seconds, 2.4f);
            float beat = Mathf.Exp(-Mathf.Pow((phase-.18f)/.1f,2f))
                + .45f*Mathf.Exp(-Mathf.Pow((phase-.43f)/.12f,2f));
            Color emission = _working ? new Color(.52f,.64f,.4f,1f) * (1f + .06f*beat) : Color.black;
            _renderer.GetPropertyBlock(_block,_slot);
            _block.SetColor("_EmissiveColor",emission); _block.SetColor("_EmissionColor",emission);
            _renderer.SetPropertyBlock(_block,_slot);
            for(int i=0;i<_lights.Length;i++) if(_lights[i] != null)
            {
                _lights[i].enabled = _working;
                _lightData[i].SetIntensity(_working ? .35f + .15f*beat : 0f, LightUnit.Lumen);
            }
        }
    }
}
