using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Reflection;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.HighDefinition;
using Y4NGZCompany.Bootstrap;

namespace Y4NGZCompany.ShipSystems.Power
{
    internal static class BatteryAssetLoader
    {
        private const string BatteryAssetRoot = "Assets/Battery";
        private const string ObjRelativePath = BatteryAssetRoot + "/source/cylinder.obj";

        private static Mesh _mesh;
        private static Material _material;
        private static Material[] _materials;
        private static readonly List<string> MaterialNames = new List<string>();
        private static bool _loadAttempted;
        private static bool _loadSucceeded;
        private static Material _fallbackMaterial;
        private static Shader _unlitShader;
        private static bool _unlitShaderResolved;
        private static Shader _unlitColorShader;
        private static bool _unlitColorShaderResolved;

        // #716: the loaded mesh/material/textures are only referenced by these
        // statics, so a scene load's UnloadUnusedAssets pass would collect them
        // and force a full reparse on the next landing. HideAndDontSave matches
        // the sibling registries (FuelPumpRegistry, SpareBatteryItemRegistry)
        // and keeps the assets alive for the process lifetime.
        private static T Persist<T>(T asset) where T : UnityEngine.Object
        {
            if (asset != null)
                asset.hideFlags = HideFlags.HideAndDontSave;
            return asset;
        }

        // Shader.Find walks the whole shader table; cache the result (including
        // a null result) instead of re-running the chain per material.
        private static Shader UnlitShader()
        {
            if (!_unlitShaderResolved)
            {
                _unlitShaderResolved = true;
                _unlitShader = Shader.Find("HDRP/Unlit") ?? Shader.Find("Unlit/Texture") ?? Shader.Find("HDRP/Lit") ?? Shader.Find("Standard");
            }

            return _unlitShader;
        }

        private static Shader UnlitColorShader()
        {
            if (!_unlitColorShaderResolved)
            {
                _unlitColorShaderResolved = true;
                _unlitColorShader = Shader.Find("HDRP/Unlit") ?? Shader.Find("Unlit/Color") ?? Shader.Find("Standard");
            }

            return _unlitColorShader;
        }

        internal static GameObject CreateBatteryVisual(Transform parent, string name, float targetMaxDimension)
        {
            EnsureLoaded();

            if (_mesh == null || _material == null)
            {
                GameObject fallback = GameObject.CreatePrimitive(PrimitiveType.Cube);
                fallback.name = name;
                fallback.transform.SetParent(parent, false);
                fallback.transform.localScale = Vector3.one * Mathf.Max(0.2f, targetMaxDimension);
                fallback.GetComponent<Renderer>().sharedMaterial = CreateFallbackMaterial();
                return fallback;
            }

            GameObject visual = new GameObject(name);
            visual.transform.SetParent(parent, false);
            MeshFilter filter = visual.AddComponent<MeshFilter>();
            MeshRenderer renderer = visual.AddComponent<MeshRenderer>();
            filter.sharedMesh = _mesh;
            renderer.sharedMaterials = _materials;
            renderer.shadowCastingMode = ShadowCastingMode.On;
            renderer.receiveShadows = true;

            NormalizeVisual(visual.transform, targetMaxDimension);
            visual.AddComponent<BatteryHeartbeat>().SetWorking(true);
            return visual;
        }

        internal static bool TryGetLocalRendererBounds(Transform root, GameObject source, out Bounds bounds)
        {
            bounds = default;
            if (root == null || source == null)
                return false;

            Renderer[] renderers = source.GetComponentsInChildren<Renderer>(true);
            bool initialized = false;
            for (int i = 0; i < renderers.Length; i++)
            {
                Renderer renderer = renderers[i];
                if (renderer == null)
                    continue;

                Bounds world = renderer.bounds;
                Vector3 min = world.min;
                Vector3 max = world.max;
                Vector3[] corners =
                {
                    new Vector3(min.x, min.y, min.z),
                    new Vector3(min.x, min.y, max.z),
                    new Vector3(min.x, max.y, min.z),
                    new Vector3(min.x, max.y, max.z),
                    new Vector3(max.x, min.y, min.z),
                    new Vector3(max.x, min.y, max.z),
                    new Vector3(max.x, max.y, min.z),
                    new Vector3(max.x, max.y, max.z),
                };

                for (int corner = 0; corner < corners.Length; corner++)
                {
                    Vector3 local = root.InverseTransformPoint(corners[corner]);
                    if (!initialized)
                    {
                        bounds = new Bounds(local, Vector3.zero);
                        initialized = true;
                    }
                    else
                    {
                        bounds.Encapsulate(local);
                    }
                }
            }

            return initialized;
        }

        private static void EnsureLoaded()
        {
            if (_loadAttempted)
            {
                // A successful load whose assets somehow went away (unloaded by
                // another mod, or a shader reload) is rebuilt once; a failed
                // load stays failed so we do not reparse the OBJ per landing.
                if (!_loadSucceeded || (_mesh != null && _material != null))
                    return;
            }

            _loadAttempted = true;
            _loadSucceeded = false;
            string root = ResolvePluginDirectory();
            try
            {
                string objPath = Path.Combine(root, ObjRelativePath.Replace('/', Path.DirectorySeparatorChar));
                if (!File.Exists(objPath))
                {
                    ShipSystemsBootstrap.Log?.LogWarning($"[LGUShipSystems.BatteryAsset] Missing OBJ at '{objPath}'. Using fallback cube.");
                    return;
                }

                _mesh = Persist(LoadObjMesh(objPath));
                _materials = MaterialNames.ConvertAll(name => Persist(CreateCylinderMaterial(root, name))).ToArray();
                _material = _materials.Length > 0 ? _materials[0] : null;
                _loadSucceeded = _mesh != null && _material != null;
                ShipSystemsBootstrap.Log?.LogInfo($"[LGUShipSystems.BatteryAsset] Loaded battery mesh (outer envelope omitted; original rods retained) vertices={(_mesh != null ? _mesh.vertexCount : 0)}.");
            }
            catch (Exception ex)
            {
                ShipSystemsBootstrap.Log?.LogWarning($"[LGUShipSystems.BatteryAsset] Failed loading battery asset: {ex.Message}");
                _mesh = null;
                _material = null;
            }
        }

        private static string ResolvePluginDirectory()
        {
            string path = Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location);
            return string.IsNullOrEmpty(path) ? Directory.GetCurrentDirectory() : path;
        }

        private static Mesh LoadObjMesh(string path)
        {
            List<Vector3> positions = new List<Vector3>();
            List<Vector2> uvs = new List<Vector2>();
            List<Vector3> normals = new List<Vector3>();
            List<Vector3> meshPositions = new List<Vector3>();
            List<Vector2> meshUvs = new List<Vector2>();
            List<Vector3> meshNormals = new List<Vector3>();
            List<int> triangles = new List<int>();
            var groups = new List<List<int>>();
            MaterialNames.Clear();
            Dictionary<string, int> vertexMap = new Dictionary<string, int>();

            string[] lines = File.ReadAllLines(path);
            for (int i = 0; i < lines.Length; i++)
            {
                string line = lines[i];
                if (string.IsNullOrWhiteSpace(line) || line[0] == '#')
                    continue;

                string[] parts = line.Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries);
                if (parts.Length == 0)
                    continue;

                switch (parts[0])
                {
                    case "v":
                        if (parts.Length >= 4)
                            positions.Add(new Vector3(Parse(parts[1]), Parse(parts[2]), Parse(parts[3])));
                        break;
                    case "vt":
                        if (parts.Length >= 3)
                            uvs.Add(new Vector2(Parse(parts[1]), Parse(parts[2])));
                        break;
                    case "vn":
                        if (parts.Length >= 4)
                            normals.Add(new Vector3(Parse(parts[1]), Parse(parts[2]), Parse(parts[3])).normalized);
                        break;
                    case "usemtl":
                        int slot = MaterialNames.IndexOf(parts[1]);
                        if (slot < 0) { MaterialNames.Add(parts[1]); groups.Add(new List<int>()); slot = groups.Count - 1; }
                        triangles = groups[slot];
                        break;
                    case "f":
                        AddFace(parts, positions, uvs, normals, meshPositions, meshUvs, meshNormals, triangles, vertexMap);
                        break;
                }
            }

            Mesh mesh = new Mesh { name = "LGUShipSystems_BatteryMesh" };
            if (meshPositions.Count > 65000)
                mesh.indexFormat = IndexFormat.UInt32;
            mesh.SetVertices(meshPositions);
            if (meshUvs.Count == meshPositions.Count)
                mesh.SetUVs(0, meshUvs);
            if (meshNormals.Count == meshPositions.Count)
                mesh.SetNormals(meshNormals);
            mesh.subMeshCount = groups.Count;
            for (int slot = 0; slot < groups.Count; slot++)
            {
                // The source's outer cylinder hides the individual rods in the game's
                // HDRP renderer despite transparent settings passing in the editor.
                // Keep the material slot/UVs stable, but omit only that envelope.
                if (MaterialNames[slot] == "lambert2SG") groups[slot].Clear();
                mesh.SetTriangles(groups[slot], slot);
            }
            mesh.RecalculateBounds();
            Vector3 center = mesh.bounds.center;
            for (int i = 0; i < meshPositions.Count; i++) meshPositions[i] -= center;
            mesh.SetVertices(meshPositions); mesh.RecalculateBounds();
            if (meshNormals.Count != meshPositions.Count)
                mesh.RecalculateNormals();
            mesh.RecalculateTangents();
            return mesh;
        }

        private static void AddFace(
            string[] parts,
            List<Vector3> positions,
            List<Vector2> uvs,
            List<Vector3> normals,
            List<Vector3> meshPositions,
            List<Vector2> meshUvs,
            List<Vector3> meshNormals,
            List<int> triangles,
            Dictionary<string, int> vertexMap)
        {
            if (parts.Length < 4)
                return;

            int[] face = new int[parts.Length - 1];
            for (int i = 1; i < parts.Length; i++)
                face[i - 1] = ResolveVertex(parts[i], positions, uvs, normals, meshPositions, meshUvs, meshNormals, vertexMap);

            for (int i = 1; i + 1 < face.Length; i++)
            {
                triangles.Add(face[0]);
                triangles.Add(face[i]);
                triangles.Add(face[i + 1]);
            }
        }

        private static int ResolveVertex(
            string token,
            List<Vector3> positions,
            List<Vector2> uvs,
            List<Vector3> normals,
            List<Vector3> meshPositions,
            List<Vector2> meshUvs,
            List<Vector3> meshNormals,
            Dictionary<string, int> vertexMap)
        {
            if (vertexMap.TryGetValue(token, out int existing))
                return existing;

            string[] indices = token.Split('/');
            int positionIndex = ResolveObjIndex(indices, 0, positions.Count);
            int uvIndex = ResolveObjIndex(indices, 1, uvs.Count);
            int normalIndex = ResolveObjIndex(indices, 2, normals.Count);

            int index = meshPositions.Count;
            meshPositions.Add(positionIndex >= 0 && positionIndex < positions.Count ? positions[positionIndex] : Vector3.zero);
            meshUvs.Add(uvIndex >= 0 && uvIndex < uvs.Count ? uvs[uvIndex] : Vector2.zero);
            meshNormals.Add(normalIndex >= 0 && normalIndex < normals.Count ? normals[normalIndex] : Vector3.up);
            vertexMap[token] = index;
            return index;
        }

        private static int ResolveObjIndex(string[] indices, int slot, int count)
        {
            if (indices == null || slot >= indices.Length || string.IsNullOrWhiteSpace(indices[slot]))
                return -1;
            if (!int.TryParse(indices[slot], NumberStyles.Integer, CultureInfo.InvariantCulture, out int value))
                return -1;
            return value < 0 ? count + value : value - 1;
        }

        private static float Parse(string value)
        {
            return float.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out float parsed)
                ? parsed
                : 0f;
        }

        private static Material CreateCylinderMaterial(string root, string group)
        {
            bool glass = group == "lambert2SG";
            bool tubes = group == "lambert8SG";
            string texture = group == "lambert5SG" ? "boczki" : group == "lambert6SG" ? "zolte"
                : group == "lambert4SG" ? "metal_jasny" : "metale_ciemne";
            var material = new Material(Shader.Find("HDRP/Lit") ?? Shader.Find("Standard"))
                { name = glass ? "ShipBattery_Glass" : tubes ? "ShipBattery_Tubes" : "ShipBattery_" + group };
            Color color = glass ? new Color(.8f,.9f,.8f,.015f) : tubes ? new Color(.43f,.65f,.24f,1f) : Color.white;
            SetColor(material, "_BaseColor", color); SetColor(material, "_Color", color);
            SetFloat(material, "_Metallic", glass || tubes ? 0f : .6f);
            SetFloat(material, "_Smoothness", glass ? .12f : tubes ? .3f : .4f);
            if (glass)
            {
                SetFloat(material, "_EnableBlendModePreserveSpecularLighting", 0f);
                SetFloat(material, "_EnableFogOnTransparent", 0f);
                // Clear outer envelope: the green belongs to the tubes behind it.
                SetFloat(material, "_SurfaceType", 1f); SetFloat(material, "_BlendMode", 0f);
                SetFloat(material, "_SrcBlend", (float)BlendMode.SrcAlpha);
                SetFloat(material, "_DstBlend", (float)BlendMode.OneMinusSrcAlpha);
                SetFloat(material, "_AlphaSrcBlend", (float)BlendMode.One);
                SetFloat(material, "_AlphaDstBlend", (float)BlendMode.OneMinusSrcAlpha);
                SetFloat(material, "_ZWrite", 0f); SetFloat(material, "_TransparentZWrite", 0f);
                SetFloat(material, "_ZTestDepthEqualForOpaque", (float)CompareFunction.LessEqual);
                material.EnableKeyword("_SURFACE_TYPE_TRANSPARENT"); material.EnableKeyword("_BLENDMODE_ALPHA");
                material.SetOverrideTag("RenderType", "Transparent"); material.renderQueue = 3000;
                material.SetShaderPassEnabled("DepthOnly", false); material.SetShaderPassEnabled("GBuffer", false);
                material.SetShaderPassEnabled("MotionVectors", false);
            }
            else if (!tubes)
            {
                string prefix = BatteryAssetRoot + "/textures/cylinder_" + texture;
                Texture2D map = LoadTexture(root, prefix + "_BaseColor.png", false);
                Texture2D mask = LoadTexture(root, prefix + "_Mask.png", true);
                Texture2D normal = LoadTexture(root, prefix + "_Normal.png", true);
                SetTexture(material, "_BaseColorMap", map); SetTexture(material, "_MainTex", map);
                SetTexture(material, "_MaskMap", mask); if (mask != null) material.EnableKeyword("_MASKMAP");
                SetTexture(material, "_NormalMap", normal); SetTexture(material, "_BumpMap", normal);
                if (normal != null) { material.EnableKeyword("_NORMALMAP"); material.EnableKeyword("_NORMALMAP_TANGENT_SPACE"); }
            }
            if (tubes)
            {
                // The original rods are the visible emitter. Keep their restrained
                // glow readable under the ship camera's exposure without boosting
                // hidden lights or washing out the metal and the open gaps.
                SetFloat(material, "_EmissiveExposureWeight", 0f);
                SetFloat(material, "_AlbedoAffectEmissive", 0f);
                SetColor(material, "_EmissiveColor", new Color(.52f, .64f, .4f));
                material.EnableKeyword("_EMISSION");
            }
            // Runtime-created HDRP materials need the same pass/keyword setup as
            // inspector-authored assets. Alpha alone leaves incompatible opaque state.
            if (material.shader.name.StartsWith("HDRP/", StringComparison.Ordinal))
                HDMaterial.ValidateMaterial(material);
            if (glass) material.SetShaderPassEnabled("ShadowCaster", false);
            return material;
        }

        internal static void SetWorking(GameObject visual, bool working)
        {
            if (visual == null) return;
            var pulse = visual.GetComponent<BatteryHeartbeat>();
            if (pulse != null) pulse.SetWorking(working);
            if (!working) foreach (var particle in visual.GetComponentsInChildren<ParticleSystem>(true))
                particle.Stop(true, ParticleSystemStopBehavior.StopEmittingAndClear);
        }

        private static Texture2D LoadTexture(string root, string relativePath, bool linear)
        {
            string path = Path.Combine(root, relativePath.Replace('/', Path.DirectorySeparatorChar));
            if (!File.Exists(path))
                return null;

            byte[] bytes = File.ReadAllBytes(path);
            Texture2D texture = new Texture2D(2, 2, TextureFormat.RGBA32, mipChain: true, linear);
            if (!texture.LoadImage(bytes, markNonReadable: true))
            {
                UnityEngine.Object.Destroy(texture);
                return null;
            }

            texture.name = Path.GetFileNameWithoutExtension(path);
            texture.wrapMode = TextureWrapMode.Repeat;
            texture.filterMode = FilterMode.Trilinear;
            return Persist(texture);
        }

        private static void NormalizeVisual(Transform visual, float targetMaxDimension)
        {
            Renderer renderer = visual != null ? visual.GetComponent<Renderer>() : null;
            if (renderer == null)
                return;

            var filter = visual.GetComponent<MeshFilter>();
            if (filter == null || filter.sharedMesh == null) return;
            Bounds bounds = filter.sharedMesh.bounds;
            float max = Mathf.Max(bounds.size.x, Mathf.Max(bounds.size.y, bounds.size.z));
            float scale = max > .001f ? targetMaxDimension / max : 1f;
            // Mesh remains shared; translate its vertices only at load time, below.
            visual.localScale = Vector3.one * scale;

        }

        private static Material CreateFallbackMaterial()
        {
            if (_fallbackMaterial != null)
                return _fallbackMaterial;

            Shader shader = UnlitColorShader();
            Material material = new Material(shader) { color = new Color(0.08f, 0.12f, 0.10f, 1f) };
            SetColor(material, "_UnlitColor", material.color);
            SetColor(material, "_BaseColor", material.color);
            _fallbackMaterial = Persist(material);
            return _fallbackMaterial;
        }

        private static void SetTexture(Material material, string property, Texture texture)
        {
            if (material != null && texture != null && material.HasProperty(property))
                material.SetTexture(property, texture);
        }

        private static void SetColor(Material material, string property, Color color)
        {
            if (material != null && material.HasProperty(property))
                material.SetColor(property, color);
        }

        private static void SetFloat(Material material, string property, float value)
        {
            if (material != null && material.HasProperty(property))
                material.SetFloat(property, value);
        }
    }
}
