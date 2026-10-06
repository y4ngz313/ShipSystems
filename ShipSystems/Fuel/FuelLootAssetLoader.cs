using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using UnityEngine;
using Y4NGZCompany.Bootstrap;

namespace Y4NGZCompany.ShipSystems.Fuel
{
    /// <summary>Two offline-authored meshes, loaded once from this package's own payload.</summary>
    internal static class FuelLootAssetLoader
    {
        private sealed class Asset { internal Mesh Mesh; internal Material Material; }
        private static readonly Dictionary<string, Asset> Cache = new Dictionary<string, Asset>();

        internal static MeshRenderer TryCreate(Transform parent, string size)
        {
            if (size != "Small" && size != "Medium") return null;
            if (!Cache.TryGetValue(size, out Asset asset))
            {
                asset = Load(size);
                Cache[size] = asset;
            }
            if (asset == null) return null;
            var visual = new GameObject(size + "Fuel_AuthoredVisual");
            visual.transform.SetParent(parent, false);
            visual.layer = FuelRuntimeUtility.PropsLayer;
            visual.AddComponent<MeshFilter>().sharedMesh = asset.Mesh;
            MeshRenderer renderer = visual.AddComponent<MeshRenderer>();
            renderer.sharedMaterial = asset.Material;
            return renderer;
        }

        private static Asset Load(string size)
        {
            Mesh mesh = null; Texture2D texture = null; Material material = null;
            try
            {
                string root = Path.Combine(Path.GetDirectoryName(typeof(ShipSystemsBootstrap).Assembly.Location), "Assets", "Fuel");
                string stem = Path.Combine(root, size.ToLowerInvariant());
                var positions = new List<Vector3>(); var normals = new List<Vector3>(); var uvs = new List<Vector2>(); var triangles = new List<int>();
                // The build script exports a triangulated, de-indexed OBJ: position,
                // normal and UV share an index, keeping seams and hard edges intact.
                foreach (string line in File.ReadLines(stem + ".obj"))
                {
                    string[] p = line.Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries);
                    if (p.Length == 0) continue;
                    switch (p[0])
                    {
                        case "v": positions.Add(new Vector3(Number(p[1]), Number(p[2]), Number(p[3]))); break;
                        case "vn": normals.Add(new Vector3(Number(p[1]), Number(p[2]), Number(p[3]))); break;
                        case "vt": uvs.Add(new Vector2(Number(p[1]), Number(p[2]))); break;
                        case "f":
                            if (p.Length != 4) throw new InvalidDataException("Expected triangulated fuel mesh");
                            for (int i = 1; i < 4; i++)
                            {
                                string[] indices = p[i].Split('/');
                                if (indices.Length != 3 || indices[0] != indices[1] || indices[0] != indices[2])
                                    throw new InvalidDataException("Fuel mesh corner indices must match");
                                int index = int.Parse(indices[0], CultureInfo.InvariantCulture) - 1;
                                if (index < 0 || index >= positions.Count) throw new InvalidDataException("Fuel mesh index out of range");
                                triangles.Add(index);
                            }
                            break;
                    }
                }
                if (positions.Count == 0 || positions.Count > 65000 || normals.Count != positions.Count || uvs.Count != positions.Count || triangles.Count == 0)
                    throw new InvalidDataException("Invalid fuel mesh attributes");
                mesh = new Mesh { name = size + "FuelMesh", hideFlags = HideFlags.HideAndDontSave };
                mesh.SetVertices(positions); mesh.SetNormals(normals); mesh.SetUVs(0, uvs); mesh.SetTriangles(triangles, 0);
                mesh.RecalculateBounds(); mesh.RecalculateTangents(); mesh.UploadMeshData(true);
                texture = new Texture2D(2, 2, TextureFormat.RGBA32, true, false)
                    { name = size + "FuelAlbedo", hideFlags = HideFlags.HideAndDontSave, filterMode = FilterMode.Bilinear, wrapMode = TextureWrapMode.Clamp };
                if (!texture.LoadImage(File.ReadAllBytes(stem + "-albedo.png"), true)) throw new InvalidDataException("Invalid fuel albedo");
                Shader shader = Shader.Find("HDRP/Lit") ?? Shader.Find("Standard");
                if (shader == null) throw new InvalidOperationException("Fuel material shader unavailable");
                material = new Material(shader) { name = size + "FuelMaterial", hideFlags = HideFlags.HideAndDontSave };
                if (material.HasProperty("_BaseColorMap")) material.SetTexture("_BaseColorMap", texture);
                if (material.HasProperty("_MainTex")) material.SetTexture("_MainTex", texture);
                if (material.HasProperty("_BaseColor")) material.SetColor("_BaseColor", Color.white);
                if (material.HasProperty("_Color")) material.SetColor("_Color", Color.white);
                if (material.HasProperty("_Metallic")) material.SetFloat("_Metallic", .35f);
                if (material.HasProperty("_Smoothness")) material.SetFloat("_Smoothness", .22f);
                if (material.HasProperty("_Glossiness")) material.SetFloat("_Glossiness", .22f);
                ShipSystemsBootstrap.Log?.LogInfo($"[LGUShipSystems.FuelItems] Loaded authored {size.ToLowerInvariant()} fuel visual ({triangles.Count / 3} triangles).");
                return new Asset { Mesh = mesh, Material = material };
            }
            catch (Exception ex)
            {
                if (mesh != null) UnityEngine.Object.Destroy(mesh);
                if (texture != null) UnityEngine.Object.Destroy(texture);
                if (material != null) UnityEngine.Object.Destroy(material);
                ShipSystemsBootstrap.Log?.LogWarning($"[LGUShipSystems.FuelItems] {size} authored visual unavailable: {ex.Message}; using fallback.");
                return null;
            }
        }
        private static float Number(string value) => float.Parse(value, CultureInfo.InvariantCulture);
    }
}
