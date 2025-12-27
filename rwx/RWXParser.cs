using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text.RegularExpressions;
using UnityEngine;

namespace RWXLoader
{
    public class RWXParser
    {
        private static readonly RegexOptions DefaultRegexOptions = RegexOptions.IgnoreCase | RegexOptions.Compiled;

        // Regex patterns for parsing RWX files
        private readonly Regex vertexRegex = new Regex(@"^\s*(vertex|vertexext)((\s+[+-]?([0-9]+([.][0-9]*)?|[.][0-9]+)(e[-+][0-9]+)?){3})\s*(uv((\s+[+-]?([0-9]+([.][0-9]*)?|[.][0-9]+)(e[-+][0-9]+)?){2}))?.*$", DefaultRegexOptions);
        private readonly Regex triangleRegex = new Regex(@"^\s*(triangle|triangleext)((\s+([0-9]+)){3})(\s+tag\s+([0-9]+))?.*$", DefaultRegexOptions);
        private readonly Regex quadRegex = new Regex(@"^\s*(quad|quadext)((\s+([0-9]+)){4})(\s+tag\s+([0-9]+))?.*$", DefaultRegexOptions);
        private readonly Regex polygonRegex = new Regex(@"^\s*(polygon|polygonext)(\s+[0-9]+)((\s+[0-9]+)+)(\s+tag\s+([0-9]+))?.*$", DefaultRegexOptions);
        private readonly Regex textureRegex = new Regex(@"^\s*texture\s+(?<texture>[A-Za-z0-9_\-\/:.]+)(?:\s+(?<rest>.*))?$", DefaultRegexOptions);
        private readonly Regex colorRegex = new Regex(@"^\s*(color)((\s+[+-]?([0-9]+([.][0-9]*)?|[.][0-9]+)(e[-+][0-9]+)?){3}).*$", DefaultRegexOptions);
        private readonly Regex opacityRegex = new Regex(@"^\s*(opacity)(\s+[+-]?([0-9]+([.][0-9]*)?|[.][0-9]+)(e[-+][0-9]+)?).*$", DefaultRegexOptions);
        private readonly Regex surfaceRegex = new Regex(@"^\s*(surface)((\s+[+-]?([0-9]+([.][0-9]*)?|[.][0-9]+)(e[-+][0-9]+)?){3}).*$", DefaultRegexOptions);
        private readonly Regex ambientRegex = new Regex(@"^\s*(ambient)(\s+[+-]?([0-9]+([.][0-9]*)?|[.][0-9]+)(e[-+][0-9]+)?).*$", DefaultRegexOptions);
        private readonly Regex diffuseRegex = new Regex(@"^\s*(diffuse)(\s+[+-]?([0-9]+([.][0-9]*)?|[.][0-9]+)(e[-+][0-9]+)?).*$", DefaultRegexOptions);
        private readonly Regex specularRegex = new Regex(@"^\s*(specular)(\s+[+-]?([0-9]+([.][0-9]*)?|[.][0-9]+)(e[-+][0-9]+)?).*$", DefaultRegexOptions);
        private readonly Regex materialModeRegex = new Regex(@"^\s*((add)?materialmode(s)?)\s+([A-Za-z0-9_\-]+).*$", DefaultRegexOptions);
        private readonly Regex lightSamplingRegex = new Regex(@"^\s*(lightsampling)\s+(facet|vertex).*$", DefaultRegexOptions);
        private readonly Regex geometrySamplingRegex = new Regex(@"^\s*(geometrysampling)\s+(pointcloud|wireframe|solid).*$", DefaultRegexOptions);
        private readonly Regex textureModesRegex = new Regex(@"^\s*(texturemode(s)?)((\s+null)|(\s+lit|\s+foreshorten|\s+filter)+).*$", DefaultRegexOptions);
        private readonly Regex clumpBeginRegex = new Regex(@"^\s*(clumpbegin).*$", DefaultRegexOptions);
        private readonly Regex clumpEndRegex = new Regex(@"^\s*(clumpend).*$", DefaultRegexOptions);
        private readonly Regex transformBeginRegex = new Regex(@"^\s*(transformbegin).*$", DefaultRegexOptions);
        private readonly Regex transformEndRegex = new Regex(@"^\s*(transformend).*$", DefaultRegexOptions);
        private readonly Regex translateRegex = new Regex(@"^\s*(translate)((\s+[+-]?([0-9]+([.][0-9]*)?|[.][0-9]+)(e[-+][0-9]+)?){3}).*$", DefaultRegexOptions);
        private readonly Regex rotateRegex = new Regex(@"^\s*(rotate)((\s+[+-]?([0-9]+([.][0-9]*)?|[.][0-9]+)(e[-+][0-9]+)?){4})$", DefaultRegexOptions);
        private readonly Regex scaleRegex = new Regex(@"^\s*(scale)((\s+[+-]?([0-9]+([.][0-9]*)?|[.][0-9]+)(e[-+][0-9]+)?){3}).*$", DefaultRegexOptions);
        private readonly Regex identityRegex = new Regex(@"^\s*(identity)\s*$", DefaultRegexOptions);
        private readonly Regex transformRegex = new Regex(@"^\s*(transform)((\s+[+-]?([0-9]+([.][0-9]*)?|[.][0-9]+)(e[-+][0-9]+)?){16}).*$", DefaultRegexOptions);
        private readonly Regex jointtransformBeginRegex = new Regex(@"^\s*(jointtransformbegin).*$", DefaultRegexOptions);
        private readonly Regex jointtransformEndRegex = new Regex(@"^\s*(jointtransformend).*$", DefaultRegexOptions);
        private readonly Regex identityJointRegex = new Regex(@"^\s*(identityjoint).*$", DefaultRegexOptions);
        private readonly Regex rotateJointTMRegex = new Regex(@"^\s*(rotatejointtm)((\s+[+-]?([0-9]+([.][0-9]*)?|[.][0-9]+)(e[-+][0-9]+)?){4}).*$", DefaultRegexOptions);
        private readonly Regex floatRegex = new Regex(@"([+-]?([0-9]+([.][0-9]*)?|[.][0-9]+)([eE][-+][0-9]+)?)", DefaultRegexOptions);
        private readonly Regex integerRegex = new Regex(@"([-+]?[0-9]+)", DefaultRegexOptions);
        private static readonly Regex textureAttributeRegex = new Regex(@"\b(mask|normal|specular)\s+([A-Za-z0-9_\-\/:.]+)", DefaultRegexOptions);

        internal static readonly Matrix4x4 RwxToUnityReflection = Matrix4x4.Scale(new Vector3(-1f, 1f, 1f));

        private RWXMeshBuilder meshBuilder;
        private RWXPrototypeParser prototypeParser;
        private readonly Dictionary<string, Func<string, RWXParseContext, bool>> commandHandlers;

        public RWXParser(RWXMeshBuilder meshBuilder)
        {
            this.meshBuilder = meshBuilder;
            this.prototypeParser = new RWXPrototypeParser(this, meshBuilder);
            commandHandlers = new Dictionary<string, Func<string, RWXParseContext, bool>>(StringComparer.OrdinalIgnoreCase)
            {
                { "vertex", ProcessVertex },
                { "vertexext", ProcessVertex },
                { "triangle", ProcessTriangle },
                { "triangleext", ProcessTriangle },
                { "quad", ProcessQuad },
                { "quadext", ProcessQuad },
                { "polygon", ProcessPolygon },
                { "polygonext", ProcessPolygon },
                { "texture", ProcessTexture },
                { "color", ProcessColor },
                { "opacity", ProcessOpacity },
                { "surface", ProcessSurface },
                { "ambient", ProcessAmbient },
                { "diffuse", ProcessDiffuse },
                { "specular", ProcessSpecular },
                { "materialmode", ProcessMaterialMode },
                { "materialmodes", ProcessMaterialMode },
                { "addmaterialmode", ProcessMaterialMode },
                { "addmaterialmodes", ProcessMaterialMode },
                { "lightsampling", ProcessLightSampling },
                { "geometrysampling", ProcessGeometrySampling },
                { "texturemode", ProcessTextureModes },
                { "texturemodes", ProcessTextureModes },
                { "clumpbegin", ProcessClumpBegin },
                { "clumpend", ProcessClumpEnd },
                { "transformbegin", ProcessTransformBegin },
                { "transformend", ProcessTransformEnd },
                { "translate", ProcessTranslate },
                { "rotate", ProcessRotate },
                { "scale", ProcessScale },
                { "identity", ProcessIdentity },
                { "transform", ProcessTransform },
                { "jointtransformbegin", ProcessJointTransformBegin },
                { "jointtransformend", ProcessJointTransformEnd },
                { "identityjoint", ProcessIdentityJoint },
                { "rotatejointtm", ProcessRotateJointTM }
            };
        }

        /// <summary>
        /// Reset the parser state for a new model
        /// </summary>
        public void Reset()
        {
            prototypeParser?.Reset();
        }

        private static string ExtractCommandToken(string line)
        {
            if (string.IsNullOrEmpty(line))
                return string.Empty;

            int start = 0;
            while (start < line.Length && char.IsWhiteSpace(line[start]))
            {
                start++;
            }

            if (start >= line.Length)
                return string.Empty;

            int end = start;
            while (end < line.Length && !char.IsWhiteSpace(line[end]))
            {
                end++;
            }

            return line.Substring(start, end - start);
        }

        public void ProcessLine(string line, RWXParseContext context)
        {
            if (string.IsNullOrWhiteSpace(line))
                return;

            // Remove comments without invoking regex each line
            line = StripComments(line);
            if (string.IsNullOrWhiteSpace(line))
                return;

            // First check if this is a prototype command
            if (prototypeParser.ProcessLine(line, context)) return;

            // Try a direct dispatch based on the first token to avoid running every regex
            string command = ExtractCommandToken(line);
            if (!string.IsNullOrEmpty(command) && commandHandlers.TryGetValue(command, out var handler))
            {
                if (handler(line, context))
                {
                    return;
                }
            }

            // Fallback to regex checks for any remaining or unregistered commands
            if (ProcessVertex(line, context)) return;
            if (ProcessTriangle(line, context)) return;
            if (ProcessQuad(line, context)) return;
            if (ProcessPolygon(line, context)) return;
            if (ProcessTexture(line, context)) return;
            if (ProcessColor(line, context)) return;
            if (ProcessOpacity(line, context)) return;
            if (ProcessSurface(line, context)) return;
            if (ProcessAmbient(line, context)) return;
            if (ProcessDiffuse(line, context)) return;
            if (ProcessSpecular(line, context)) return;
            if (ProcessMaterialMode(line, context)) return;
            if (ProcessLightSampling(line, context)) return;
            if (ProcessGeometrySampling(line, context)) return;
            if (ProcessTextureModes(line, context)) return;
            if (ProcessClumpBegin(line, context)) return;
            if (ProcessClumpEnd(line, context)) return;
            if (ProcessTransformBegin(line, context)) return;
            if (ProcessTransformEnd(line, context)) return;
            if (ProcessTranslate(line, context)) return;
            if (ProcessRotate(line, context)) return;
            if (ProcessScale(line, context)) return;
            if (ProcessIdentity(line, context)) return;
            if (ProcessTransform(line, context)) return;
            if (ProcessJointTransformBegin(line, context)) return;
            if (ProcessJointTransformEnd(line, context)) return;
            if (ProcessIdentityJoint(line, context)) return;
            if (ProcessRotateJointTM(line, context)) return;
        }

        private bool ProcessVertex(string line, RWXParseContext context)
        {
            if (TryProcessVertexFast(line.AsSpan(), context))
            {
                return true;
            }

            var match = vertexRegex.Match(line);
            if (!match.Success) return false;

            var floatMatches = floatRegex.Matches(match.Groups[2].Value);
            if (floatMatches.Count < 3) return false;

            float x = float.Parse(floatMatches[0].Value, CultureInfo.InvariantCulture);
            float y = float.Parse(floatMatches[1].Value, CultureInfo.InvariantCulture);
            float z = float.Parse(floatMatches[2].Value, CultureInfo.InvariantCulture);

            // Store RWX coordinates directly - coordinate conversion will be handled at transform level
            Vector3 position = new Vector3(x, y, z);
            Vector2 uv = Vector2.zero;

            // Check for UV coordinates
            if (match.Groups[7].Success)
            {
                var uvMatches = floatRegex.Matches(match.Groups[7].Value);
                if (uvMatches.Count >= 2)
                {
                    float u = float.Parse(uvMatches[0].Value, CultureInfo.InvariantCulture);
                    float v = float.Parse(uvMatches[1].Value, CultureInfo.InvariantCulture);

                    // Standard UV flip for RWX to Unity conversion
                    uv = new Vector2(u, 1.0f - v);

                    // Additional UV correction for prototypes with specific Transform matrices
                    // that cause texture orientation issues (like bed headboard/footboard)
                    if (prototypeParser.IsInPrototype && NeedsUVCorrection(context))
                    {
                        // For prototypes with rotation matrices that flip textures, apply additional correction
                        uv = new Vector2(uv.x, 1.0f - uv.y); // Double flip to correct orientation
                        Debug.Log($"🎨 UV CORRECTION: Applied additional UV flip for prototype texture orientation");
                    }
                }
            }

            // Apply prototype transform if we're inside a prototype definition
            if (prototypeParser.IsInPrototype && context.currentTransform != Matrix4x4.identity)
            {
                Vector4 homogeneousPos = new Vector4(position.x, position.y, position.z, 1.0f);
                Vector4 transformedPos = context.currentTransform * homogeneousPos;
                position = new Vector3(transformedPos.x, transformedPos.y, transformedPos.z);
            }

            context.vertices.Add(new RWXVertex(position, uv));
            return true;
        }
        
        private string GetHierarchyPath(RWXParseContext context)
        {
            var path = new List<string>();
            var stack = new Stack<GameObject>(context.objectStack);
            
            // Add current object
            if (context.currentObject != null)
                path.Add(context.currentObject.name);
                
            // Add all objects in stack (from bottom to top)
            while (stack.Count > 0)
            {
                var obj = stack.Pop();
                if (obj != null)
                    path.Add(obj.name);
            }
            
            path.Reverse(); // Show root → leaf order
            return string.Join(" → ", path);
        }

        private bool ProcessTriangle(string line, RWXParseContext context)
        {
            if (TryProcessTriangleFast(line.AsSpan(), context))
            {
                return true;
            }

            var match = triangleRegex.Match(line);
            if (!match.Success) return false;

            var intMatches = integerRegex.Matches(match.Groups[2].Value);
            if (intMatches.Count < 3) return false;

            int a = int.Parse(intMatches[0].Value) - 1; // RWX uses 1-based indexing
            int b = int.Parse(intMatches[1].Value) - 1;
            int c = int.Parse(intMatches[2].Value) - 1;

            int? tagOverride = null;
            if (TryExtractTagFromLine(line, out int parsedTag))
            {
                tagOverride = parsedTag;
            }

            // Use original triangle order since coordinate conversion is handled at matrix level
            ApplyGeometryTag(context, tagOverride, () =>
            {
                meshBuilder.CreateTriangle(context, a, b, c);
            });
            return true;
        }

        private bool ProcessQuad(string line, RWXParseContext context)
        {
            if (TryProcessQuadFast(line.AsSpan(), context))
            {
                return true;
            }

            var match = quadRegex.Match(line);
            if (!match.Success) return false;

            var intMatches = integerRegex.Matches(match.Groups[2].Value);
            if (intMatches.Count < 4) return false;

            int a = int.Parse(intMatches[0].Value) - 1;
            int b = int.Parse(intMatches[1].Value) - 1;
            int c = int.Parse(intMatches[2].Value) - 1;
            int d = int.Parse(intMatches[3].Value) - 1;

            int? tagOverride = null;
            if (TryExtractTagFromLine(line, out int parsedTag))
            {
                tagOverride = parsedTag;
            }

            // Use original quad order since we're handling coordinate conversion at root level
            ApplyGeometryTag(context, tagOverride, () =>
            {
                meshBuilder.CreateQuad(context, a, b, c, d);
            });
            return true;
        }

        private bool ProcessPolygon(string line, RWXParseContext context)
        {
            if (TryProcessPolygonFast(line.AsSpan(), context))
            {
                return true;
            }

            var match = polygonRegex.Match(line);
            if (!match.Success) return false;

            var intMatches = integerRegex.Matches(line);
            if (intMatches.Count < 2) return false;

            int polyLen = int.Parse(intMatches[0].Value);
            var indices = new List<int>();

            for (int i = 1; i <= polyLen && i < intMatches.Count; i++)
            {
                indices.Add(int.Parse(intMatches[i].Value) - 1);
            }

            // Use original polygon order since we're handling coordinate conversion at root level
            // indices.Reverse(); // Removed - no longer needed
            int? tagOverride = null;
            if (TryExtractTagFromLine(line, out int parsedTag))
            {
                tagOverride = parsedTag;
            }

            ApplyGeometryTag(context, tagOverride, () =>
            {
                meshBuilder.CreatePolygon(context, indices);
            });
            return true;
        }

        private bool ProcessTexture(string line, RWXParseContext context)
        {
            if (TryProcessTextureFast(line.AsSpan(), context))
            {
                return true;
            }

            var match = textureRegex.Match(line);
            if (!match.Success) return false;

            string textureName = match.Groups["texture"].Value.ToLower();

            if (textureName == "null")
            {
                context.currentMaterial.texture = null;
            }
            else
            {
                context.currentMaterial.texture = textureName;
            }

            context.currentMaterial.mask = null;
            context.currentMaterial.normalMap = null;
            context.currentMaterial.specularMap = null;

            // Parse additional attributes
            string rest = match.Groups["rest"]?.Value ?? "";
            var attrMatches = textureAttributeRegex.Matches(rest);

            foreach (Match attrMatch in attrMatches)
            {
                string key = attrMatch.Groups[1].Value.ToLower();
                string value = attrMatch.Groups[2].Value;

                switch (key)
                {
                    case "mask":
                        context.currentMaterial.mask = value;
                        break;
                    case "normal":
                        context.currentMaterial.normalMap = value;
                        break;
                    case "specular":
                        context.currentMaterial.specularMap = value;
                        break;
                }
            }

            return true;
        }

        private bool ProcessColor(string line, RWXParseContext context)
        {
            if (TryProcessColorFast(line.AsSpan(), context))
            {
                return true;
            }

            var match = colorRegex.Match(line);
            if (!match.Success) return false;

            var floatMatches = floatRegex.Matches(match.Groups[2].Value);
            if (floatMatches.Count < 3) return false;

            float r = float.Parse(floatMatches[0].Value, CultureInfo.InvariantCulture);
            float g = float.Parse(floatMatches[1].Value, CultureInfo.InvariantCulture);
            float b = float.Parse(floatMatches[2].Value, CultureInfo.InvariantCulture);

            context.currentMaterial.color = new Color(r, g, b, context.currentMaterial.color.a);
            return true;
        }

        private bool ProcessOpacity(string line, RWXParseContext context)
        {
            if (TryProcessOpacityFast(line.AsSpan(), context))
            {
                return true;
            }

            var match = opacityRegex.Match(line);
            if (!match.Success) return false;

            float opacity = float.Parse(match.Groups[2].Value, CultureInfo.InvariantCulture);
            context.currentMaterial.opacity = opacity;
            context.currentMaterial.color = new Color(
                context.currentMaterial.color.r,
                context.currentMaterial.color.g,
                context.currentMaterial.color.b,
                opacity
            );
            return true;
        }

        private bool ProcessSurface(string line, RWXParseContext context)
        {
            if (TryProcessSurfaceFast(line.AsSpan(), context))
            {
                return true;
            }

            var match = surfaceRegex.Match(line);
            if (!match.Success) return false;

            var floatMatches = floatRegex.Matches(match.Groups[2].Value);
            if (floatMatches.Count < 3) return false;

            float ambient = float.Parse(floatMatches[0].Value, CultureInfo.InvariantCulture);
            float diffuse = float.Parse(floatMatches[1].Value, CultureInfo.InvariantCulture);
            float specular = float.Parse(floatMatches[2].Value, CultureInfo.InvariantCulture);

            context.currentMaterial.surface = new Vector3(ambient, diffuse, specular);
            return true;
        }

        private bool ProcessAmbient(string line, RWXParseContext context)
        {
            if (TryProcessAmbientFast(line.AsSpan(), context))
            {
                return true;
            }

            var match = ambientRegex.Match(line);
            if (!match.Success) return false;

            float ambient = float.Parse(match.Groups[2].Value, CultureInfo.InvariantCulture);
            context.currentMaterial.surface = new Vector3(ambient, context.currentMaterial.surface.y, context.currentMaterial.surface.z);
            return true;
        }

        private bool ProcessDiffuse(string line, RWXParseContext context)
        {
            if (TryProcessDiffuseFast(line.AsSpan(), context))
            {
                return true;
            }

            var match = diffuseRegex.Match(line);
            if (!match.Success) return false;

            float diffuse = float.Parse(match.Groups[2].Value, CultureInfo.InvariantCulture);
            context.currentMaterial.surface = new Vector3(context.currentMaterial.surface.x, diffuse, context.currentMaterial.surface.z);
            return true;
        }

        private bool ProcessSpecular(string line, RWXParseContext context)
        {
            if (TryProcessSpecularFast(line.AsSpan(), context))
            {
                return true;
            }

            var match = specularRegex.Match(line);
            if (!match.Success) return false;

            float specular = float.Parse(match.Groups[2].Value, CultureInfo.InvariantCulture);
            context.currentMaterial.surface = new Vector3(context.currentMaterial.surface.x, context.currentMaterial.surface.y, specular);
            return true;
        }

        private bool ProcessMaterialMode(string line, RWXParseContext context)
        {
            var match = materialModeRegex.Match(line);
            if (!match.Success) return false;

            string mode = match.Groups[4].Value.ToUpper();
            switch (mode)
            {
                case "NONE":
                    context.currentMaterial.materialMode = MaterialMode.None;
                    break;
                case "NULL":
                    context.currentMaterial.materialMode = MaterialMode.Null;
                    break;
                case "DOUBLE":
                    context.currentMaterial.materialMode = MaterialMode.Double;
                    break;
            }
            return true;
        }

        private bool ProcessLightSampling(string line, RWXParseContext context)
        {
            var match = lightSamplingRegex.Match(line);
            if (!match.Success) return false;

            string sampling = match.Groups[2].Value.ToUpper();
            switch (sampling)
            {
                case "FACET":
                    context.currentMaterial.lightSampling = LightSampling.Facet;
                    break;
                case "VERTEX":
                    context.currentMaterial.lightSampling = LightSampling.Vertex;
                    break;
            }
            return true;
        }

        private bool ProcessGeometrySampling(string line, RWXParseContext context)
        {
            var match = geometrySamplingRegex.Match(line);
            if (!match.Success) return false;

            string sampling = match.Groups[2].Value.ToUpper();
            switch (sampling)
            {
                case "POINTCLOUD":
                    context.currentMaterial.geometrySampling = GeometrySampling.PointCloud;
                    break;
                case "WIREFRAME":
                    context.currentMaterial.geometrySampling = GeometrySampling.Wireframe;
                    break;
                case "SOLID":
                    context.currentMaterial.geometrySampling = GeometrySampling.Solid;
                    break;
            }
            return true;
        }

        private bool ProcessTextureModes(string line, RWXParseContext context)
        {
            var match = textureModesRegex.Match(line);
            if (!match.Success) return false;

            context.currentMaterial.textureModes.Clear();

            if (!match.Groups[4].Success) // Not NULL mode
            {
                string modes = match.Groups[3].Value;
                if (modes.Contains("lit")) context.currentMaterial.textureModes.Add(TextureMode.Lit);
                if (modes.Contains("foreshorten")) context.currentMaterial.textureModes.Add(TextureMode.Foreshorten);
                if (modes.Contains("filter")) context.currentMaterial.textureModes.Add(TextureMode.Filter);
            }

            return true;
        }

        private bool ProcessClumpBegin(string line, RWXParseContext context)
        {
            if (!clumpBeginRegex.IsMatch(line)) return false;

            meshBuilder.CommitCurrentMesh(context);

            // Preserve the incoming transform so we can compute a clump-local matrix
            // when ending the clump without reapplying the parent portion.
            context.clumpTransformStack.Push(context.currentTransform);

            // Generate a more descriptive name based on hierarchy depth
            int depth = context.objectStack.Count;
            string clumpName = $"Clump_Depth{depth}";
            var newObject = new GameObject(clumpName);
            newObject.transform.SetParent(context.currentObject.transform);
            
            context.objectStack.Push(context.currentObject);
            context.materialStack.Push(context.currentMaterial.Clone());
            context.currentObject = newObject;

            // FIXED: Clear vertices when starting a new clump since each clump has its own vertex space
            // In RWX, each clump starts vertex numbering from 1 again
            context.vertices.Clear();

            // Reset baked-prototype flag for this clump scope
            context.hasBakedPrototypeInstances = false;

            Debug.Log($"🎯 CLUMP BEGIN - Depth: {depth}");
            Debug.Log($"   📦 Created: '{clumpName}' - clump-local matrix will be derived from parent on clumpend");
            Debug.Log($"   ═══════════════════════════════════════");

            return true;
        }

        internal Matrix4x4 ConvertRWXMatrixToUnity(Matrix4x4 rwxMatrix, RWXParseContext context)
        {
            // Reflect across X on both sides to convert the right-handed RWX matrix to Unity's left-handed space.
            Matrix4x4 unityMatrix = RwxToUnityReflection * rwxMatrix * RwxToUnityReflection;

            string hierarchyPath = GetHierarchyPath(context);
            Debug.Log($"🔄 MATRIX CONVERSION | {hierarchyPath}");
            Debug.Log($"   RWX Translation: ({rwxMatrix.m03:F6}, {rwxMatrix.m13:F6}, {rwxMatrix.m23:F6})");
            Debug.Log($"   Unity Translation: ({unityMatrix.m03:F6}, {unityMatrix.m13:F6}, {unityMatrix.m23:F6})");
            Debug.Log($"   Stack Depth: {context.objectStack.Count}");

            return unityMatrix;
        }

        internal void ApplyTransformToObject(Matrix4x4 rwxMatrix, GameObject target, RWXParseContext context)
        {
            if (target == null)
            {
                return;
            }

            Matrix4x4 unityMatrix = ConvertRWXMatrixToUnity(rwxMatrix, context);

            Vector3 position, scale;
            Quaternion rotation;

            if (TryDecomposeMatrix(unityMatrix, out position, out rotation, out scale))
            {
                target.transform.localPosition = position;
                target.transform.localRotation = rotation;
                target.transform.localScale = scale;

                Debug.Log($"   🔄 Applied accumulated transform:");
                Debug.Log($"   📍 Local Position: {position:F6}");
                Debug.Log($"   🔄 Local Rotation: {rotation}");
                Debug.Log($"   📏 Local Scale: {scale:F6}");
            }
            else
            {
                Vector3 rwxPosition = new Vector3(rwxMatrix.m03, rwxMatrix.m13, rwxMatrix.m23);
                Vector3 fallbackPosition = new Vector3(-rwxPosition.x, rwxPosition.y, rwxPosition.z);

                target.transform.localPosition = fallbackPosition;
                Debug.Log($"   📍 Applied fallback position: {fallbackPosition:F6}");
            }
        }

        private bool IsValidFloat(float value)
        {
            return !float.IsNaN(value) && !float.IsInfinity(value);
        }
        
        private bool IsValidMatrix(Matrix4x4 matrix)
        {
            // Check all matrix elements for NaN or infinity
            for (int i = 0; i < 16; i++)
            {
                if (!IsValidFloat(matrix[i]))
                {
                    return false;
                }
            }
            
            // Check if determinant is valid (not zero, NaN, or infinity)
            float det = matrix.determinant;
            if (!IsValidFloat(det) || Mathf.Approximately(det, 0f))
            {
                return false;
            }
            
            return true;
        }
        
        private Matrix4x4 SanitizeMatrix(Matrix4x4 matrix)
        {
            Matrix4x4 sanitized = matrix;
            
            // Replace any NaN or infinity values with safe defaults
            for (int i = 0; i < 16; i++)
            {
                if (!IsValidFloat(matrix[i]))
                {
                    // For diagonal elements, use 1.0, for others use 0.0
                    if (i == 0 || i == 5 || i == 10 || i == 15) // m00, m11, m22, m33
                    {
                        sanitized[i] = 1.0f;
                    }
                    else
                    {
                        sanitized[i] = 0.0f;
                    }
                }
            }
            
            // Ensure m33 is 1 for affine transformations
            if (!IsValidFloat(sanitized.m33) || Mathf.Approximately(sanitized.m33, 0f))
            {
                sanitized.m33 = 1.0f;
            }
            
            return sanitized;
        }
        
        private bool TryDecomposeMatrix(Matrix4x4 matrix, out Vector3 position, out Quaternion rotation, out Vector3 scale)
        {
            position = Vector3.zero;
            rotation = Quaternion.identity;
            scale = Vector3.one;
            
            try
            {
                // First validate and sanitize the matrix
                if (!IsValidMatrix(matrix))
                {
                    Debug.LogWarning($"Invalid matrix detected, attempting to sanitize. Original determinant: {matrix.determinant}");
                    matrix = SanitizeMatrix(matrix);
                    
                    // If still invalid after sanitization, fail
                    if (!IsValidMatrix(matrix))
                    {
                        Debug.LogError("Matrix could not be sanitized, decomposition failed");
                        return false;
                    }
                }
                
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
                
                // Validate rotation matrix before extracting quaternion
                // Check if the rotation part is orthogonal (det should be ±1)
                float rotDet = rotMatrix.m00 * (rotMatrix.m11 * rotMatrix.m22 - rotMatrix.m12 * rotMatrix.m21) -
                              rotMatrix.m01 * (rotMatrix.m10 * rotMatrix.m22 - rotMatrix.m12 * rotMatrix.m20) +
                              rotMatrix.m02 * (rotMatrix.m10 * rotMatrix.m21 - rotMatrix.m11 * rotMatrix.m20);
                
                if (!IsValidFloat(rotDet) || Mathf.Abs(Mathf.Abs(rotDet) - 1.0f) > 0.1f)
                {
                    Debug.LogWarning($"Non-orthogonal rotation matrix detected (det={rotDet:F6}), using identity rotation");
                    rotation = Quaternion.identity;
                }
                else
                {
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
                }
                
                // Final validation of all components
                if (!IsValidFloat(position.x) || !IsValidFloat(position.y) || !IsValidFloat(position.z) ||
                    !IsValidFloat(rotation.x) || !IsValidFloat(rotation.y) || !IsValidFloat(rotation.z) || !IsValidFloat(rotation.w) ||
                    !IsValidFloat(scale.x) || !IsValidFloat(scale.y) || !IsValidFloat(scale.z))
                {
                    Debug.LogError("Final validation failed - invalid TRS components");
                    return false;
                }
                
                return true;
            }
            catch (System.Exception e)
            {
                Debug.LogError($"Exception during matrix decomposition: {e.Message}");
                return false;
            }
        }

        private bool ProcessClumpEnd(string line, RWXParseContext context)
        {
            if (!clumpEndRegex.IsMatch(line)) return false;

            meshBuilder.CommitCurrentMesh(context);

            string currentName = context.currentObject != null ? context.currentObject.name : "NULL";
            int depth = context.objectStack.Count;
            
            Debug.Log($"🏁 CLUMP END - Depth: {depth}");
            Debug.Log($"   📦 Ending: '{currentName}'");

            // Apply the accumulated transform directly to this clump's GameObject unless
            // we've already baked the parent transform into prototype instances. In that
            // case, skip reapplying it to avoid double transforms.
            if (context.currentObject != null)
            {
                if (context.hasBakedPrototypeInstances)
                {
                    Debug.Log("   ⏭️ Skipping clump transform (prototype instances already baked)");
                }
                else
                {
                    ApplyTransformToObject(context.currentTransform, context.currentObject, context);
                }
            }

            // Restore the parent transform for subsequent siblings
            Matrix4x4 parentTransform = context.clumpTransformStack.Count > 0
                ? context.clumpTransformStack.Pop()
                : Matrix4x4.identity;

            context.currentTransform = parentTransform;

            if (context.objectStack.Count > 0)
            {
                context.currentObject = context.objectStack.Pop();
                string parentName = context.currentObject != null ? context.currentObject.name : "NULL";
                Debug.Log($"   ⬆️ Returning to parent: '{parentName}'");
            }

            if (context.materialStack.Count > 0)
            {
                context.currentMaterial = context.materialStack.Pop();
            }

            // Clear baked prototype flag for future clumps
            context.hasBakedPrototypeInstances = false;

            Debug.Log($"   ═══════════════════════════════════════");

            return true;
        }

        private bool ProcessTransformBegin(string line, RWXParseContext context)
        {
            if (!transformBeginRegex.IsMatch(line)) return false;

            context.transformStack.Push(context.currentTransform);
            
            Debug.Log($"🔄 TRANSFORM BEGIN - Stack depth: {context.transformStack.Count}");
            Debug.Log($"   📋 Pushed current transform to stack");
            Debug.Log($"   🎭 Current transform matrix:");
            Debug.Log($"      [{context.currentTransform.m00:F3}, {context.currentTransform.m01:F3}, {context.currentTransform.m02:F3}, {context.currentTransform.m03:F3}]");
            Debug.Log($"      [{context.currentTransform.m10:F3}, {context.currentTransform.m11:F3}, {context.currentTransform.m12:F3}, {context.currentTransform.m13:F3}]");
            Debug.Log($"      [{context.currentTransform.m20:F3}, {context.currentTransform.m21:F3}, {context.currentTransform.m22:F3}, {context.currentTransform.m23:F3}]");
            Debug.Log($"      [{context.currentTransform.m30:F3}, {context.currentTransform.m31:F3}, {context.currentTransform.m32:F3}, {context.currentTransform.m33:F3}]");
            
            return true;
        }

        private bool ProcessTransformEnd(string line, RWXParseContext context)
        {
            if (!transformEndRegex.IsMatch(line)) return false;

            if (context.transformStack.Count > 0)
            {
                context.currentTransform = context.transformStack.Pop();
            }
            else
            {
                context.currentTransform = Matrix4x4.identity;
            }
            return true;
        }

        private bool ProcessTranslate(string line, RWXParseContext context)
        {
            if (!TryParseTranslateValues(line.AsSpan(), out var translationVector))
                return false;

            Matrix4x4 translation = Matrix4x4.Translate(translationVector);

            // Debug translate operations
            Vector3 oldPos = new Vector3(context.currentTransform.m03, context.currentTransform.m13, context.currentTransform.m23);
            context.currentTransform = context.currentTransform * translation;
            Vector3 newPos = new Vector3(context.currentTransform.m03, context.currentTransform.m13, context.currentTransform.m23);

            Debug.Log($"🔄 TRANSLATE: ({translationVector.x:F6}, {translationVector.y:F6}, {translationVector.z:F6}) | Old pos: {oldPos:F6} → New pos: {newPos:F6}");

            return true;
        }

        private bool ProcessRotate(string line, RWXParseContext context)
        {
            if (!TryParseRotateValues(line.AsSpan(), out float axisX, out float axisY, out float axisZ, out float angleDegrees))
                return false;

            // Determine if this is a root-level model orientation rotation
            bool isRootLevelRotation = IsRootLevelModelOrientation(context, axisX, axisY, axisZ, angleDegrees);
            
            Vector3 rwxAxis = new Vector3(axisX, axisY, axisZ);
            Vector3 unityAxis = rwxAxis;
            float unityAngle = angleDegrees;
            
            if (isRootLevelRotation)
            {
                // Root-level orientation rotations: these are meant to orient the model correctly
                // The common pattern "Rotate 0 1 0 180" + "Rotate 1 0 0 -90" should work as-is
                unityAngle = angleDegrees; // Keep original angles for model orientation
                Debug.Log($"🎯 ROOT-LEVEL ORIENTATION: Axis({axisX:F1}, {axisY:F1}, {axisZ:F1}) Angle({angleDegrees:F1}°) - keeping original");
            }
            else
            {
                // Internal/prototype rotations: apply coordinate system conversion
                if (Mathf.Abs(axisX) > 0.9f) // Rotation around X axis
                {
                    unityAngle = -angleDegrees;
                    Debug.Log($"🔄 Internal X-axis rotation: negating angle {angleDegrees}° → {unityAngle}°");
                }
                else if (Mathf.Abs(axisY) > 0.9f) // Rotation around Y axis
                {
                    unityAngle = -angleDegrees;
                    Debug.Log($"🔄 Internal Y-axis rotation: negating angle {angleDegrees}° → {unityAngle}°");
                }
                else if (Mathf.Abs(axisZ) > 0.9f) // Rotation around Z axis
                {
                    unityAngle = -angleDegrees;
                    Debug.Log($"🔄 Internal Z-axis rotation: negating angle {angleDegrees}° → {unityAngle}°");
                }
            }
            
            // Create rotation axis vector (normalized)
            Vector3 axis = unityAxis.normalized;
            
            // Create rotation quaternion from axis and angle
            Quaternion rotation = Quaternion.AngleAxis(unityAngle, axis);
            Matrix4x4 rotationMatrix = Matrix4x4.Rotate(rotation);
            
            // Apply rotation to current transform
            context.currentTransform = context.currentTransform * rotationMatrix;
            
            Debug.Log($"🔄 ROTATE RESULT: Axis({unityAxis.x:F1}, {unityAxis.y:F1}, {unityAxis.z:F1}) Angle({unityAngle:F1}°)");

            return true;
        }
        /// <summary>
        /// Determine if this rotation is a root-level model orientation rotation
        /// </summary>
        private bool IsRootLevelModelOrientation(RWXParseContext context, float axisX, float axisY, float axisZ, float angle)
        {
            // Check if we're at the root level (not deep in the hierarchy)
            bool isAtRootLevel = context.objectStack.Count <= 1;
            
            // Check if this matches common model orientation patterns
            bool isCommonOrientationPattern = false;
            
            // Pattern 1: Y-axis 180° rotation (common for flipping models)
            if (Mathf.Abs(axisY) > 0.9f && Mathf.Abs(angle - 180f) < 1f)
            {
                isCommonOrientationPattern = true;
            }
            
            // Pattern 2: X-axis -90° rotation (common for rotating from Z-up to Y-up)
            if (Mathf.Abs(axisX) > 0.9f && Mathf.Abs(angle - (-90f)) < 1f)
            {
                isCommonOrientationPattern = true;
            }
            
            // Pattern 3: X-axis 90° rotation (alternative orientation)
            if (Mathf.Abs(axisX) > 0.9f && Mathf.Abs(angle - 90f) < 1f)
            {
                isCommonOrientationPattern = true;
            }
            
            return isAtRootLevel && isCommonOrientationPattern;
        }

        /// <summary>
        /// Determine if UV coordinates need correction for prototype textures
        /// </summary>
        private bool NeedsUVCorrection(RWXParseContext context)
        {
            // The real issue might not be UV coordinates but triangle winding order
            // Let me try a different approach - detect bed prototypes by their transform patterns
            // and apply triangle winding correction instead of UV correction
            
            return false; // Disable UV correction for now - the issue is likely triangle winding
        }

        /// <summary>
        /// Check if this transform matrix matches bed prototype patterns
        /// </summary>
        private bool IsPotentialBedPrototype(float[] values)
        {
            // BED1.RWX headboard/footboard prototypes have specific patterns:
            // plane_1_1: Transform 0. 0. -0.4 0. -0.95 0. 0. 0. 0. 0.319999 0. 0. 0.000001 1.25 0.600001 1.
            // plane_0_3: Transform 0. 0. -0.65 0. -0.95 0. 0. 0. 0. 0.319999 0. 0. 0.000002 -1.25 0.850001 1.
            
            // Check for the specific pattern: values[2] negative, values[4] negative around -0.95, values[9] around 0.32
            bool hasNegativeZ = values[2] < -0.3f; // Third element (Z component of first row)
            bool hasNegativeY = values[4] < -0.9f; // Fifth element (Y component of second row)  
            bool hasSmallPositiveZRot = values[9] > 0.3f && values[9] < 0.4f; // Tenth element
            bool hasSignificantYTranslation = Mathf.Abs(values[13]) > 1.0f; // Y translation
            
            return hasNegativeZ && hasNegativeY && hasSmallPositiveZRot && hasSignificantYTranslation;
        }

        private bool ProcessScale(string line, RWXParseContext context)
        {
            if (!TryParseScaleValues(line.AsSpan(), out Vector3 scaleVector))
                return false;

            Matrix4x4 scale = Matrix4x4.Scale(scaleVector);
            context.currentTransform = context.currentTransform * scale;
            return true;
        }

        private bool ProcessIdentity(string line, RWXParseContext context)
        {
            if (!identityRegex.IsMatch(line)) return false;

            context.currentTransform = Matrix4x4.identity;
            return true;
        }

        private bool ProcessTransform(string line, RWXParseContext context)
        {
            var match = transformRegex.Match(line);
            if (!match.Success) return false;

            var floatMatches = floatRegex.Matches(match.Groups[2].Value);
            if (floatMatches.Count < 16) return false;

            // Parse the 16 values from the RWX transform command
            float[] values = new float[16];
            for (int i = 0; i < 16; i++)
            {
                values[i] = float.Parse(floatMatches[i].Value, CultureInfo.InvariantCulture);
            }

            // RWX matrices are written in column-major order (RenderWare style):
            //   m00 m10 m20 m30 m01 m11 m21 m31 m02 m12 m22 m32 m03 m13 m23 m33
            // with translation in the final column (indices 12–14). Map them into Unity's
            // row/column fields explicitly so translation is preserved.
            Matrix4x4 matrix = new Matrix4x4();

            matrix.m00 = values[0];  matrix.m10 = values[1];  matrix.m20 = values[2];  matrix.m30 = values[3];
            matrix.m01 = values[4];  matrix.m11 = values[5];  matrix.m21 = values[6];  matrix.m31 = values[7];
            matrix.m02 = values[8];  matrix.m12 = values[9];  matrix.m22 = values[10]; matrix.m32 = values[11];
            matrix.m03 = values[12]; matrix.m13 = values[13]; matrix.m23 = values[14]; matrix.m33 = values[15];
            
            // HACK: Some RWX files have m33=0 in their matrices, which is invalid for TRS.
            // Force it to 1 to treat it as an affine transformation.
            if (matrix.m33 == 0)
            {
                matrix.m33 = 1.0f;
                Debug.LogWarning("Matrix with m33=0 found, forcing to 1. This might be a projective matrix.");
            }
            
            // HOTEP.RWX FIX: Check for the specific problematic matrix pattern in hotep.rwx
            // The large negative value (-1.934346) in position 11 (m32) causes ValidTRS errors
            if (Mathf.Abs(matrix.m32) > 1.0f)
            {
                Debug.LogWarning($"Large m32 value detected: {matrix.m32:F6}. This may cause ValidTRS errors. Setting to 0.");
                matrix.m32 = 0.0f;
            }

            // BED DEBUG: Only log matrix transforms for bed-related prototypes
            bool isInPrototype = prototypeParser.IsInPrototype;
            bool isBedRelated = IsPotentialBedPrototype(values);
            
            if (isInPrototype && isBedRelated)
            {
                Vector3 translation = new Vector3(matrix.m03, matrix.m13, matrix.m23);
                float det = matrix.m00 * (matrix.m11 * matrix.m22 - matrix.m12 * matrix.m21) -
                           matrix.m01 * (matrix.m10 * matrix.m22 - matrix.m12 * matrix.m20) +
                           matrix.m02 * (matrix.m10 * matrix.m21 - matrix.m11 * matrix.m20);
                
                string hierarchyPath = GetHierarchyPath(context);
                Debug.Log($"🛏️ BED MATRIX | {hierarchyPath}");
                Debug.Log($"   RWX: [{values[0]:F3}, {values[1]:F3}, {values[2]:F3}, {values[3]:F3}, {values[4]:F3}, {values[5]:F3}, {values[6]:F3}, {values[7]:F3}, {values[8]:F3}, {values[9]:F3}, {values[10]:F3}, {values[11]:F3}, {values[12]:F3}, {values[13]:F3}, {values[14]:F3}, {values[15]:F3}]");
                Debug.Log($"   Unity: Det={det:F3}, Trans=({translation.x:F3}, {translation.y:F3}, {translation.z:F3})");
            }

            // Check if we're inside a prototype definition
            if (prototypeParser.IsInPrototype)
            {
                // For prototype transforms, we need to apply this to the current context transform
                // This will affect all vertices and geometry defined within this prototype
                context.currentTransform = matrix;
            }
            else
            {
                context.currentTransform = matrix;
            }
            
            return true;
        }

        private bool ProcessJointTransformBegin(string line, RWXParseContext context)
        {
            if (!jointtransformBeginRegex.IsMatch(line)) return false;

            // Push current joint transform to stack
            context.jointTransformStack.Push(context.currentJointTransform);
            
            Debug.Log($"� JOINT TRANSFORM BEGIN - Stack depth: {context.jointTransformStack.Count}");
            Debug.Log($"   📋 Pushed current joint transform to stack");
            
            return true;
        }

        private bool ProcessJointTransformEnd(string line, RWXParseContext context)
        {
            if (!jointtransformEndRegex.IsMatch(line)) return false;

            if (context.jointTransformStack.Count > 0)
            {
                context.currentJointTransform = context.jointTransformStack.Pop();
                Debug.Log($"� JOINT TRANSFORM END - Restored from stack, depth: {context.jointTransformStack.Count}");
            }
            else
            {
                context.currentJointTransform = Matrix4x4.identity;
                Debug.Log($"🔗 JOINT TRANSFORM END - Reset to identity (stack empty)");
            }
            return true;
        }

        private bool ProcessIdentityJoint(string line, RWXParseContext context)
        {
            if (!identityJointRegex.IsMatch(line)) return false;

            context.currentJointTransform = Matrix4x4.identity;
            Debug.Log($"🔗 IDENTITY JOINT - Reset joint transform to identity");
            return true;
        }

        private bool ProcessRotateJointTM(string line, RWXParseContext context)
        {
            var match = rotateJointTMRegex.Match(line);
            if (!match.Success) return false;

            var floatMatches = floatRegex.Matches(match.Groups[2].Value);
            if (floatMatches.Count < 4) return false;

            float x = float.Parse(floatMatches[0].Value, CultureInfo.InvariantCulture);
            float y = float.Parse(floatMatches[1].Value, CultureInfo.InvariantCulture);
            float z = float.Parse(floatMatches[2].Value, CultureInfo.InvariantCulture);
            float angle = float.Parse(floatMatches[3].Value, CultureInfo.InvariantCulture);

            // Create rotation around the specified axis
            Vector3 axis = new Vector3(x, y, z).normalized;
            Quaternion rotation = Quaternion.AngleAxis(angle, axis);
            Matrix4x4 rotationMatrix = Matrix4x4.Rotate(rotation);
            
            context.currentJointTransform = context.currentJointTransform * rotationMatrix;
            
            Debug.Log($"🔗 ROTATE JOINT TM - Axis: ({x:F3}, {y:F3}, {z:F3}), Angle: {angle:F1}°");

            return true;
        }

        private static string StripComments(string line)
        {
            int commentIndex = line.IndexOf('#');
            if (commentIndex < 0)
            {
                return line;
            }

            // Respect escaped hash (#!) used in some RWX variants
            if (commentIndex + 1 < line.Length && line[commentIndex + 1] == '!')
            {
                return line;
            }

            return line.Substring(0, commentIndex).TrimEnd();
        }

        private static void SkipWhitespace(ReadOnlySpan<char> line, ref int index)
        {
            while (index < line.Length && char.IsWhiteSpace(line[index]))
            {
                index++;
            }
        }

        private static bool TryReadToken(ReadOnlySpan<char> line, ref int index, out ReadOnlySpan<char> token)
        {
            SkipWhitespace(line, ref index);

            if (index >= line.Length)
            {
                token = default;
                return false;
            }

            int start = index;
            while (index < line.Length && !char.IsWhiteSpace(line[index]))
            {
                index++;
            }

            token = line.Slice(start, index - start);
            return token.Length > 0;
        }

        private static bool TryReadFloat(ReadOnlySpan<char> line, ref int index, out float value)
        {
            if (!TryReadToken(line, ref index, out var token))
            {
                value = 0;
                return false;
            }

            return float.TryParse(token, NumberStyles.Float, CultureInfo.InvariantCulture, out value);
        }

        private static bool TryReadInt(ReadOnlySpan<char> line, ref int index, out int value)
        {
            if (!TryReadToken(line, ref index, out var token))
            {
                value = 0;
                return false;
            }

            return int.TryParse(token, NumberStyles.Integer, CultureInfo.InvariantCulture, out value);
        }

        private static bool TryReadTag(ReadOnlySpan<char> line, ref int index, out int tag)
        {
            int savedIndex = index;
            tag = 0;

            if (TryReadToken(line, ref index, out var token) &&
                token.Equals("tag", StringComparison.OrdinalIgnoreCase))
            {
                if (TryReadInt(line, ref index, out int parsedTag))
                {
                    tag = parsedTag;
                    return true;
                }
            }

            index = savedIndex;
            return false;
        }

        private static void ApplyGeometryTag(RWXParseContext context, int? tagValue, Action geometryAction)
        {
            int previousTag = context.currentMaterial.tag;
            int appliedTag = tagValue ?? previousTag;
            context.currentMaterial.tag = appliedTag;

            geometryAction?.Invoke();

            context.currentMaterial.tag = previousTag;
        }

        private static bool TryExtractTagFromLine(string line, out int tagValue)
        {
            var tagMatch = Regex.Match(line, @"\btag\s+([0-9]+)", DefaultRegexOptions);
            if (tagMatch.Success && int.TryParse(tagMatch.Groups[1].Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out int parsedTag))
            {
                tagValue = parsedTag;
                return true;
            }

            tagValue = 0;
            return false;
        }

        private static bool IsCommand(ReadOnlySpan<char> line, string command, string alternate, out int index)
        {
            index = 0;
            if (!TryReadToken(line, ref index, out var token))
            {
                return false;
            }

            if (token.Equals(command, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }

            if (!string.IsNullOrEmpty(alternate) && token.Equals(alternate, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }

            return false;
        }

        private bool TryProcessVertexFast(ReadOnlySpan<char> line, RWXParseContext context)
        {
            if (!IsCommand(line, "vertex", "vertexext", out int index))
                return false;

            if (!TryReadFloat(line, ref index, out float x) ||
                !TryReadFloat(line, ref index, out float y) ||
                !TryReadFloat(line, ref index, out float z))
            {
                return false;
            }

            Vector3 position = new Vector3(x, y, z);
            Vector2 uv = Vector2.zero;

            int uvStart = index;
            if (TryReadToken(line, ref index, out var uvToken) && uvToken.Equals("uv", StringComparison.OrdinalIgnoreCase))
            {
                if (TryReadFloat(line, ref index, out float u) && TryReadFloat(line, ref index, out float v))
                {
                    uv = new Vector2(u, 1.0f - v);

                    if (prototypeParser.IsInPrototype && NeedsUVCorrection(context))
                    {
                        uv = new Vector2(uv.x, 1.0f - uv.y);
                        Debug.Log($"🎨 UV CORRECTION: Applied additional UV flip for prototype texture orientation");
                    }
                }
                else
                {
                    // Roll back if UV parsing failed so regex fallback can try
                    index = uvStart;
                }
            }

            if (prototypeParser.IsInPrototype && context.currentTransform != Matrix4x4.identity)
            {
                Vector4 homogeneousPos = new Vector4(position.x, position.y, position.z, 1.0f);
                Vector4 transformedPos = context.currentTransform * homogeneousPos;
                position = new Vector3(transformedPos.x, transformedPos.y, transformedPos.z);
            }

            context.vertices.Add(new RWXVertex(position, uv));
            return true;
        }

        private bool TryProcessTriangleFast(ReadOnlySpan<char> line, RWXParseContext context)
        {
            if (!IsCommand(line, "triangle", "triangleext", out int index))
                return false;

            if (!TryReadInt(line, ref index, out int a) ||
                !TryReadInt(line, ref index, out int b) ||
                !TryReadInt(line, ref index, out int c))
            {
                return false;
            }

            int? tagOverride = null;
            if (TryReadTag(line, ref index, out int parsedTag))
            {
                tagOverride = parsedTag;
            }

            ApplyGeometryTag(context, tagOverride, () =>
            {
                meshBuilder.CreateTriangle(context, a - 1, b - 1, c - 1);
            });
            return true;
        }

        private bool TryProcessQuadFast(ReadOnlySpan<char> line, RWXParseContext context)
        {
            if (!IsCommand(line, "quad", "quadext", out int index))
                return false;

            if (!TryReadInt(line, ref index, out int a) ||
                !TryReadInt(line, ref index, out int b) ||
                !TryReadInt(line, ref index, out int c) ||
                !TryReadInt(line, ref index, out int d))
            {
                return false;
            }

            int? tagOverride = null;
            if (TryReadTag(line, ref index, out int parsedTag))
            {
                tagOverride = parsedTag;
            }

            ApplyGeometryTag(context, tagOverride, () =>
            {
                meshBuilder.CreateQuad(context, a - 1, b - 1, c - 1, d - 1);
            });
            return true;
        }

        private bool TryProcessPolygonFast(ReadOnlySpan<char> line, RWXParseContext context)
        {
            if (!IsCommand(line, "polygon", "polygonext", out int index))
                return false;

            if (!TryReadInt(line, ref index, out int count) || count <= 0)
                return false;

            var indices = new List<int>(count);

            for (int i = 0; i < count; i++)
            {
                if (!TryReadInt(line, ref index, out int value))
                {
                    return false;
                }

                indices.Add(value - 1);
            }

            int? tagOverride = null;
            if (TryReadTag(line, ref index, out int parsedTag))
            {
                tagOverride = parsedTag;
            }

            ApplyGeometryTag(context, tagOverride, () =>
            {
                meshBuilder.CreatePolygon(context, indices);
            });
            return true;
        }

        private bool TryProcessTextureFast(ReadOnlySpan<char> line, RWXParseContext context)
        {
            if (!IsCommand(line, "texture", null, out int index))
                return false;

            if (!TryReadToken(line, ref index, out var textureToken))
                return false;

            string textureName = textureToken.ToString().ToLowerInvariant();

            context.currentMaterial.texture = textureName == "null" ? null : textureName;
            context.currentMaterial.mask = null;
            context.currentMaterial.normalMap = null;
            context.currentMaterial.specularMap = null;

            while (TryReadToken(line, ref index, out var keyToken))
            {
                if (!TryReadToken(line, ref index, out var valueToken))
                {
                    break;
                }

                string key = keyToken.ToString().ToLowerInvariant();
                string value = valueToken.ToString();

                switch (key)
                {
                    case "mask":
                        context.currentMaterial.mask = value;
                        break;
                    case "normal":
                        context.currentMaterial.normalMap = value;
                        break;
                    case "specular":
                        context.currentMaterial.specularMap = value;
                        break;
                }
            }

            return true;
        }

        private bool TryProcessColorFast(ReadOnlySpan<char> line, RWXParseContext context)
        {
            if (!IsCommand(line, "color", null, out int index))
                return false;

            if (!TryReadFloat(line, ref index, out float r) ||
                !TryReadFloat(line, ref index, out float g) ||
                !TryReadFloat(line, ref index, out float b))
            {
                return false;
            }

            context.currentMaterial.color = new Color(r, g, b, context.currentMaterial.color.a);
            return true;
        }

        private bool TryProcessOpacityFast(ReadOnlySpan<char> line, RWXParseContext context)
        {
            if (!IsCommand(line, "opacity", null, out int index))
                return false;

            if (!TryReadFloat(line, ref index, out float opacity))
                return false;

            context.currentMaterial.opacity = opacity;
            context.currentMaterial.color = new Color(
                context.currentMaterial.color.r,
                context.currentMaterial.color.g,
                context.currentMaterial.color.b,
                opacity
            );
            return true;
        }

        private bool TryProcessSurfaceFast(ReadOnlySpan<char> line, RWXParseContext context)
        {
            if (!IsCommand(line, "surface", null, out int index))
                return false;

            if (!TryReadFloat(line, ref index, out float ambient) ||
                !TryReadFloat(line, ref index, out float diffuse) ||
                !TryReadFloat(line, ref index, out float specular))
            {
                return false;
            }

            context.currentMaterial.surface = new Vector3(ambient, diffuse, specular);
            return true;
        }

        private bool TryProcessAmbientFast(ReadOnlySpan<char> line, RWXParseContext context)
        {
            if (!IsCommand(line, "ambient", null, out int index))
                return false;

            if (!TryReadFloat(line, ref index, out float ambient))
                return false;

            context.currentMaterial.surface = new Vector3(ambient, context.currentMaterial.surface.y, context.currentMaterial.surface.z);
            return true;
        }

        private bool TryProcessDiffuseFast(ReadOnlySpan<char> line, RWXParseContext context)
        {
            if (!IsCommand(line, "diffuse", null, out int index))
                return false;

            if (!TryReadFloat(line, ref index, out float diffuse))
                return false;

            context.currentMaterial.surface = new Vector3(context.currentMaterial.surface.x, diffuse, context.currentMaterial.surface.z);
            return true;
        }

        private bool TryProcessSpecularFast(ReadOnlySpan<char> line, RWXParseContext context)
        {
            if (!IsCommand(line, "specular", null, out int index))
                return false;

            if (!TryReadFloat(line, ref index, out float specular))
                return false;

            context.currentMaterial.surface = new Vector3(context.currentMaterial.surface.x, context.currentMaterial.surface.y, specular);
            return true;
        }

        private bool TryParseTranslateValues(ReadOnlySpan<char> line, out Vector3 translation)
        {
            translation = Vector3.zero;

            if (IsCommand(line, "translate", null, out int index))
            {
                if (TryReadFloat(line, ref index, out float x) &&
                    TryReadFloat(line, ref index, out float y) &&
                    TryReadFloat(line, ref index, out float z))
                {
                    translation = new Vector3(x, y, z);
                    return true;
                }
            }

            var match = translateRegex.Match(line.ToString());
            if (!match.Success) return false;

            var floatMatches = floatRegex.Matches(match.Groups[2].Value);
            if (floatMatches.Count < 3) return false;

            translation = new Vector3(
                float.Parse(floatMatches[0].Value, CultureInfo.InvariantCulture),
                float.Parse(floatMatches[1].Value, CultureInfo.InvariantCulture),
                float.Parse(floatMatches[2].Value, CultureInfo.InvariantCulture));

            return true;
        }

        private bool TryParseScaleValues(ReadOnlySpan<char> line, out Vector3 scale)
        {
            scale = Vector3.one;

            if (IsCommand(line, "scale", null, out int index))
            {
                if (TryReadFloat(line, ref index, out float x) &&
                    TryReadFloat(line, ref index, out float y) &&
                    TryReadFloat(line, ref index, out float z))
                {
                    scale = new Vector3(x, y, z);
                    return true;
                }
            }

            var match = scaleRegex.Match(line.ToString());
            if (!match.Success) return false;

            var floatMatches = floatRegex.Matches(match.Groups[2].Value);
            if (floatMatches.Count < 3) return false;

            scale = new Vector3(
                float.Parse(floatMatches[0].Value, CultureInfo.InvariantCulture),
                float.Parse(floatMatches[1].Value, CultureInfo.InvariantCulture),
                float.Parse(floatMatches[2].Value, CultureInfo.InvariantCulture));

            return true;
        }

        private bool TryParseRotateValues(ReadOnlySpan<char> line, out float axisX, out float axisY, out float axisZ, out float angleDegrees)
        {
            axisX = axisY = axisZ = angleDegrees = 0f;

            if (IsCommand(line, "rotate", null, out int index))
            {
                if (TryReadFloat(line, ref index, out axisX) &&
                    TryReadFloat(line, ref index, out axisY) &&
                    TryReadFloat(line, ref index, out axisZ) &&
                    TryReadFloat(line, ref index, out angleDegrees))
                {
                    return true;
                }
            }

            var match = rotateRegex.Match(line.ToString());
            if (!match.Success) return false;

            var floatMatches = floatRegex.Matches(match.Groups[2].Value);
            if (floatMatches.Count < 4) return false;

            axisX = float.Parse(floatMatches[0].Value, CultureInfo.InvariantCulture);
            axisY = float.Parse(floatMatches[1].Value, CultureInfo.InvariantCulture);
            axisZ = float.Parse(floatMatches[2].Value, CultureInfo.InvariantCulture);
            angleDegrees = float.Parse(floatMatches[3].Value, CultureInfo.InvariantCulture);

            return true;
        }
    }
}
