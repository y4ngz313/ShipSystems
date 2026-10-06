using System;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.HighDefinition;
using UnityEngine.Rendering.RendererUtils;

namespace Y4NGZCompany.ShipSystems.Fuel
{
    /// <summary>The ship's original animated SpriteRenderer and material, clipped by draw state.</summary>
    internal sealed class VanillaChuteFlame : MonoBehaviour
    {
        internal const uint FlameRenderingLayer = 1u << 31;
        private Animator _animator;
        private SpriteRenderer _sprite;
        private Material _material;
        private Sprite _lastSprite;
        private Color _nativeEmission, _nativeColor;
        private bool _hasEmission, _hasColor;
        private Light _rimLight;
        private HDAdditionalLightData _rimLightData;
        private CustomPassVolume _volume;
        private NativeFlamePass _pass;
        private int _ownerInstanceId;
        private float _strength, _baseHeight, _baseWidth;
        internal bool IsInitialized => _ownerInstanceId == GetInstanceID() && _animator != null && _sprite != null;
        internal int FrameChanges { get; private set; }
        internal int DrawCalls => _pass?.DrawCalls ?? 0;
        internal bool IsVisible => _sprite != null && _sprite.enabled;
        internal string NativeShader => _material?.shader?.name ?? "missing";

        internal void Initialize(Transform source)
        {
            _ownerInstanceId = GetInstanceID();
            SpriteRenderer original = source.GetComponent<SpriteRenderer>();
            Animator animator = source.GetComponent<Animator>();
            if (original?.sprite == null || original.sharedMaterial == null || animator?.runtimeAnimatorController == null)
                throw new InvalidOperationException("Ship thruster has no sprite/material/controller");
            _material = new Material(original.sharedMaterial) { name = "FuelAcceptance_" + original.sharedMaterial.name };
            _hasEmission = _material.HasProperty("_EmissiveColor");
            _hasColor = _material.HasProperty("_UnlitColor");
            if (_hasEmission) _nativeEmission = _material.GetColor("_EmissiveColor");
            if (_hasColor) _nativeColor = _material.GetColor("_UnlitColor");
            var driver = new GameObject("NativeThrusterAnimation");
            driver.transform.SetParent(transform, false);
            driver.layer = 6;
            _sprite = driver.AddComponent<SpriteRenderer>();
            _sprite.sprite = original.sprite;
            _sprite.sharedMaterial = _material;
            _sprite.color = original.color;
            _sprite.flipX = original.flipX; _sprite.flipY = original.flipY;
            _sprite.shadowCastingMode = ShadowCastingMode.Off;
            _sprite.receiveShadows = false;
            _sprite.allowOcclusionWhenDynamic = false;
            _sprite.renderingLayerMask = FlameRenderingLayer | 1u;
            _sprite.enabled = false;
            _animator = driver.AddComponent<Animator>();
            _animator.runtimeAnimatorController = animator.runtimeAnimatorController;
            _animator.cullingMode = AnimatorCullingMode.AlwaysAnimate;
            _animator.fireEvents = false;
            _animator.enabled = false;
            _baseHeight = original.sprite.bounds.size.y;
            _baseWidth = original.sprite.bounds.size.x;
            var rim = new GameObject("FuelAcceptanceRimLight");
            rim.transform.SetParent(transform, false);
            _rimLight = rim.AddComponent<Light>();
            _rimLight.type = LightType.Point;
            _rimLight.color = new Color(1f, .35f, .065f);
            _rimLight.range = 1.15f;
            _rimLight.shadows = LightShadows.None;
            _rimLightData = rim.AddComponent<HDAdditionalLightData>();
            _volume = gameObject.AddComponent<CustomPassVolume>();
            _volume.isGlobal = true;
            _volume.injectionPoint = CustomPassInjectionPoint.BeforeTransparent;
            _pass = new NativeFlamePass { name = "Fuel chute native thruster", Owner = this };
            _volume.customPasses.Add(_pass);
            SetEnvelope(0f);
        }

        internal void SetEnvelope(float strength)
        {
            if (!IsInitialized) return;
            _strength = Mathf.Clamp01(strength);
            bool visible = _strength > .001f;
            _sprite.enabled = visible;
            _volume.enabled = visible;
            // Only brightness changes during burnout. The original shader, textures,
            // keywords, alpha cutoff, blend mode and emissive hue remain intact.
            if (_hasEmission) _material.SetColor("_EmissiveColor", _nativeEmission * _strength);
            if (_hasColor) _material.SetColor("_UnlitColor", new Color(_nativeColor.r * _strength, _nativeColor.g * _strength, _nativeColor.b * _strength, _nativeColor.a));
            _rimLight.enabled = visible;
            _rimLightData.SetIntensity(40f * _strength, LightUnit.Lumen);
            float size = Mathf.Lerp(.28f, 1f, Mathf.Sqrt(_strength));
            transform.localScale = new Vector3(1.35f / Mathf.Max(.01f, _baseWidth), 1.10f / Mathf.Max(.01f, _baseHeight), 1f) * size;
        }

        internal void Restart()
        {
            if (!IsInitialized) return;
            _animator.Rebind(); _animator.Update(0f);
            _lastSprite = null; FrameChanges = 0; _pass.DrawCalls = 0;
            CountFrame();
        }

        private void LateUpdate()
        {
#if UNITY_EDITOR
            Camera observer = Camera.main;
#else
            Camera observer = GameNetworkManager.Instance?.localPlayerController?.gameplayCamera;
#endif
            Tick(Time.deltaTime, observer);
        }

        internal void Tick(float deltaTime, Camera observer)
        {
            if (!IsInitialized || _strength <= .001f) return;
            FaceCamera(observer);
            Vector3 up = transform.parent != null ? transform.parent.up : Vector3.up;
            _rimLight.transform.position = transform.position + up * .30f;
            _animator.Update(Mathf.Max(0f, deltaTime));
            CountFrame();
        }

        private void FaceCamera(Camera observer)
        {
            if (observer == null) return;
            Vector3 direction = transform.position - observer.transform.position;
            if (direction.sqrMagnitude > .0001f)
                transform.rotation = Quaternion.LookRotation(direction, observer.transform.up);
        }
        private void CountFrame()
        {
            if (_sprite.sprite != null && _sprite.sprite != _lastSprite) { _lastSprite = _sprite.sprite; FrameChanges++; }
        }
        private void OnDisable() { if (_volume != null) SetEnvelope(0f); }
        private void OnDestroy()
        {
            if (_ownerInstanceId != GetInstanceID()) return;
            if (_material != null) Destroy(_material);
        }

        [Serializable]
        internal sealed class NativeFlamePass : CustomPass
        {
            internal VanillaChuteFlame Owner;
            internal int DrawCalls;
            private static readonly ShaderTagId[] Passes = { new ShaderTagId("ForwardOnly"), new ShaderTagId("Forward"), new ShaderTagId("SRPDefaultUnlit") };
#if Y4NGZCOMPANY_CUSTOMPASS_PUBLIC
            public override void Execute(CustomPassContext ctx)
#else
            protected override void Execute(CustomPassContext ctx)
#endif
            {
                if (Owner == null || !Owner.IsInitialized || !Owner.IsVisible) return;
                // The physical floor hides the normal native draw. Repeat that same
                // shader after opaque geometry, admitting only the chute opening bit.
                // No override shader/material or resampled render texture is involved.
                var state = new RenderStateBlock(RenderStateMask.Depth | RenderStateMask.Stencil | RenderStateMask.Raster)
                {
                    depthState = new DepthState(false, CompareFunction.Always),
                    rasterState = new RasterState(CullMode.Off),
                    stencilReference = 64,
                    stencilState = new StencilState(true, 64, 0, CompareFunction.Equal, StencilOp.Keep, StencilOp.Keep, StencilOp.Keep)
                };
                var list = new RendererListDesc(Passes, ctx.cullingResults, ctx.hdCamera.camera)
                {
                    renderQueueRange = RenderQueueRange.all,
                    sortingCriteria = SortingCriteria.CommonOpaque,
                    layerMask = 1 << 6,
                    renderingLayerMask = FlameRenderingLayer,
                    stateBlock = state
                };
                CoreUtils.DrawRendererList(ctx.renderContext, ctx.cmd, ctx.renderContext.CreateRendererList(list));
                DrawCalls++;
            }
        }
    }
}
