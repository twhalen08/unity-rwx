using System;
using System.Collections.Generic;
using System.Text.RegularExpressions;
using UnityEngine;

namespace RWXLoader
{
    /// <summary>
    /// Handles RWX prototype commands: ProtoBegin, ProtoEnd, ProtoInstance
    /// </summary>
    public class RWXPrototypeParser
    {
        private readonly Regex protoBeginRegex = new Regex(@"^\s*protobegin\s+([A-Za-z0-9_\-]+).*$", RegexOptions.IgnoreCase);
        private readonly Regex protoEndRegex = new Regex(@"^\s*protoend.*$", RegexOptions.IgnoreCase);
        private readonly Regex protoInstanceRegex = new Regex(@"^\s*protoinstance\s+([A-Za-z0-9_\-]+).*$", RegexOptions.IgnoreCase);
        
        private Dictionary<string, List<string>> prototypes = new Dictionary<string, List<string>>();
        private Dictionary<string, Matrix4x4> prototypeTransforms = new Dictionary<string, Matrix4x4>();
        private List<string> currentPrototypeLines = null;
        private string currentPrototypeName = null;
        private bool isInPrototype = false;
        
        private RWXParser mainParser;
        private RWXMeshBuilder meshBuilder;
        
        public RWXPrototypeParser(RWXParser mainParser, RWXMeshBuilder meshBuilder)
        {
            this.mainParser = mainParser;
            this.meshBuilder = meshBuilder;
        }
        
        /// <summary>
        /// Process a line that might be a prototype command
        /// </summary>
        /// <param name="line">The line to process</param>
        /// <param name="context">Current parse context</param>
        /// <returns>True if the line was handled as a prototype command</returns>
        public bool ProcessLine(string line, RWXParseContext context)
        {
            if (ProcessProtoBegin(line, context)) return true;
            if (ProcessProtoEnd(line, context)) return true;
            if (ProcessProtoInstance(line, context)) return true;
            
            // If we're inside a prototype definition, collect the line
            if (isInPrototype && currentPrototypeLines != null)
            {
                currentPrototypeLines.Add(line);
                return true;
            }
            
            return false;
        }
        
        private bool ProcessProtoBegin(string line, RWXParseContext context)
        {
            var match = protoBeginRegex.Match(line);
            if (!match.Success) return false;
            
            currentPrototypeName = match.Groups[1].Value.ToLower();
            currentPrototypeLines = new List<string>();
            isInPrototype = true;
            
            Debug.Log($"Starting prototype definition: {currentPrototypeName}");
            return true;
        }
        
        private bool ProcessProtoEnd(string line, RWXParseContext context)
        {
            if (!protoEndRegex.IsMatch(line)) return false;
            
            if (isInPrototype && currentPrototypeName != null && currentPrototypeLines != null)
            {
                // Store the prototype
                prototypes[currentPrototypeName] = new List<string>(currentPrototypeLines);
                Debug.Log($"Stored prototype '{currentPrototypeName}' with {currentPrototypeLines.Count} lines");
                
                // Reset state
                currentPrototypeName = null;
                currentPrototypeLines = null;
                isInPrototype = false;
            }
            
            return true;
        }
        
        private bool ProcessProtoInstance(string line, RWXParseContext context)
        {
            var match = protoInstanceRegex.Match(line);
            if (!match.Success) return false;
            
            string prototypeName = match.Groups[1].Value.ToLower();
            
            if (!prototypes.ContainsKey(prototypeName))
            {
                Debug.LogWarning($"Prototype '{prototypeName}' not found!");
                return true;
            }
            
            Debug.Log($"🌲 PROTOTYPE INSTANCE: {prototypeName}");
            Debug.Log($"🌲 Current context transform: {context.currentTransform}");
            
            // Commit current mesh before creating instance
            meshBuilder.CommitCurrentMesh(context);
            
            // Create a new GameObject for this prototype instance
            var instanceObject = new GameObject($"Proto_{prototypeName}");
            instanceObject.transform.SetParent(context.currentObject.transform);

            // Apply the full accumulated transform (including rotation/scale) so leaves align with trunks, etc.
            mainParser.ApplyTransformToObject(context.currentTransform, instanceObject, context);

            // Save current state
            var savedObject = context.currentObject;
            var savedMaterial = context.currentMaterial.Clone();
            var savedTransform = context.currentTransform;
            var savedVertices = new List<RWXVertex>(context.vertices);
            var savedTriangles = new List<int>(context.currentTriangles);
            var savedMeshMaterial = context.currentMeshMaterial?.Clone();
            
            // Set up context for prototype instance
            context.currentObject = instanceObject;
            context.currentTransform = Matrix4x4.identity; // Reset for prototype processing
            context.vertices.Clear(); // Start with clean vertex list for this prototype
            context.currentTriangles.Clear();
            context.currentMeshMaterial = null;
            
            // Track the transform that should be applied to this instance
            Matrix4x4 instanceTransform = Matrix4x4.identity;
            
            // Process all lines from the prototype
            var prototypeLines = prototypes[prototypeName];
            foreach (string prototypeLine in prototypeLines)
            {
                if (!string.IsNullOrWhiteSpace(prototypeLine))
                {
            // Check if this is a Transform command - if so, handle it specially
            if (prototypeLine.Trim().ToLower().StartsWith("transform"))
            {
                instanceTransform = ExtractTransformFromLine(prototypeLine);
                
                // For bed-style prototypes with Transform matrices, apply the transform directly to vertices
                // This preserves the precise orientations defined in the prototype
                if (IsBedStylePrototype(prototypeName, instanceTransform))
                {
                    context.currentTransform = instanceTransform;
                    Debug.Log($"🛏️ BED PROTOTYPE: {prototypeName} - Applied Transform matrix directly to context for vertex processing");
                    Debug.Log($"🛏️ Transform: Translation=({instanceTransform.m03:F6}, {instanceTransform.m13:F6}, {instanceTransform.m23:F6})");
                    
                    // Add detailed matrix logging for bed prototypes
                    if (IsBedHeadFootboardPrototype(prototypeName, instanceTransform))
                    {
                        Debug.Log($"🛏️ BED MATRIX | {prototypeName}");
                        float det = instanceTransform.m00 * (instanceTransform.m11 * instanceTransform.m22 - instanceTransform.m12 * instanceTransform.m21) -
                                   instanceTransform.m01 * (instanceTransform.m10 * instanceTransform.m22 - instanceTransform.m12 * instanceTransform.m20) +
                                   instanceTransform.m02 * (instanceTransform.m10 * instanceTransform.m21 - instanceTransform.m11 * instanceTransform.m20);
                        Debug.Log($"   Unity: Det={det:F3}, Trans=({instanceTransform.m03:F3}, {instanceTransform.m13:F3}, {instanceTransform.m23:F3})");
                    }
                }
                else
                {
                    // For tree-style prototypes, capture for instance positioning
                    Debug.Log($"🌲 TREE PROTOTYPE: {prototypeName} - Captured instance transform: Translation=({instanceTransform.m03:F6}, {instanceTransform.m13:F6}, {instanceTransform.m23:F6})");
                }
            }
            else
            {
                // Process other commands normally (geometry, materials, etc.)
                mainParser.ProcessLine(prototypeLine, context);
            }
                }
            }
            
            Debug.Log($"🌲 Prototype {prototypeName} created {context.vertices.Count} vertices and {context.currentTriangles.Count} triangles");
            
            // Apply the captured transform to the instance object
            if (instanceTransform != Matrix4x4.identity)
            {
                ApplyTransformToInstance(instanceObject, instanceTransform);
            }
            
            // Commit the prototype instance mesh immediately
            meshBuilder.CommitPrototypeMesh(context);
            
            // Restore context
            context.currentObject = savedObject;
            context.currentMaterial = savedMaterial;
            context.currentTransform = savedTransform;
            context.vertices = savedVertices;
            context.currentTriangles = savedTriangles;
            context.currentMeshMaterial = savedMeshMaterial;
            
            return true;
        }
        
        /// <summary>
        /// Check if we're currently inside a prototype definition
        /// </summary>
        public bool IsInPrototype => isInPrototype;
        
        /// <summary>
        /// Get the names of all defined prototypes
        /// </summary>
        public string[] GetPrototypeNames()
        {
            var names = new string[prototypes.Count];
            prototypes.Keys.CopyTo(names, 0);
            return names;
        }
        
        /// <summary>
        /// Clear all prototype definitions
        /// </summary>
        public void ClearPrototypes()
        {
            prototypes.Clear();
            prototypeTransforms.Clear();
            currentPrototypeName = null;
            currentPrototypeLines = null;
            isInPrototype = false;
        }
        
        /// <summary>
        /// Reset the prototype parser state for a new model
        /// </summary>
        public void Reset()
        {
            ClearPrototypes();
            Debug.Log("🔄 Prototype parser reset for new model");
        }
        
        /// <summary>
        /// Check if any defined prototypes contain Transform commands
        /// </summary>
        public bool HasPrototypesWithTransforms()
        {
            foreach (var prototype in prototypes.Values)
            {
                foreach (string line in prototype)
                {
                    if (line.Trim().ToLower().StartsWith("transform"))
                    {
                        return true;
                    }
                }
            }
            return false;
        }
        
        /// <summary>
        /// Check if a specific prototype contains Transform commands
        /// </summary>
        private bool PrototypeHasTransform(string prototypeName)
        {
            if (!prototypes.ContainsKey(prototypeName))
                return false;
                
            var prototypeLines = prototypes[prototypeName];
            foreach (string line in prototypeLines)
            {
                if (line.Trim().ToLower().StartsWith("transform"))
                {
                    return true;
                }
            }
            return false;
        }
        
        /// <summary>
        /// Determine if a prototype needs positioning correction
        /// </summary>
        private bool ShouldApplyPrototypeFix(string prototypeName, RWXParseContext context)
        {
            // Check if the current transform is at origin (indicating canceling translates)
            Vector3 currentPos = new Vector3(context.currentTransform.m03, context.currentTransform.m13, context.currentTransform.m23);
            bool isAtOrigin = currentPos.magnitude < 0.001f;
            
            // Known problematic patterns
            bool isTreeCone = prototypeName.Contains("cone");
            bool isNumberedPrototype = System.Text.RegularExpressions.Regex.IsMatch(prototypeName, @".*_\d+$");
            
            // Apply fix if we're at origin and have a pattern that suggests layered/positioned prototypes
            return isAtOrigin && (isTreeCone || isNumberedPrototype);
        }
        
        /// <summary>
        /// Get corrected transform matrix for problematic prototypes
        /// </summary>
        private Matrix4x4 GetCorrectedPrototypeTransform(string prototypeName, RWXParseContext context)
        {
            // Tree cone fix (specific known case)
            if (prototypeName.Contains("cone"))
            {
                return GetTreeConeTransform(prototypeName, context);
            }
            
            // Generic numbered prototype fix
            if (System.Text.RegularExpressions.Regex.IsMatch(prototypeName, @".*_(\d+)$"))
            {
                return GetNumberedPrototypeTransform(prototypeName, context);
            }
            
            // Default: return original transform
            return context.currentTransform;
        }
        
        /// <summary>
        /// Calculate the correct transform matrix for tree cone prototypes
        /// Based on the tree01.rwx model structure where cones should be positioned at different heights
        /// </summary>
        private Matrix4x4 GetTreeConeTransform(string prototypeName, RWXParseContext context)
        {
            // Get the base transform (which should be at origin due to canceling translates)
            Matrix4x4 baseTransform = context.currentTransform;
            
            // Tree cone positioning - using relative offsets from cube position
            // The cube (trunk) should be in the middle of the cone layers
            // From the RWX file: Cube Z=0.207747, Cones range from 0.151067 to 0.430186
            
            float cubeZ = 0.207747f;  // Trunk position from Cube Transform matrix
            float coneZ = 0f;
            
            switch (prototypeName.ToLower())
            {
                case "cone":
                    coneZ = 0.151067f;   // From Cone Transform matrix
                    break;
                case "cone_1":
                    coneZ = 0.217371f;   // From Cone_1 Transform matrix
                    break;
                case "cone_2":
                    coneZ = 0.290092f;   // From Cone_2 Transform matrix
                    break;
                case "cone_3":
                    coneZ = 0.352118f;   // From Cone_3 Transform matrix
                    break;
                case "cone_4":
                    coneZ = 0.430186f;   // From Cone_4 Transform matrix
                    break;
                default:
                    coneZ = cubeZ;  // Default to cube position
                    break;
            }
            
            // Calculate relative offset from cube position
            float relativeOffset = coneZ - cubeZ;
            
            Debug.Log($"🌲 Tree positioning: {prototypeName} - Cube Z: {cubeZ}, Cone Z: {coneZ}, Relative offset: {relativeOffset}");
            
            // Create a translation matrix for the relative offset
            Matrix4x4 offsetTransform = Matrix4x4.Translate(new Vector3(0, 0, relativeOffset));
            
            // Apply the offset to the base transform
            return baseTransform * offsetTransform;
        }
        
        /// <summary>
        /// Calculate transform for generic numbered prototypes (e.g., part_1, part_2, etc.)
        /// Assumes they should be spaced along the Z axis
        /// </summary>
        private Matrix4x4 GetNumberedPrototypeTransform(string prototypeName, RWXParseContext context)
        {
            Matrix4x4 baseTransform = context.currentTransform;
            
            // Extract the number from the prototype name
            var match = System.Text.RegularExpressions.Regex.Match(prototypeName, @".*_(\d+)$");
            if (!match.Success) return baseTransform;
            
            int prototypeNumber = int.Parse(match.Groups[1].Value);
            
            // Generic spacing: 0.1 units per prototype number
            float zOffset = prototypeNumber * 0.1f;
            
            Debug.Log($"🔧 Generic numbered prototype fix: {prototypeName} -> Z offset: {zOffset}");
            
            // Create a translation matrix for the Z offset
            Matrix4x4 offsetTransform = Matrix4x4.Translate(new Vector3(0, 0, zOffset));
            
            return baseTransform * offsetTransform;
        }
        
        /// <summary>
        /// Extract transform matrix from a Transform command line with proper RWX row-major to Unity column-major conversion
        /// </summary>
        private Matrix4x4 ExtractTransformFromLine(string line)
        {
            var floatRegex = new Regex(@"([+-]?([0-9]+([.][0-9]*)?|[.][0-9]+)([eE][-+][0-9]+)?)", RegexOptions.IgnoreCase);
            var floatMatches = floatRegex.Matches(line);
            
            if (floatMatches.Count < 16) return Matrix4x4.identity;
            
            // Parse the 16 values from the RWX transform command
            float[] values = new float[16];
            for (int i = 0; i < 16; i++)
            {
                values[i] = float.Parse(floatMatches[i].Value, System.Globalization.CultureInfo.InvariantCulture);
            }
            
            // FIXED: RWX matrices are ROW-MAJOR with translation in the FINAL ROW
            // According to RenderWare docs: "translation in the final row"
            // RWX format: [m00,m01,m02,m03, m10,m11,m12,m13, m20,m21,m22,m23, m30,m31,m32,m33]
            // Translation is in the final row: values[12], values[13], values[14]
            // But Unity uses COLUMN-MAJOR with translation in the final column
            
            // Create RWX matrix in row-major format first
            Matrix4x4 rwxMatrix = new Matrix4x4();
            
            // Row 0: X axis
            rwxMatrix.m00 = values[0];  rwxMatrix.m01 = values[1];  rwxMatrix.m02 = values[2];  rwxMatrix.m03 = values[3];
            // Row 1: Y axis
            rwxMatrix.m10 = values[4];  rwxMatrix.m11 = values[5];  rwxMatrix.m12 = values[6];  rwxMatrix.m13 = values[7];
            // Row 2: Z axis
            rwxMatrix.m20 = values[8];  rwxMatrix.m21 = values[9];  rwxMatrix.m22 = values[10]; rwxMatrix.m23 = values[11];
            // Row 3: Translation and homogeneous coordinate
            rwxMatrix.m30 = values[12]; rwxMatrix.m31 = values[13]; rwxMatrix.m32 = values[14]; rwxMatrix.m33 = values[15];

            // Now transpose to convert from RWX row-major to Unity column-major
            Matrix4x4 matrix = rwxMatrix.transpose;
            
            // Force m33 to 1 if it's 0 (invalid for TRS)
            if (matrix.m33 == 0) matrix.m33 = 1.0f;
            
            Debug.Log($"🛏️ PROTOTYPE MATRIX EXTRACTION:");
            Debug.Log($"   RWX Row-Major Translation: ({values[12]:F6}, {values[13]:F6}, {values[14]:F6})");
            Debug.Log($"   Unity Column-Major Translation: ({matrix.m03:F6}, {matrix.m13:F6}, {matrix.m23:F6})");
            
            return matrix;
        }
        
        /// <summary>
        /// Determine if this is a bed-style prototype that needs special Transform handling
        /// </summary>
        private bool IsBedStylePrototype(string prototypeName, Matrix4x4 transform)
        {
            // Check for bed-specific prototype names
            if (prototypeName.Contains("plane") && (prototypeName.Contains("_0_") || prototypeName.Contains("_1_") || prototypeName.Contains("_2_")))
            {
                return true;
            }
            
            // Check for Transform matrices that define specific orientations (non-identity rotation components)
            // Bed prototypes have rotation matrices that orient parts correctly (horizontal mattress, vertical headboard/footboard)
            bool hasRotation = (Mathf.Abs(transform.m01) > 0.001f || Mathf.Abs(transform.m02) > 0.001f || 
                               Mathf.Abs(transform.m10) > 0.001f || Mathf.Abs(transform.m12) > 0.001f ||
                               Mathf.Abs(transform.m20) > 0.001f || Mathf.Abs(transform.m21) > 0.001f);
            
            return hasRotation;
        }
        
        /// <summary>
        /// Check if this is specifically a bed headboard/footboard prototype (plane_1_1 or plane_0_3)
        /// </summary>
        private bool IsBedHeadFootboardPrototype(string prototypeName, Matrix4x4 transform)
        {
            // Check for the specific headboard/footboard prototypes
            if (prototypeName == "plane_1_1" || prototypeName == "plane_0_3")
            {
                return true;
            }
            
            // Check for the specific transform pattern that indicates flipped geometry
            // These prototypes have negative determinants and specific rotation patterns
            float det = transform.m00 * (transform.m11 * transform.m22 - transform.m12 * transform.m21) -
                       transform.m01 * (transform.m10 * transform.m22 - transform.m12 * transform.m20) +
                       transform.m02 * (transform.m10 * transform.m21 - transform.m11 * transform.m20);
            
            // Check for negative Z component (m02) and negative Y component (m11)
            bool hasNegativeZ = transform.m02 < -0.3f;
            bool hasNegativeY = transform.m11 < -0.9f;
            bool hasSmallPositiveZRot = transform.m22 > 0.3f && transform.m22 < 0.4f;
            bool hasSignificantYTranslation = Mathf.Abs(transform.m13) > 1.0f;
            
            return hasNegativeZ && hasNegativeY && hasSmallPositiveZRot && hasSignificantYTranslation;
        }
        
        /// <summary>
        /// Apply transform matrix to a prototype instance GameObject with proper coordinate system conversion
        /// </summary>
        private void ApplyTransformToInstance(GameObject instanceObject, Matrix4x4 transform)
        {
            // Apply the same coordinate system conversion as we do for character models
            Matrix4x4 unityMatrix = ConvertRWXMatrixToUnity(transform);
            
            // Try to decompose the matrix into TRS components
            Vector3 position, scale;
            Quaternion rotation;
            
            if (TryDecomposeMatrix(unityMatrix, out position, out rotation, out scale))
            {
                // Apply the decomposed transform
                instanceObject.transform.localPosition = position;
                instanceObject.transform.localRotation = rotation;
                instanceObject.transform.localScale = scale;
                
                Debug.Log($"🛏️ Applied prototype transform - Position: {position:F6}, Rotation: {rotation}, Scale: {scale:F6}");
            }
            else
            {
                // Fallback: only extract translation if matrix decomposition fails
                Vector3 rwxPosition = new Vector3(transform.m03, transform.m13, transform.m23);
                Vector3 fallbackPosition = new Vector3(-rwxPosition.x, rwxPosition.y, rwxPosition.z);
                
                instanceObject.transform.localPosition = fallbackPosition;
                Debug.Log($"🛏️ Applied fallback position: {fallbackPosition:F6}");
            }
        }
        
        /// <summary>
        /// Convert RWX matrix to Unity coordinate system (same logic as in RWXParser)
        /// </summary>
        private Matrix4x4 ConvertRWXMatrixToUnity(Matrix4x4 rwxMatrix)
        {
            // Reflect across X on both sides to convert the right-handed RWX matrix to Unity's left-handed space.
            Matrix4x4 unityMatrix = RWXParser.RwxToUnityReflection * rwxMatrix * RWXParser.RwxToUnityReflection;

            Debug.Log($"🔄 PROTOTYPE MATRIX CONVERSION");
            Debug.Log($"   RWX Translation: ({rwxMatrix.m03:F6}, {rwxMatrix.m13:F6}, {rwxMatrix.m23:F6})");
            Debug.Log($"   Unity Translation: ({unityMatrix.m03:F6}, {unityMatrix.m13:F6}, {unityMatrix.m23:F6})");

            return unityMatrix;
        }
        
        /// <summary>
        /// Try to decompose a matrix into TRS components (same logic as in RWXParser)
        /// </summary>
        private bool TryDecomposeMatrix(Matrix4x4 matrix, out Vector3 position, out Quaternion rotation, out Vector3 scale)
        {
            position = Vector3.zero;
            rotation = Quaternion.identity;
            scale = Vector3.one;
            
            try
            {
                // Extract translation
                position = new Vector3(matrix.m03, matrix.m13, matrix.m23);
                
                // Validate translation
                if (!IsValidFloat(position.x) || !IsValidFloat(position.y) || !IsValidFloat(position.z))
                {
                    Debug.LogWarning("Invalid translation values detected, using zero");
                    position = Vector3.zero;
                }
                
                // Extract scale vectors
                Vector3 scaleX = new Vector3(matrix.m00, matrix.m10, matrix.m20);
                Vector3 scaleY = new Vector3(matrix.m01, matrix.m11, matrix.m21);
                Vector3 scaleZ = new Vector3(matrix.m02, matrix.m12, matrix.m22);
                
                scale.x = scaleX.magnitude;
                scale.y = scaleY.magnitude;
                scale.z = scaleZ.magnitude;
                
                // Validate scale values
                if (!IsValidFloat(scale.x) || Mathf.Approximately(scale.x, 0f)) scale.x = 1.0f;
                if (!IsValidFloat(scale.y) || Mathf.Approximately(scale.y, 0f)) scale.y = 1.0f;
                if (!IsValidFloat(scale.z) || Mathf.Approximately(scale.z, 0f)) scale.z = 1.0f;
                
                // Check for negative determinant (indicates reflection)
                float det = matrix.determinant;
                if (det < 0)
                {
                    scale.x = -scale.x;
                }
                
                // Create rotation matrix by removing scale
                Matrix4x4 rotMatrix = matrix;
                rotMatrix.m00 /= scale.x; rotMatrix.m10 /= scale.x; rotMatrix.m20 /= scale.x;
                rotMatrix.m01 /= scale.y; rotMatrix.m11 /= scale.y; rotMatrix.m21 /= scale.y;
                rotMatrix.m02 /= scale.z; rotMatrix.m12 /= scale.z; rotMatrix.m22 /= scale.z;
                
                // Clear translation and ensure proper homogeneous coordinate
                rotMatrix.m03 = 0; rotMatrix.m13 = 0; rotMatrix.m23 = 0; rotMatrix.m33 = 1;
                
                // Extract rotation quaternion
                rotation = rotMatrix.rotation;
                
                // Validate quaternion
                if (!IsValidFloat(rotation.x) || !IsValidFloat(rotation.y) || 
                    !IsValidFloat(rotation.z) || !IsValidFloat(rotation.w))
                {
                    Debug.LogWarning("Invalid quaternion values detected, using identity rotation");
                    rotation = Quaternion.identity;
                }
                else
                {
                    // Normalize quaternion to ensure it's valid
                    rotation = rotation.normalized;
                }
                
                return true;
            }
            catch (System.Exception e)
            {
                Debug.LogError($"Exception during prototype matrix decomposition: {e.Message}");
                return false;
            }
        }
        
        /// <summary>
        /// Check if a float value is valid (not NaN or infinity)
        /// </summary>
        private bool IsValidFloat(float value)
        {
            return !float.IsNaN(value) && !float.IsInfinity(value);
        }
    }
}
