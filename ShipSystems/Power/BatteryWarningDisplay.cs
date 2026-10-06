using System;
using System.Collections.Generic;
using TMPro;
using UnityEngine;
using UnityEngine.UI;
using UnityEngine.Rendering;
using UnityEngine.Rendering.HighDefinition;
using Y4NGZCompany.Core.Compat;
using Y4NGZCompany.ShipSystems.Fuel;
using Y4NGZCompany.ShipSystems.Rendering;
using Y4NGZCore.Modules.Monitor;

namespace Y4NGZCompany.ShipSystems.Power
{
    internal sealed class BatteryWarningDisplay
    {
        private const int RenderWidth = 512, RenderHeight = 256;
        private const string Owner = "Y4NGZShipSystems:BatteryWarning";
        private readonly List<MonitorLease> _leases = new List<MonitorLease>();
        private readonly List<GeneralImprovementsMonitorPropertyBlock> _screens = new List<GeneralImprovementsMonitorPropertyBlock>();
        private readonly int _ownerToken = GeneralImprovementsMonitorPropertyBlock.NextOwnerToken();
        private GameObject _rigRoot;
        private Camera _rigCamera;
        private Canvas _canvas;
        private RenderTexture _renderTexture;
        private TextMeshProUGUI _rigText;
        private float _until, _nextBind;
        private string _message;

        internal void Pulse(bool critical)
        {
            _until = Time.unscaledTime + 1.5f;
            _message = critical ? "WARNING\n<size=55%>BATTERY CRITICAL</size>" : "WARNING\n<size=55%>BATTERY UNDER ATTACK</size>";
            EnsureRig();
            if (_rigText.text != _message) { _rigText.text = _message; RenderNow(); }
            if (_leases.Count == 0)
            {
                // Claim every overlapping logical surface so the old owner releases before
                // we snapshot. Surface registrations belong to the arbiter, not this pulse.
                foreach (string surface in MonitorSurfaces.All)
                {
                    if (!MonitorArbiter.IsSurfaceRegistered(surface)) MonitorArbiter.RegisterSurface(surface, new SurfaceBinding(surface));
                    var lease = MonitorArbiter.Acquire(new MonitorLeaseRequest(Owner, surface, MonitorPriority.Safety,
                        reason: "battery attack warning", displaced: ReleaseScreens), out var outcome);
                    if (lease != null) _leases.Add(lease);
                }
            }
            Tick(false);
        }

        internal void Tick(bool powerFailed)
        {
            if (_leases.Count == 0) return;
            if (powerFailed || Time.unscaledTime >= _until) { Stop(); return; }
            foreach (var lease in _leases) if (!lease.IsActive) { ReleaseScreens(); return; }
            if (Time.unscaledTime >= _nextBind)
            {
                _nextBind = Time.unscaledTime + .25f;
                var found = new List<(MeshRenderer renderer, int index)>();
                if (GeneralImprovementsMonitors.BetterMonitorsActive)
                {
                    found.AddRange(GeneralImprovementsMonitors.EnumerateAllSurfaces());
                }
                else if (StartOfRound.Instance?.elevatorTransform != null)
                {
                    foreach (var renderer in StartOfRound.Instance.elevatorTransform.GetComponentsInChildren<MeshRenderer>(true))
                    {
                        if (!renderer.enabled || !renderer.gameObject.activeInHierarchy) continue;
                        bool wall = false;
                        for (Transform t = renderer.transform; t != null; t = t.parent)
                            if (t.name.IndexOf("Monitor", StringComparison.OrdinalIgnoreCase) >= 0) { wall = true; break; }
                        if (!wall) continue;
                        var materials = renderer.sharedMaterials;
                        for (int i = 0; i < materials.Length; i++)
                            if (materials[i] != null && (materials[i].mainTexture is RenderTexture
                                || materials[i].name.IndexOf("Screen", StringComparison.OrdinalIgnoreCase) >= 0)) found.Add((renderer,i));
                    }
                }
                bool changed = found.Count != _screens.Count;
                for (int i = 0; !changed && i < found.Count; i++) changed |= _screens[i].Renderer != found[i].renderer || _screens[i].MaterialIndex != found[i].index;
                if (changed)
                {
                    ReleaseScreens();
                    foreach (var screen in found) _screens.Add(new GeneralImprovementsMonitorPropertyBlock(screen.renderer,screen.index,_ownerToken));
                }
            }
            foreach (var screen in _screens) screen.Apply(_renderTexture, Vector2.one, Vector2.zero);
        }

        private void ReleaseScreens() { foreach (var screen in _screens) screen.Release(); _screens.Clear(); }
        internal void Stop()
        {
            ReleaseScreens();
            foreach (var lease in _leases) MonitorArbiter.Release(lease);
            _leases.Clear(); _until = 0; _nextBind = 0;
        }
        internal void Destroy() { Stop(); DestroyRig(); }

        private void RenderNow()
        {
            if (_rigCamera == null || _canvas == null) return;
            _canvas.enabled = true;
            try
            {
                Canvas.ForceUpdateCanvases();
                _rigText?.ForceMeshUpdate(true, true);
                _rigCamera.Render();
            }
            finally
            {
                _canvas.enabled = false;
            }
        }

        // ---- offscreen rig (GI fallback only) --------------------------------------------

        private void EnsureRig()
        {
            if (_rigRoot != null &&
                _rigCamera != null &&
                _canvas != null &&
                _renderTexture != null &&
                _renderTexture.IsCreated())
            {
                return;
            }

            DestroyRig();

            FuelMonitorHost.Style style = FuelMonitorHost.ResolveStyle(null);

            _rigRoot = new GameObject("Y4NGZ_BatteryWarningRig");
            UnityEngine.Object.DontDestroyOnLoad(_rigRoot);
            // Separate from the fuel rig so simultaneous manual renders cannot overlap.
            _rigRoot.transform.position = new Vector3(
                OffscreenRigParking.SlotX(OffscreenRigSlot.BatteryWarning),
                OffscreenRigParking.ParkingHeight,
                0f);

            _renderTexture = new RenderTexture(RenderWidth, RenderHeight, 0, RenderTextureFormat.ARGB32)
            {
                name = "Y4NGZ_BatteryWarningRT",
                filterMode = FilterMode.Bilinear,
                wrapMode = TextureWrapMode.Clamp,
                antiAliasing = 1
            };
            if (!_renderTexture.Create())
            {
                DestroyRig();
                throw new InvalidOperationException("Battery warning RenderTexture.Create() failed.");
            }

            var cameraGo = new GameObject("BatteryWarningCamera");
            cameraGo.transform.SetParent(_rigRoot.transform, false);
            cameraGo.transform.localPosition = new Vector3(0f, 0f, -10f);
            _rigCamera = cameraGo.AddComponent<Camera>();
            _rigCamera.clearFlags = CameraClearFlags.SolidColor;
            _rigCamera.backgroundColor = new Color(.025f,.015f,.008f,1f);
            _rigCamera.orthographic = true;
            _rigCamera.orthographicSize = RenderHeight * 0.5f;
            _rigCamera.aspect = RenderWidth / (float)RenderHeight;
            _rigCamera.nearClipPlane = 0.1f;
            _rigCamera.farClipPlane = 30f;
            _rigCamera.cullingMask = 1 << 5;
            _rigCamera.targetTexture = _renderTexture;
            _rigCamera.enabled = false;

            // Under HDRP a manually driven camera without HDAdditionalCameraData cannot
            // resolve into its target texture and stomps the backbuffer with its clear
            // colour instead. Same treatment the quota announcement rig uses.
            HDAdditionalCameraData hdrp = cameraGo.AddComponent<HDAdditionalCameraData>();
            hdrp.clearColorMode = HDAdditionalCameraData.ClearColorMode.Color;
            hdrp.backgroundColorHDR = _rigCamera.backgroundColor;
            hdrp.volumeLayerMask = 0;
            Y4NGZCompany.ShipSystems.Rendering.CameraRenderProfile.ApplyProfile(
                _rigCamera, Y4NGZCompany.ShipSystems.Rendering.CameraRenderRole.Unlit);

            var canvasGo = new GameObject("BatteryWarningCanvas", typeof(RectTransform));
            canvasGo.transform.SetParent(_rigRoot.transform, false);
            canvasGo.layer = 5;
            _canvas = canvasGo.AddComponent<Canvas>();
            _canvas.renderMode = RenderMode.WorldSpace;
            _canvas.worldCamera = _rigCamera;
            _canvas.enabled = false;
            RectTransform canvasRt = canvasGo.GetComponent<RectTransform>();
            canvasRt.sizeDelta = new Vector2(RenderWidth, RenderHeight);

            Image background = canvasGo.AddComponent<Image>();
            background.color = new Color(.025f,.015f,.008f,1f);
            background.raycastTarget = false;

            // One plain text block, exactly like a GI text monitor: no header rule, no gauge,
            // no status strip. Auto-sizing stands in for GI's hand-tuned per-screen font size,
            // which is not readable from outside its prefab.
            var textGo = new GameObject("Readout", typeof(RectTransform));
            textGo.transform.SetParent(canvasRt, false);
            textGo.layer = 5;
            _rigText = textGo.AddComponent<TextMeshProUGUI>();
            if (style.Font != null) _rigText.font = style.Font;
            if (style.Sprites != null) _rigText.spriteAsset = style.Sprites;
            _rigText.color = new Color(1f,.55f,.12f,1f);
            _rigText.alignment = TextAlignmentOptions.Center;
            _rigText.raycastTarget = false;
            _rigText.enableWordWrapping = true;
            _rigText.richText = true;
            _rigText.enableAutoSizing = true;
            _rigText.fontSizeMin = 18f;
            _rigText.fontSizeMax = 72f;
            _rigText.margin = new Vector4(16f, 12f, 16f, 12f);
            _rigText.text = string.Empty;

            RectTransform textRt = _rigText.rectTransform;
            textRt.anchorMin = Vector2.zero;
            textRt.anchorMax = Vector2.one;
            textRt.offsetMin = Vector2.zero;
            textRt.offsetMax = Vector2.zero;

        }


        private void DestroyRig()
        {
            if (_renderTexture != null)
            {
                _renderTexture.Release();
                UnityEngine.Object.Destroy(_renderTexture);
                _renderTexture = null;
            }
            if (_rigRoot != null)
            {
                UnityEngine.Object.Destroy(_rigRoot);
                _rigRoot = null;
            }
            _rigCamera = null;
            _canvas = null;
            _rigText = null;
        }

        private sealed class SurfaceBinding : IMonitorSurfaceBinding
        {
            private readonly string _surface;
            internal SurfaceBinding(string surface) { _surface = surface; }
            public bool IsAvailable => StartOfRound.Instance?.elevatorTransform != null;
            public MonitorSurfaceInfo Info => new MonitorSurfaceInfo(_surface, MonitorSurfaceBacking.Vanilla, detail: "ship monitor warning");
            public object CaptureSnapshot() => null;
            public void RestoreSnapshot(object snapshot) { }
            public void Apply(MonitorLease lease) { }
        }
    }
}
