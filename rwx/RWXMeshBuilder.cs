
using System.Collections.Generic;
using UnityEngine;

namespace RWXLoader
{
    public class RWXMeshBuilder
    {
        private RWXMaterialManager materialManager;

        public RWXMeshBuilder(RWXMaterialManager materialManager)
        {
            this.materialManager = materialManager;
        }

        public void CreateTriangle(RWXParseContext context, int a, int b, int c)
        {
            if (a < 0 || a >= context.vertices.Count ||
                b < 0 || b >= context.vertices.Count ||
                c < 0 || c >= context.vertices.Count)
            {
                Debug.LogWarning($"Triangle indices out of range: {a}, {b}, {c} (vertex count: {context.vertices.Count})");
                return;
            }

            // FIXED: Always set mesh material to current material state when adding geometry
            // This ensures the first triangle gets the correct material with texture
            if (context.currentTriangles.Count == 0)
            {
                // For the first triangle, always use the current material state
                context.currentMeshMaterial = context.currentMaterial?.Clone();
            }
            else
            {
                // For subsequent triangles, check if material changed
                CheckMaterialChange(context);
            }

            // FIXED: Add triangle indices in the order passed from parser
            // The parser already handles coordinate system conversion by reversing winding
            context.currentTriangles.Add(a);
            context.currentTriangles.Add(b);
            context.currentTriangles.Add(c);
        }

        public void CreateQuad(RWXParseContext context, int a, int b, int c, int d)
        {
            if (a < 0 || a >= context.vertices.Count ||
                b < 0 || b >= context.vertices.Count ||
                c < 0 || c >= context.vertices.Count ||
                d < 0 || d >= context.vertices.Count)
            {
                Debug.LogWarning($"Quad indices out of range: {a}, {b}, {c}, {d} (vertex count: {context.vertices.Count})");
                return;
            }

            // Split quad into two triangles
            CreateTriangle(context, a, b, c);
            CreateTriangle(context, a, c, d);
        }

        public void CreatePolygon(RWXParseContext context, List<int> indices)
        {
            if (indices.Count < 3) return;

            // Validate all indices
            foreach (int index in indices)
            {
                if (index < 0 || index >= context.vertices.Count)
                {
                    Debug.LogWarning($"Polygon index out of range: {index} (vertex count: {context.vertices.Count})");
                    return;
                }
            }

            // Triangulate polygon using fan triangulation
            for (int i = 1; i < indices.Count - 1; i++)
            {
                CreateTriangle(context, indices[0], indices[i], indices[i + 1]);
            }
        }

        public void CheckMaterialChange(RWXParseContext context)
        {
            // Only commit if we actually have triangles AND the material has changed
            if (context.currentTriangles.Count > 0 && context.currentMeshMaterial != null)
            {
                string currentSig = context.currentMaterial.GetMaterialSignature();
                string meshSig = context.currentMeshMaterial.GetMaterialSignature();

                if (currentSig != meshSig)
                {
                    CommitCurrentMesh(context);
                }
            }
        }

        public void CommitCurrentMesh(RWXParseContext context)
        {
            if (context.vertices.Count == 0 || context.currentTriangles.Count == 0)
            {
                // Reset material for next mesh
                context.currentMeshMaterial = context.currentMaterial?.Clone();
                return;
            }

            // FIXED: Create mesh immediately for the current clump instead of deferring
            // This ensures meshes are created as children of their respective clumps
            CreateMeshForCurrentClump(context);

            // FIXED: Don't clear vertices - they may be referenced by geometry in other clumps
            // Only clear triangles for next mesh
            context.currentTriangles.Clear();
            context.currentMeshMaterial = context.currentMaterial?.Clone();
        }

        // FIXED: Create mesh immediately for the current clump
        private void CreateMeshForCurrentClump(RWXParseContext context)
        {
            if (context.vertices.Count == 0 || context.currentTriangles.Count == 0)
            {
                return;
            }

            // Create mesh immediately for this clump
            var mesh = new Mesh();

            // Convert vertices to arrays with coordinate system conversion
            var positions = new Vector3[context.vertices.Count];
            var uvs = new Vector2[context.vertices.Count];

            for (int i = 0; i < context.vertices.Count; i++)
            {
                // Apply RWX to Unity coordinate system conversion: flip X axis
                Vector3 rwxPos = context.vertices[i].position;
                positions[i] = new Vector3(-rwxPos.x, rwxPos.y, rwxPos.z);
                uvs[i] = context.vertices[i].uv;
            }

            // Fix triangle winding order for coordinate system conversion
            var triangles = new int[context.currentTriangles.Count];
            for (int i = 0; i < context.currentTriangles.Count; i += 3)
            {
                // Reverse triangle winding order to account for flipped X coordinate
                triangles[i] = context.currentTriangles[i];
                triangles[i + 1] = context.currentTriangles[i + 2]; // Swap these two
                triangles[i + 2] = context.currentTriangles[i + 1]; // to reverse winding
            }

            bool isDoubleSided = context.currentMeshMaterial?.materialMode == MaterialMode.Double;
            var meshData = BuildMeshData(positions, uvs, triangles, isDoubleSided);
            mesh.vertices = meshData.positions;
            mesh.uv = meshData.uvs;
            mesh.triangles = meshData.triangles;
            mesh.normals = meshData.normals;
            mesh.RecalculateBounds();

            // Create mesh object as child of current clump
            string materialName = context.currentMeshMaterial?.texture ?? "Material";
            if (materialName == "default") materialName = "Default";
            
            var meshObject = new GameObject(materialName);
            meshObject.transform.SetParent(context.currentObject.transform);
            meshObject.transform.localPosition = Vector3.zero;
            meshObject.transform.localRotation = Quaternion.identity;
            meshObject.transform.localScale = Vector3.one;

            var meshFilter = meshObject.AddComponent<MeshFilter>();
            meshFilter.mesh = mesh;

            var meshRenderer = meshObject.AddComponent<MeshRenderer>();
            
            // Apply material
            if (context.currentMeshMaterial != null)
            {
                Material unityMaterial = materialManager.GetUnityMaterial(context.currentMeshMaterial);
                meshRenderer.material = unityMaterial;
            }
            else
            {
                meshRenderer.material = materialManager.GetDefaultMaterial();
            }

            // Only log mesh creation for significant meshes (body parts likely have more than 10 triangles)
            int triangleCount = context.currentTriangles.Count / 3;
            if (triangleCount > 10)
            {
                Debug.Log($"🎯 BODY PART MESH: '{materialName}' | {positions.Length} vertices, {triangleCount} triangles | Clump: '{context.currentObject.name}'");
            }
        }

        // New method to immediately create a mesh for prototype instances
        public void CommitPrototypeMesh(RWXParseContext context)
        {
            if (context.vertices.Count == 0 || context.currentTriangles.Count == 0)
            {
                return;
            }

            // Create mesh immediately for this prototype instance
            var mesh = new Mesh();

            // Convert vertices to arrays with coordinate system conversion
            var positions = new Vector3[context.vertices.Count];
            var uvs = new Vector2[context.vertices.Count];

            for (int i = 0; i < context.vertices.Count; i++)
            {
                // Apply RWX to Unity coordinate system conversion: flip X axis
                Vector3 rwxPos = context.vertices[i].position;
                positions[i] = new Vector3(-rwxPos.x, rwxPos.y, rwxPos.z);
                uvs[i] = context.vertices[i].uv;
            }

            // Fix triangle winding order for coordinate system conversion
            var triangles = new int[context.currentTriangles.Count];
            for (int i = 0; i < context.currentTriangles.Count; i += 3)
            {
                // Reverse triangle winding order to account for flipped X coordinate
                triangles[i] = context.currentTriangles[i];
                triangles[i + 1] = context.currentTriangles[i + 2]; // Swap these two
                triangles[i + 2] = context.currentTriangles[i + 1]; // to reverse winding
            }

            bool isDoubleSided = context.currentMeshMaterial?.materialMode == MaterialMode.Double;
            var meshData = BuildMeshData(positions, uvs, triangles, isDoubleSided);
            mesh.vertices = meshData.positions;
            mesh.uv = meshData.uvs;
            mesh.triangles = meshData.triangles;
            mesh.normals = meshData.normals;
            mesh.RecalculateBounds();

            // Create mesh object as child of current object (the prototype instance)
            string materialName = context.currentMeshMaterial?.texture ?? "PrototypeMesh";
            var meshObject = new GameObject(materialName);
            meshObject.transform.SetParent(context.currentObject.transform);
            // FIXED: Explicitly set local position to zero to ensure mesh appears at origin relative to positioned GameObject
            meshObject.transform.localPosition = Vector3.zero;
            meshObject.transform.localRotation = Quaternion.identity;
            meshObject.transform.localScale = Vector3.one;

            var meshFilter = meshObject.AddComponent<MeshFilter>();
            meshFilter.mesh = mesh;

            var meshRenderer = meshObject.AddComponent<MeshRenderer>();
            
            // Apply material
            if (context.currentMeshMaterial != null)
            {
                Material unityMaterial = materialManager.GetUnityMaterial(context.currentMeshMaterial);
                meshRenderer.material = unityMaterial;
            }
            else
            {
                meshRenderer.material = materialManager.GetDefaultMaterial();
            }

            Debug.Log($"Created prototype mesh '{materialName}' with {positions.Length} vertices and {context.currentTriangles.Count / 3} triangles");
            Debug.Log($"Mesh vertex positions: {string.Join(", ", positions)}");
            Debug.Log($"Mesh object localPos: {meshObject.transform.localPosition}, worldPos: {meshObject.transform.position}");

            // Clear for next mesh
            context.currentTriangles.Clear();
            context.currentMeshMaterial = context.currentMaterial?.Clone();
        }

        // FIXED: Simplified final commit - just commit any remaining geometry
        public void FinalCommit(RWXParseContext context)
        {
            // Commit any remaining geometry
            if (context.currentTriangles.Count > 0)
            {
                CommitCurrentMesh(context);
            }
        }

        private struct MeshData
        {
            public Vector3[] positions;
            public Vector2[] uvs;
            public int[] triangles;
            public Vector3[] normals;
        }

        private static MeshData BuildMeshData(Vector3[] positions, Vector2[] uvs, int[] triangles, bool isDoubleSided)
        {
            // First pass: gather face normals per vertex to detect opposing directions
            var perVertexNormals = new List<Vector3>[positions.Length];
            var faceNormals = new Vector3[triangles.Length / 3];

            for (int i = 0; i < triangles.Length; i += 3)
            {
                int i0 = triangles[i];
                int i1 = triangles[i + 1];
                int i2 = triangles[i + 2];

                if (i0 < 0 || i0 >= positions.Length ||
                    i1 < 0 || i1 >= positions.Length ||
                    i2 < 0 || i2 >= positions.Length)
                {
                    continue;
                }

                Vector3 p0 = positions[i0];
                Vector3 p1 = positions[i1];
                Vector3 p2 = positions[i2];

                Vector3 faceNormal = Vector3.Cross(p1 - p0, p2 - p0);

                // Skip degenerate triangles
                if (faceNormal.sqrMagnitude < 1e-12f)
                {
                    continue;
                }

                faceNormal.Normalize();
                faceNormals[i / 3] = faceNormal;

                AddNormal(perVertexNormals, i0, faceNormal);
                AddNormal(perVertexNormals, i1, faceNormal);
                AddNormal(perVertexNormals, i2, faceNormal);
            }

            bool hasOpposingNormals = false;
            for (int i = 0; i < perVertexNormals.Length; i++)
            {
                var list = perVertexNormals[i];
                if (list == null || list.Count < 2)
                    continue;

                for (int a = 0; a < list.Count - 1 && !hasOpposingNormals; a++)
                {
                    for (int b = a + 1; b < list.Count; b++)
                    {
                        if (Vector3.Dot(list[a], list[b]) < -0.001f)
                        {
                            hasOpposingNormals = true;
                            break;
                        }
                    }
                }
            }

            // If opposing normals share vertices (double-sided plane), duplicate vertices per face
            if (isDoubleSided && hasOpposingNormals)
            {
                var newPositions = new List<Vector3>(triangles.Length);
                var newUvs = new List<Vector2>(triangles.Length);
                var newNormals = new List<Vector3>(triangles.Length);
                var newTriangles = new int[triangles.Length];

                for (int i = 0; i < triangles.Length; i += 3)
                {
                    int i0 = triangles[i];
                    int i1 = triangles[i + 1];
                    int i2 = triangles[i + 2];

                    if (i0 < 0 || i0 >= positions.Length ||
                        i1 < 0 || i1 >= positions.Length ||
                        i2 < 0 || i2 >= positions.Length)
                    {
                        continue;
                    }

                    Vector3 faceNormal = faceNormals[i / 3];
                    if (faceNormal.sqrMagnitude < 1e-12f)
                    {
                        faceNormal = Vector3.up;
                    }

                    int newBase = newPositions.Count;
                    newPositions.Add(positions[i0]);
                    newPositions.Add(positions[i1]);
                    newPositions.Add(positions[i2]);

                    newUvs.Add(uvs[i0]);
                    newUvs.Add(uvs[i1]);
                    newUvs.Add(uvs[i2]);

                    newNormals.Add(faceNormal);
                    newNormals.Add(faceNormal);
                    newNormals.Add(faceNormal);

                    newTriangles[i] = newBase;
                    newTriangles[i + 1] = newBase + 1;
                    newTriangles[i + 2] = newBase + 2;
                }

                return new MeshData
                {
                    positions = newPositions.ToArray(),
                    uvs = newUvs.ToArray(),
                    triangles = newTriangles,
                    normals = newNormals.ToArray()
                };
            }

            // Otherwise keep smooth shading with hemisphere-aligned accumulation
            var normals = new Vector3[positions.Length];

            for (int i = 0; i < triangles.Length; i += 3)
            {
                int i0 = triangles[i];
                int i1 = triangles[i + 1];
                int i2 = triangles[i + 2];

                if (i0 < 0 || i0 >= positions.Length ||
                    i1 < 0 || i1 >= positions.Length ||
                    i2 < 0 || i2 >= positions.Length)
                {
                    continue;
                }

                Vector3 faceNormal = faceNormals[i / 3];

                if (faceNormal.sqrMagnitude < 1e-12f)
                {
                    continue;
                }

                normals[i0] = AccumulateNormal(normals[i0], faceNormal);
                normals[i1] = AccumulateNormal(normals[i1], faceNormal);
                normals[i2] = AccumulateNormal(normals[i2], faceNormal);
            }

            for (int i = 0; i < normals.Length; i++)
            {
                if (normals[i].sqrMagnitude > 1e-12f)
                {
                    normals[i].Normalize();
                }
                else
                {
                    normals[i] = Vector3.up; // sensible fallback
                }
            }

            return new MeshData
            {
                positions = positions,
                uvs = uvs,
                triangles = triangles,
                normals = normals
            };
        }

        private static Vector3 AccumulateNormal(Vector3 accumulator, Vector3 faceNormal)
        {
            if (accumulator.sqrMagnitude > 1e-12f)
            {
                // If the face normal points into the opposite hemisphere, flip it so vertex
                // normals don't cancel out when double-sided geometry reuses the same vertices.
                if (Vector3.Dot(accumulator, faceNormal) < 0f)
                {
                    faceNormal = -faceNormal;
                }
            }

            return accumulator + faceNormal;
        }

        private static void AddNormal(List<Vector3>[] buckets, int index, Vector3 normal)
        {
            var list = buckets[index];
            if (list == null)
            {
                list = new List<Vector3>(2);
                buckets[index] = list;
            }

            list.Add(normal);
        }

    }
}
