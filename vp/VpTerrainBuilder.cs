using System;
using System.Collections;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Networking;
using VpNet;

public class VpTerrainBuilder
{
    public struct TerrainCellCacheEntry
    {
        public bool hasData;
        public bool isHole;
        public float height;
    }

    private struct TerrainCellData
    {
        public bool hasData;
        public bool isHole;
        public float height;
        public ushort texture;
        public byte rotation;
    }

    private readonly Dictionary<(int cx, int cz), TerrainCellCacheEntry> terrainCellCache = new();
    private readonly Dictionary<ushort, Material> terrainMaterialCache = new();
    private readonly HashSet<ushort> terrainDownloadsInFlight = new();

    private readonly Func<float> getUnityUnitsPerVpCell;
    private readonly Func<float> getUnityUnitsPerVpUnit;
    private readonly Func<IEnumerator, Coroutine> coroutineStarter;
    private readonly Action<string> logWarning;
    private readonly Func<string, Shader> shaderLookup;

    public int TerrainTileCellSpan { get; set; } = 32;
    public int TerrainNodeCellSpan { get; set; } = 8;
    public float TerrainHeightOffset { get; set; } = -0.01f;
    public Material TerrainMaterialTemplate { get; set; }
        = new Material(Shader.Find("Standard")) { name = "VP Terrain Material" };
    public string ObjectPath { get; set; } = string.Empty;
    public string ObjectPathPassword { get; set; } = string.Empty;

    public IReadOnlyDictionary<(int cx, int cz), TerrainCellCacheEntry> TerrainCellCache => terrainCellCache;
    public bool HasActiveDownloads => terrainDownloadsInFlight.Count > 0;

    public VpTerrainBuilder(
        Func<float> unityUnitsPerVpCell,
        Func<float> unityUnitsPerVpUnit,
        Func<IEnumerator, Coroutine> coroutineStarter,
        Func<string, Shader> shaderLookup = null,
        Action<string> logWarning = null)
    {
        getUnityUnitsPerVpCell = unityUnitsPerVpCell ?? throw new ArgumentNullException(nameof(unityUnitsPerVpCell));
        getUnityUnitsPerVpUnit = unityUnitsPerVpUnit ?? throw new ArgumentNullException(nameof(unityUnitsPerVpUnit));
        this.coroutineStarter = coroutineStarter ?? throw new ArgumentNullException(nameof(coroutineStarter));
        this.shaderLookup = shaderLookup ?? Shader.Find;
        this.logWarning = logWarning ?? Debug.LogWarning;
    }

    public Mesh BuildTerrainMesh(int tileX, int tileZ, TerrainNode[] nodes, out List<Material> materials)
    {
        materials = new List<Material>();

        if (nodes == null || nodes.Length == 0 || TerrainTileCellSpan <= 0 || TerrainNodeCellSpan <= 0)
            return null;

        int tileSpan = TerrainTileCellSpan;
        int nodeSpan = TerrainNodeCellSpan;

        var cellData = new TerrainCellData[tileSpan, tileSpan];

        foreach (var node in nodes)
        {
            for (int cz = 0; cz < nodeSpan; cz++)
            {
                for (int cx = 0; cx < nodeSpan; cx++)
                {
                    int idx = cz * nodeSpan + cx;
                    if (node.Cells == null || idx >= node.Cells.Length)
                        continue;

                    int cellX = node.X * nodeSpan + cx;
                    int cellZ = node.Z * nodeSpan + cz;

                    if (cellX < 0 || cellX >= tileSpan || cellZ < 0 || cellZ >= tileSpan)
                        continue;

                    var cell = new TerrainCellData
                    {
                        hasData = true,
                        height = node.Cells[idx].Height,
                        texture = node.Cells[idx].Texture,
                        isHole = node.Cells[idx].IsHole,
                        rotation = ExtractTerrainRotation(node.Cells[idx])
                    };

                    cellData[cellX, cellZ] = cell;

                    int worldCX = tileX * tileSpan + cellX;
                    int worldCZ = tileZ * tileSpan + cellZ;
                    terrainCellCache[(worldCX, worldCZ)] = new TerrainCellCacheEntry
                    {
                        hasData = cell.hasData,
                        isHole = cell.isHole,
                        height = cell.height
                    };
                }
            }
        }

        float cellSizeUnity = getUnityUnitsPerVpCell();
        if (cellSizeUnity <= 0f)
            cellSizeUnity = 1f;

        bool TryGetCellHeight(int worldCX, int worldCZ, out float h)
        {
            if (terrainCellCache.TryGetValue((worldCX, worldCZ), out var cachedCell) && cachedCell.hasData && !cachedCell.isHole)
            {
                h = cachedCell.height;
                return true;
            }

            int localCX = worldCX - tileX * tileSpan;
            int localCZ = worldCZ - tileZ * tileSpan;
            if (localCX >= 0 && localCX < tileSpan && localCZ >= 0 && localCZ < tileSpan)
            {
                var c = cellData[localCX, localCZ];
                if (c.hasData && !c.isHole)
                {
                    h = c.height;
                    return true;
                }
            }

            h = 0f;
            return false;
        }

        float[,] heightGrid = new float[tileSpan + 1, tileSpan + 1];
        for (int vx = 0; vx <= tileSpan; vx++)
        {
            for (int vz = 0; vz <= tileSpan; vz++)
            {
                int worldCX = tileX * tileSpan + vx;
                int worldCZ = tileZ * tileSpan + vz;
                int ownerCX = worldCX;
                int ownerCZ = worldCZ;

                if (TryGetCellHeight(ownerCX, ownerCZ, out float hExact) ||
                    TryGetCellHeight(ownerCX - 1, ownerCZ, out hExact) ||
                    TryGetCellHeight(ownerCX, ownerCZ - 1, out hExact) ||
                    TryGetCellHeight(ownerCX - 1, ownerCZ - 1, out hExact))
                {
                    heightGrid[vx, vz] = hExact;
                    continue;
                }

                float foundH = 0f;
                bool found = false;
                for (int radius = 1; radius <= 2 && !found; radius++)
                {
                    for (int dz = -radius; dz <= radius && !found; dz++)
                    {
                        for (int dx = -radius; dx <= radius && !found; dx++)
                        {
                            int wx = ownerCX + dx;
                            int wz = ownerCZ + dz;
                            if (TryGetCellHeight(wx, wz, out float hh))
                            {
                                foundH = hh;
                                found = true;
                            }
                        }
                    }
                }

                heightGrid[vx, vz] = found ? foundH : 0f;
            }
        }

        float unityUnitsPerVpUnit = getUnityUnitsPerVpUnit();

        var normalsGrid = new UnityEngine.Vector3[tileSpan + 1, tileSpan + 1];
        for (int vx = 0; vx <= tileSpan; vx++)
        {
            for (int vz = 0; vz <= tileSpan; vz++)
            {
                float hC = heightGrid[vx, vz] * unityUnitsPerVpUnit;
                float hL = heightGrid[Mathf.Max(0, vx - 1), vz] * unityUnitsPerVpUnit;
                float hR = heightGrid[Mathf.Min(tileSpan, vx + 1), vz] * unityUnitsPerVpUnit;
                float hD = heightGrid[vx, Mathf.Max(0, vz - 1)] * unityUnitsPerVpUnit;
                float hU = heightGrid[vx, Mathf.Min(tileSpan, vz + 1)] * unityUnitsPerVpUnit;

                float dx = (hR - hL) * 0.5f / cellSizeUnity;
                float dz = (hU - hD) * 0.5f / cellSizeUnity;

                normalsGrid[vx, vz] = new UnityEngine.Vector3(-dx, 1f, dz).normalized;
            }
        }

        var vertices = new List<UnityEngine.Vector3>(tileSpan * tileSpan * 4);
        var normals = new List<UnityEngine.Vector3>(tileSpan * tileSpan * 4);
        var uvs = new List<UnityEngine.Vector2>(tileSpan * tileSpan * 4);
        var trianglesByTex = new Dictionary<ushort, List<int>>();

        for (int z = 0; z < tileSpan; z++)
        {
            for (int x = 0; x < tileSpan; x++)
            {
                var cell = cellData[x, z];
                if (!cell.hasData || cell.isHole)
                    continue;

                float unityX = (tileX * tileSpan + x) * cellSizeUnity;
                float unityZ = (tileZ * tileSpan + z) * cellSizeUnity;

                float h00 = heightGrid[x, z] * unityUnitsPerVpUnit + TerrainHeightOffset;
                float h10 = heightGrid[x + 1, z] * unityUnitsPerVpUnit + TerrainHeightOffset;
                float h01 = heightGrid[x, z + 1] * unityUnitsPerVpUnit + TerrainHeightOffset;
                float h11 = heightGrid[x + 1, z + 1] * unityUnitsPerVpUnit + TerrainHeightOffset;

                int vStart = vertices.Count;
                vertices.Add(new UnityEngine.Vector3(-unityX, h00, unityZ));
                vertices.Add(new UnityEngine.Vector3(-(unityX + cellSizeUnity), h10, unityZ));
                vertices.Add(new UnityEngine.Vector3(-unityX, h01, unityZ + cellSizeUnity));
                vertices.Add(new UnityEngine.Vector3(-(unityX + cellSizeUnity), h11, unityZ + cellSizeUnity));

                normals.Add(normalsGrid[x, z]);
                normals.Add(normalsGrid[x + 1, z]);
                normals.Add(normalsGrid[x, z + 1]);
                normals.Add(normalsGrid[x + 1, z + 1]);

                UnityEngine.Vector2 uv0 = new UnityEngine.Vector2(0f, 1f);
                UnityEngine.Vector2 uv1 = new UnityEngine.Vector2(1f, 1f);
                UnityEngine.Vector2 uv2 = new UnityEngine.Vector2(0f, 0f);
                UnityEngine.Vector2 uv3 = new UnityEngine.Vector2(1f, 0f);

                RotateUvQuarter(ref uv0, ref uv1, ref uv2, ref uv3, cell.rotation);

                uvs.Add(uv0);
                uvs.Add(uv1);
                uvs.Add(uv2);
                uvs.Add(uv3);

                if (!trianglesByTex.TryGetValue(cell.texture, out var tris))
                {
                    tris = new List<int>();
                    trianglesByTex[cell.texture] = tris;
                }

                tris.AddRange(new[]
                {
                    vStart, vStart + 1, vStart + 2,
                    vStart + 1, vStart + 3, vStart + 2
                });
            }
        }

        if (vertices.Count == 0 || trianglesByTex.Count == 0)
            return null;

        var mesh = new Mesh
        {
            indexFormat = vertices.Count > 65000 ? UnityEngine.Rendering.IndexFormat.UInt32 : UnityEngine.Rendering.IndexFormat.UInt16,
            name = $"VP_Terrain_{tileX}_{tileZ}"
        };

        mesh.SetVertices(vertices);
        mesh.SetNormals(normals);
        mesh.SetUVs(0, uvs);
        mesh.subMeshCount = trianglesByTex.Count;

        int subMesh = 0;
        foreach (var kvp in trianglesByTex)
        {
            mesh.SetTriangles(kvp.Value, subMesh++);
            materials.Add(GetTerrainMaterial(kvp.Key));
        }

        mesh.RecalculateBounds();
        return mesh;
    }

    public void ClearTileCache(int tileX, int tileZ)
    {
        int tileSpan = Mathf.Max(1, TerrainTileCellSpan);
        for (int cz = 0; cz < tileSpan; cz++)
        {
            for (int cx = 0; cx < tileSpan; cx++)
            {
                terrainCellCache.Remove((tileX * tileSpan + cx, tileZ * tileSpan + cz));
            }
        }
    }

    private Material GetTerrainMaterial(ushort textureId)
    {
        if (terrainMaterialCache.TryGetValue(textureId, out var cached) && cached != null)
            return cached;

        var template = TerrainMaterialTemplate != null
            ? new Material(TerrainMaterialTemplate)
            : new Material(shaderLookup("Standard"));

        template.name = $"Terrain_{textureId}";
        terrainMaterialCache[textureId] = template;

        if (template.HasProperty("_Glossiness"))
            template.SetFloat("_Glossiness", 0.0f);
        if (template.HasProperty("_Smoothness"))
            template.SetFloat("_Smoothness", 0.0f);

        if (!terrainDownloadsInFlight.Contains(textureId) && !string.IsNullOrWhiteSpace(ObjectPath))
            coroutineStarter(DownloadTerrainTexture(textureId, template));

        return template;
    }

    private IEnumerator DownloadTerrainTexture(ushort textureId, Material target)
    {
        if (target == null)
            yield break;

        string basePath = ObjectPath.TrimEnd('/') + "/";

        terrainDownloadsInFlight.Add(textureId);

        string[] exts = { "jpg", "png" };
        Texture2D texFound = null;
        foreach (var ext in exts)
        {
            string url = $"{basePath}textures/terrain{textureId}.{ext}";

            if (!string.IsNullOrWhiteSpace(ObjectPathPassword))
            {
                string separator = url.Contains("?") ? "&" : "?";
                url = $"{url}{separator}password={UnityWebRequest.EscapeURL(ObjectPathPassword)}";
            }
            using (var req = UnityWebRequestTexture.GetTexture(url))
            {
                yield return req.SendWebRequest();

#if UNITY_2020_1_OR_NEWER
                bool hasError = req.result != UnityWebRequest.Result.Success;
#else
                bool hasError = req.isNetworkError || req.isHttpError;
#endif

                if (!hasError)
                {
                    texFound = DownloadHandlerTexture.GetContent(req);
                    break;
                }
            }
        }

        if (texFound != null)
        {
            texFound.wrapMode = TextureWrapMode.Repeat;
            target.mainTexture = texFound;
            if (target.HasProperty("_BaseMap"))
                target.SetTexture("_BaseMap", texFound);
        }
        else
        {
            logWarning?.Invoke($"[VP] Failed to download terrain texture {textureId} (.jpg/.png)");
        }

        terrainDownloadsInFlight.Remove(textureId);
    }

    private static byte ExtractTerrainRotation(object cell)
    {
        if (cell == null) return 0;

        var type = cell.GetType();
        var rotProp = type.GetProperty("Rotation") ?? type.GetProperty("TextureRotation");
        if (rotProp != null)
        {
            try
            {
                var val = rotProp.GetValue(cell);
                if (val is byte b) return b;
                if (val is int i) return (byte)i;
                if (val is short s) return (byte)s;
                if (val is sbyte sb) return (byte)sb;
                if (val is IConvertible conv)
                    return (byte)conv.ToInt32(System.Globalization.CultureInfo.InvariantCulture);
            }
            catch { }
        }

        return 0;
    }

    private static void RotateUvQuarter(ref UnityEngine.Vector2 uv0, ref UnityEngine.Vector2 uv1, ref UnityEngine.Vector2 uv2, ref UnityEngine.Vector2 uv3, byte rotation)
    {
        int r = ((-rotation) % 4 + 4) % 4;
        if (r == 0) return;

        for (int i = 0; i < r; i++)
        {
            uv0 = new UnityEngine.Vector2(uv0.y, 1f - uv0.x);
            uv1 = new UnityEngine.Vector2(uv1.y, 1f - uv1.x);
            uv2 = new UnityEngine.Vector2(uv2.y, 1f - uv2.x);
            uv3 = new UnityEngine.Vector2(uv3.y, 1f - uv3.x);
        }
    }
}
