using System;
using GameNetcodeStuff;
using UnityEngine;
using Y4NGZCompany.Bootstrap;

namespace Y4NGZCompany.ShipSystems.Fuel
{
    internal sealed class FuelRefineryAnimationController : MonoBehaviour
    {
        private const float AnimationCycleSeconds = 4.2f;
        private const float RuntimeDiagnosticDelaySeconds = 5f;
        private const float VisibilityDistanceCheckIntervalSeconds = 0.5f;
        private const float VisibilityDistanceSqr = 60f * 60f;

        [SerializeField] private MeshFilter _beamFilter;
        [SerializeField] private MeshFilter _rodFilter;
        [SerializeField] private Mesh[] _beamFrames = Array.Empty<Mesh>();
        [SerializeField] private Mesh[] _rodFrames = Array.Empty<Mesh>();

        private Renderer _beamRenderer;
        private Renderer _rodRenderer;
        private Mesh _beamRuntimeMesh;
        private Mesh _rodRuntimeMesh;
        private Vector3[][] _beamFrameVertices = Array.Empty<Vector3[]>();
        private Vector3[][] _rodFrameVertices = Array.Empty<Vector3[]>();
        private Vector3[][] _beamFrameNormals = Array.Empty<Vector3[]>();
        private Vector3[][] _rodFrameNormals = Array.Empty<Vector3[]>();
        private Vector3[] _beamVertexBuffer = Array.Empty<Vector3>();
        private Vector3[] _rodVertexBuffer = Array.Empty<Vector3>();
        private Vector3[] _beamNormalBuffer = Array.Empty<Vector3>();
        private Vector3[] _rodNormalBuffer = Array.Empty<Vector3>();
        private bool _runtimePrepared;
        private float _nextPrepareRetryAt;
        private float _runtimeDiagnosticAt = -1f;
        private bool _runtimeDiagnosticLogged;
        private int _runtimeBlendTicks;
        private int _runtimeDirectFallbackTicks;
        private float _nextVisibilityDistanceCheckAt;
        private bool _localPlayerBeyondVisibilityDistance;

        private void Awake()
        {
            PrepareRuntimeMeshes();
        }

        private void OnEnable()
        {
            _nextPrepareRetryAt = 0f;
            _nextVisibilityDistanceCheckAt = 0f;
            PrepareRuntimeMeshes();
            ResetRuntimeDiagnostics();
        }

        internal void Configure(Mesh[] beamFrames, Mesh[] rodFrames)
        {
            _beamFrames = beamFrames ?? Array.Empty<Mesh>();
            _rodFrames = rodFrames ?? Array.Empty<Mesh>();
            _beamFilter = FindMeshFilter("Cube_006");
            _rodFilter = FindMeshFilter("Cylinder_044");
            _beamRenderer = null;
            _rodRenderer = null;
            CacheRendererReferences();

            _runtimePrepared = false;
            _nextPrepareRetryAt = 0f;
            _nextVisibilityDistanceCheckAt = 0f;
            PrepareRuntimeMeshes();
            ApplyFrameDirect(0);
            ResetRuntimeDiagnostics();

            int frameCount = Mathf.Min(_beamFrames.Length, _rodFrames.Length);
            bool canBlend = CanBlendFrames(_beamFrameVertices, frameCount) && CanBlendFrames(_rodFrameVertices, frameCount);
            enabled = frameCount > 0;
            ShipSystemsBootstrap.Log?.LogInfo($"[LGUShipSystems.Refinery] Refinery animation configured: beamTarget={_beamFilter != null}, rodTarget={_rodFilter != null}, smoothBlend={canBlend}, cycleSeconds={AnimationCycleSeconds:0.00}, beamFrames={_beamFrames.Length}, rodFrames={_rodFrames.Length}.");
            if (frameCount <= 0)
                ShipSystemsBootstrap.Log?.LogWarning("[LGUShipSystems.Refinery] Refinery animation has no matching beam/rod frames; pumpjack beam will stay static.");
            if ((_beamFrames.Length > 0 && _beamFilter == null) || (_rodFrames.Length > 0 && _rodFilter == null))
                ShipSystemsBootstrap.Log?.LogWarning("[LGUShipSystems.Refinery] Refinery animation frames were loaded but the visual mesh targets were not found.");
            if (frameCount > 0 && !canBlend)
                ShipSystemsBootstrap.Log?.LogWarning($"[LGUShipSystems.Refinery] Refinery animation frames do not share stable vertex counts; falling back to discrete frame stepping. beamProblem={DescribeFrameProblem(_beamFrames)} rodProblem={DescribeFrameProblem(_rodFrames)}.");
        }

        private void Update()
        {
            int frameCount = Mathf.Min(_beamFrames?.Length ?? 0, _rodFrames?.Length ?? 0);
            if (frameCount <= 0)
                return;

            if (ShouldSkipBlendWork())
            {
                LogRuntimeDiagnosticsIfReady();
                return;
            }

            PrepareRuntimeMeshes();

            float normalizedCycle = Mathf.Repeat(Time.time / AnimationCycleSeconds, 1f);
            float framePosition = normalizedCycle * frameCount;
            int fromFrame = Mathf.FloorToInt(framePosition) % frameCount;
            int toFrame = (fromFrame + 1) % frameCount;
            float blend = framePosition - Mathf.Floor(framePosition);

            bool beamBlended = BlendMeshFrame(
                _beamFilter,
                _beamRuntimeMesh,
                _beamFrames,
                _beamFrameVertices,
                _beamFrameNormals,
                ref _beamVertexBuffer,
                ref _beamNormalBuffer,
                fromFrame,
                toFrame,
                blend);
            bool rodBlended = BlendMeshFrame(
                _rodFilter,
                _rodRuntimeMesh,
                _rodFrames,
                _rodFrameVertices,
                _rodFrameNormals,
                ref _rodVertexBuffer,
                ref _rodNormalBuffer,
                fromFrame,
                toFrame,
                blend);

            if (!beamBlended || !rodBlended)
            {
                _runtimeDirectFallbackTicks++;
                ApplyFrameDirect(fromFrame);
            }
            else
            {
                _runtimeBlendTicks++;
            }

            LogRuntimeDiagnosticsIfReady();
        }

        private void OnDestroy()
        {
            if (_beamRuntimeMesh != null)
                UnityEngine.Object.Destroy(_beamRuntimeMesh);
            if (_rodRuntimeMesh != null)
                UnityEngine.Object.Destroy(_rodRuntimeMesh);
            _runtimePrepared = false;
            _nextPrepareRetryAt = 0f;
        }

        private void PrepareRuntimeMeshes()
        {
            int frameCount = Mathf.Min(_beamFrames?.Length ?? 0, _rodFrames?.Length ?? 0);
            if (frameCount <= 0)
                return;

            if (_beamFilter == null)
                _beamFilter = FindMeshFilter("Cube_006");
            if (_rodFilter == null)
                _rodFilter = FindMeshFilter("Cylinder_044");
            CacheRendererReferences();

            bool beamReady = _beamRuntimeMesh != null && CanBlendFrames(_beamFrameVertices, frameCount);
            bool rodReady = _rodRuntimeMesh != null && CanBlendFrames(_rodFrameVertices, frameCount);
            if (_runtimePrepared && beamReady && rodReady)
                return;

            // A bundle that can never blend would otherwise recache vertex
            // arrays every frame; retry at most every 5 seconds instead.
            if (!_runtimePrepared && _nextPrepareRetryAt > 0f && Time.unscaledTime < _nextPrepareRetryAt)
                return;

            PrepareRuntimeMesh(_beamFilter, _beamFrames, ref _beamRuntimeMesh, ref _beamFrameVertices, ref _beamFrameNormals);
            PrepareRuntimeMesh(_rodFilter, _rodFrames, ref _rodRuntimeMesh, ref _rodFrameVertices, ref _rodFrameNormals);

            beamReady = _beamRuntimeMesh != null && CanBlendFrames(_beamFrameVertices, frameCount);
            rodReady = _rodRuntimeMesh != null && CanBlendFrames(_rodFrameVertices, frameCount);
            _runtimePrepared = beamReady && rodReady;
            _nextPrepareRetryAt = _runtimePrepared ? 0f : Time.unscaledTime + 5f;
        }

        private void ResetRuntimeDiagnostics()
        {
            _runtimeDiagnosticAt = Time.time + RuntimeDiagnosticDelaySeconds;
            _runtimeDiagnosticLogged = false;
            _runtimeBlendTicks = 0;
            _runtimeDirectFallbackTicks = 0;
        }

        private void LogRuntimeDiagnosticsIfReady()
        {
            if (_runtimeDiagnosticLogged || _runtimeDiagnosticAt < 0f || Time.time < _runtimeDiagnosticAt)
                return;

            _runtimeDiagnosticLogged = true;
            ShipSystemsBootstrap.Log?.LogInfo($"[LGUShipSystems.Refinery] Refinery animation runtime diagnostic: blendedTicks={_runtimeBlendTicks}, directFallbackTicks={_runtimeDirectFallbackTicks}, beamFirst={FrameName(_beamFrames, 0)}, beamLast={FrameName(_beamFrames, (_beamFrames?.Length ?? 0) - 1)}, rodFirst={FrameName(_rodFrames, 0)}, rodLast={FrameName(_rodFrames, (_rodFrames?.Length ?? 0) - 1)}, beamRuntime={_beamRuntimeMesh != null}, rodRuntime={_rodRuntimeMesh != null}, beamShared={MeshName(_beamFilter)}, rodShared={MeshName(_rodFilter)}.");
        }

        private static string FrameName(Mesh[] frames, int index)
        {
            if (frames == null || index < 0 || index >= frames.Length || frames[index] == null)
                return "<none>";
            return frames[index].name;
        }

        private static string MeshName(MeshFilter filter)
        {
            return filter != null && filter.sharedMesh != null ? filter.sharedMesh.name : "<none>";
        }

        private static void PrepareRuntimeMesh(
            MeshFilter filter,
            Mesh[] frames,
            ref Mesh runtimeMesh,
            ref Vector3[][] vertices,
            ref Vector3[][] normals)
        {
            vertices = CacheFrameVectors(frames, mesh => mesh.vertices);
            normals = CacheFrameVectors(frames, mesh => mesh.normals);
            if (filter == null || frames == null || frames.Length == 0 || frames[0] == null)
                return;

            if (runtimeMesh == null || runtimeMesh.vertexCount != frames[0].vertexCount)
            {
                if (runtimeMesh != null)
                    UnityEngine.Object.Destroy(runtimeMesh);
                runtimeMesh = UnityEngine.Object.Instantiate(frames[0]);
                runtimeMesh.name = frames[0].name + "_SmoothRuntime";
                runtimeMesh.MarkDynamic();
            }

            filter.sharedMesh = runtimeMesh;
        }

        private bool ShouldSkipBlendWork()
        {
            CacheRendererReferences();
            bool bothRenderersHidden =
                _beamRenderer != null &&
                _rodRenderer != null &&
                !(_beamRenderer != null && _beamRenderer.isVisible) &&
                !(_rodRenderer != null && _rodRenderer.isVisible);
            if (bothRenderersHidden)
                return true;

            return IsLocalPlayerBeyondVisibilityDistance();
        }

        private bool IsLocalPlayerBeyondVisibilityDistance()
        {
            if (Time.unscaledTime < _nextVisibilityDistanceCheckAt)
                return _localPlayerBeyondVisibilityDistance;

            _nextVisibilityDistanceCheckAt = Time.unscaledTime + VisibilityDistanceCheckIntervalSeconds;
            PlayerControllerB player = GameNetworkManager.Instance?.localPlayerController;
            if (player == null)
            {
                _localPlayerBeyondVisibilityDistance = false;
                return false;
            }

            _localPlayerBeyondVisibilityDistance =
                (player.transform.position - transform.position).sqrMagnitude > VisibilityDistanceSqr;
            return _localPlayerBeyondVisibilityDistance;
        }

        private void CacheRendererReferences()
        {
            if (_beamRenderer == null && _beamFilter != null)
                _beamRenderer = _beamFilter.GetComponent<Renderer>();
            if (_rodRenderer == null && _rodFilter != null)
                _rodRenderer = _rodFilter.GetComponent<Renderer>();
        }

        private static Vector3[][] CacheFrameVectors(Mesh[] frames, Func<Mesh, Vector3[]> selector)
        {
            if (frames == null || frames.Length == 0)
                return Array.Empty<Vector3[]>();

            Vector3[][] cached = new Vector3[frames.Length][];
            int expectedLength = -1;
            for (int i = 0; i < frames.Length; i++)
            {
                Mesh frame = frames[i];
                if (frame == null || !frame.isReadable)
                    return Array.Empty<Vector3[]>();

                Vector3[] values = selector(frame);
                if (values == null || values.Length == 0)
                    return Array.Empty<Vector3[]>();
                if (expectedLength < 0)
                    expectedLength = values.Length;
                if (values.Length != expectedLength)
                    return Array.Empty<Vector3[]>();

                cached[i] = values;
            }

            return cached;
        }

        private static string DescribeFrameProblem(Mesh[] frames)
        {
            if (frames == null || frames.Length == 0)
                return "none";
            Mesh first = frames[0];
            if (first == null)
                return "frame0=null";
            if (!first.isReadable)
                return first.name + ":unreadable";

            int expected = first.vertexCount;
            for (int i = 1; i < frames.Length; i++)
            {
                Mesh frame = frames[i];
                if (frame == null)
                    return "frame" + i + "=null";
                if (!frame.isReadable)
                    return frame.name + ":unreadable";
                if (frame.vertexCount != expected)
                    return frame.name + ":vertices=" + frame.vertexCount + " expected=" + expected;
            }

            return "ok";
        }

        private static bool CanBlendFrames(Vector3[][] cachedFrames, int frameCount)
        {
            return cachedFrames != null && frameCount > 0 && cachedFrames.Length >= frameCount && cachedFrames[0] != null && cachedFrames[0].Length > 0;
        }

        private static bool BlendMeshFrame(
            MeshFilter filter,
            Mesh runtimeMesh,
            Mesh[] frames,
            Vector3[][] vertices,
            Vector3[][] normals,
            ref Vector3[] vertexBuffer,
            ref Vector3[] normalBuffer,
            int fromFrame,
            int toFrame,
            float blend)
        {
            if (filter == null || runtimeMesh == null || frames == null || frames.Length == 0)
                return false;
            if (!CanReadFrame(vertices, fromFrame, toFrame))
                return false;

            Vector3[] fromVertices = vertices[fromFrame];
            Vector3[] toVertices = vertices[toFrame];
            if (vertexBuffer == null || vertexBuffer.Length != fromVertices.Length)
                vertexBuffer = new Vector3[fromVertices.Length];

            for (int i = 0; i < vertexBuffer.Length; i++)
                vertexBuffer[i] = Vector3.LerpUnclamped(fromVertices[i], toVertices[i], blend);

            runtimeMesh.vertices = vertexBuffer;

            if (CanReadFrame(normals, fromFrame, toFrame))
            {
                Vector3[] fromNormals = normals[fromFrame];
                Vector3[] toNormals = normals[toFrame];
                if (normalBuffer == null || normalBuffer.Length != fromNormals.Length)
                    normalBuffer = new Vector3[fromNormals.Length];

                for (int i = 0; i < normalBuffer.Length; i++)
                    normalBuffer[i] = Vector3.LerpUnclamped(fromNormals[i], toNormals[i], blend).normalized;
                runtimeMesh.normals = normalBuffer;
            }
            else
            {
                runtimeMesh.RecalculateNormals();
            }

            runtimeMesh.RecalculateBounds();
            filter.sharedMesh = runtimeMesh;
            return true;
        }

        private static bool CanReadFrame(Vector3[][] frames, int fromFrame, int toFrame)
        {
            return frames != null
                   && fromFrame >= 0
                   && toFrame >= 0
                   && fromFrame < frames.Length
                   && toFrame < frames.Length
                   && frames[fromFrame] != null
                   && frames[toFrame] != null
                   && frames[fromFrame].Length == frames[toFrame].Length;
        }

        private void ApplyFrameDirect(int frame)
        {
            if (_beamFilter != null && _beamFrames != null && frame >= 0 && frame < _beamFrames.Length)
                _beamFilter.sharedMesh = _beamFrames[frame];
            if (_rodFilter != null && _rodFrames != null && frame >= 0 && frame < _rodFrames.Length)
                _rodFilter.sharedMesh = _rodFrames[frame];
        }

        private MeshFilter FindMeshFilter(string objectName)
        {
            Transform child = FindChild(objectName);
            return child != null ? child.GetComponent<MeshFilter>() : null;
        }

        private Transform FindChild(string objectName)
        {
            Transform[] children = GetComponentsInChildren<Transform>(true);
            for (int i = 0; i < children.Length; i++)
            {
                Transform child = children[i];
                if (child != null && string.Equals(child.name, objectName, StringComparison.OrdinalIgnoreCase))
                    return child;
            }

            return null;
        }
    }
}
