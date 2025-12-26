using System;
using System.Collections;
using System.Collections.Generic;
using UnityEngine;

namespace RWXLoader
{
    /// <summary>
    /// Manages Unity materials for RWX objects, coordinating texture loading and material creation
    /// </summary>
    public class RWXMaterialManager : MonoBehaviour
    {
        [Header("Settings")]
        public bool enableTextures = true;
        public bool useStandardShader = true;
        public float alphaTest = 0.2f;

        [Header("Components")]
        public RWXTextureLoader textureLoader;
        public RWXTextureProcessor textureProcessor;
        public RWXBmpDecoder bmpDecoder;

        private readonly Dictionary<string, Material> materialCache = new Dictionary<string, Material>();
        private Material defaultMaterial;

        private void Start()
        {
            // Get or create required components
            if (textureLoader == null)
                textureLoader = GetComponent<RWXTextureLoader>() ?? gameObject.AddComponent<RWXTextureLoader>();
            
            if (textureProcessor == null)
                textureProcessor = GetComponent<RWXTextureProcessor>() ?? gameObject.AddComponent<RWXTextureProcessor>();
            
            if (bmpDecoder == null)
                bmpDecoder = GetComponent<RWXBmpDecoder>() ?? gameObject.AddComponent<RWXBmpDecoder>();

            // Create default material
            CreateDefaultMaterial();
        }

        /// <summary>
        /// Sets the texture source for remote loading
        /// </summary>
        public void SetTextureSource(string objectPath, string password)
        {
            if (textureLoader != null)
            {
                textureLoader.SetTextureSource(objectPath, password);
            }
        }

        private void CreateDefaultMaterial()
        {
            if (useStandardShader)
            {
                defaultMaterial = new Material(Shader.Find("Standard"));
                defaultMaterial.color = Color.white;
            }
            else
            {
                defaultMaterial = new Material(Shader.Find("Legacy Shaders/Diffuse"));
                defaultMaterial.color = Color.white;
            }
        }

        public Material GetDefaultMaterial()
        {
            if (defaultMaterial == null)
                CreateDefaultMaterial();
            return defaultMaterial;
        }

        public Material GetUnityMaterial(RWXMaterial rwxMaterial)
        {
            string signature = rwxMaterial.GetMaterialSignature();
            
            if (materialCache.TryGetValue(signature, out Material cachedMaterial))
            {
                return cachedMaterial;
            }

            Material unityMaterial = CreateUnityMaterial(rwxMaterial);
            materialCache[signature] = unityMaterial;
            return unityMaterial;
        }

        private Material CreateUnityMaterial(RWXMaterial rwxMaterial)
        {
            Material material;
            bool isDoubleSided = rwxMaterial.materialMode == MaterialMode.Double;

            // For double-sided materials, use Standard shader since we handle double-sided via triangle duplication
            if (useStandardShader)
            {
                material = new Material(Shader.Find("Standard"));
                
                // Set base color with proper alpha
                Color materialColor = new Color(rwxMaterial.color.r, rwxMaterial.color.g, rwxMaterial.color.b, rwxMaterial.opacity);
                material.color = materialColor;
                
                // Set metallic and smoothness based on surface properties
                material.SetFloat("_Metallic", rwxMaterial.surface.z); // Use specular as metallic
                material.SetFloat("_Glossiness", rwxMaterial.surface.z); // Use specular as smoothness
                
                // Handle transparency (including mask-based transparency)
                bool hasMask = !string.IsNullOrEmpty(rwxMaterial.mask);
                if (rwxMaterial.opacity < 1.0f || hasMask)
                {
                    // Set to Transparent mode
                    material.SetFloat("_Mode", 3);
                    material.SetInt("_SrcBlend", (int)UnityEngine.Rendering.BlendMode.SrcAlpha);
                    material.SetInt("_DstBlend", (int)UnityEngine.Rendering.BlendMode.OneMinusSrcAlpha);
                    material.SetInt("_ZWrite", 0);
                    material.DisableKeyword("_ALPHATEST_ON");
                    material.EnableKeyword("_ALPHABLEND_ON");
                    material.DisableKeyword("_ALPHAPREMULTIPLY_ON");
                    material.renderQueue = 3000;
                    
                }
                else
                {
                    // Opaque mode
                    material.SetFloat("_Mode", 0);
                    material.SetInt("_SrcBlend", (int)UnityEngine.Rendering.BlendMode.One);
                    material.SetInt("_DstBlend", (int)UnityEngine.Rendering.BlendMode.Zero);
                    material.SetInt("_ZWrite", 1);
                    material.DisableKeyword("_ALPHATEST_ON");
                    material.DisableKeyword("_ALPHABLEND_ON");
                    material.DisableKeyword("_ALPHAPREMULTIPLY_ON");
                    material.renderQueue = -1;
                }
                
            }
            else
            {
                material = new Material(Shader.Find("Legacy Shaders/Diffuse"));
                material.color = new Color(rwxMaterial.color.r, rwxMaterial.color.g, rwxMaterial.color.b, rwxMaterial.opacity);
                
                // Handle transparency for legacy shader
                bool hasMask = !string.IsNullOrEmpty(rwxMaterial.mask);
                if (rwxMaterial.opacity < 1.0f || hasMask)
                {
                    material.shader = Shader.Find("Legacy Shaders/Transparent/Diffuse");
                }
                
            }

            // Apply texture and mask if available
            if (enableTextures && textureLoader != null)
            {
                StartCoroutine(LoadTexturesForMaterial(material, rwxMaterial));
            }

            // For double-sided materials, we don't need to disable culling since we duplicate triangles
            // Keep normal culling behavior
            material.SetInt("_Cull", (int)UnityEngine.Rendering.CullMode.Back);

            return material;
        }

        private IEnumerator LoadTexturesForMaterial(Material material, RWXMaterial rwxMaterial)
        {
            Texture2D mainTexture = null;
            Texture2D maskTexture = null;
            bool mainTextureLoaded = false;
            bool maskTextureLoaded = false;

            // Load main texture (simplified - no double-sided flag)
            if (!string.IsNullOrEmpty(rwxMaterial.texture))
            {
                
                // Try to load texture synchronously first (for local files)
                mainTexture = textureLoader.LoadTextureSync(rwxMaterial.texture);
                if (mainTexture != null)
                {
                    mainTextureLoaded = true;
                }
                else
                {
                    // Try loading from ZIP first, then fall back to individual download
                    yield return textureLoader.LoadTextureFromZipOrRemote(rwxMaterial.texture, false, (texture) => {
                        mainTexture = texture;
                        mainTextureLoaded = true;
                    });
                }
            }
            else
            {
                mainTextureLoaded = true; // No texture to load
            }

            // Load mask texture (simplified - no double-sided flag)
            if (!string.IsNullOrEmpty(rwxMaterial.mask))
            {
                
                // Try to load mask synchronously first (for local files)
                maskTexture = textureLoader.LoadTextureSync(rwxMaterial.mask);
                if (maskTexture != null)
                {
                    maskTextureLoaded = true;
                }
                else
                {
                    // Try loading from ZIP first, then fall back to individual download
                    yield return textureLoader.LoadTextureFromZipOrRemote(rwxMaterial.mask, true, (texture) => {
                        maskTexture = texture;
                        maskTextureLoaded = true;
                    });
                }
            }
            else
            {
                maskTextureLoaded = true; // No mask to load
            }

            // Wait for both textures to finish loading
            while (!mainTextureLoaded || !maskTextureLoaded)
            {
                yield return null;
            }

            // Apply textures to material
            if (mainTexture != null || maskTexture != null)
            {
                
                if (textureProcessor != null)
                {
                    textureProcessor.ApplyTexturesWithMask(material, mainTexture, maskTexture, rwxMaterial);
                }
                else
                {
                    // Fallback: apply main texture directly
                    if (mainTexture != null)
                    {
                        material.mainTexture = mainTexture;
                        
                        // For Standard shader, also set the albedo texture
                        if (material.shader.name.Contains("Standard"))
                        {
                            material.SetTexture("_MainTex", mainTexture);
                            material.SetTexture("_AlbedoMap", mainTexture);
                        }
                        
                    }
                }
                
                // CRITICAL FIX: Update all MeshRenderers that use this material
                // Unity creates material instances when assigning to renderers, so we need to update those instances
                UpdateMaterialInstances(material, rwxMaterial);
                
                // Verify the texture was applied
            }
        }

        /// <summary>
        /// Updates all MeshRenderer instances that use this exact material with the new texture
        /// This is critical because Unity creates material instances when assigning to renderers
        /// </summary>
        private void UpdateMaterialInstances(Material sourceMaterial, RWXMaterial rwxMaterial)
        {
            // Get the exact material signature to match only the correct materials
            string materialSignature = rwxMaterial.GetMaterialSignature();
            
            // Find all MeshRenderers in the scene that might be using this material
            MeshRenderer[] allRenderers = FindObjectsOfType<MeshRenderer>();
            int updatedRenderers = 0;
            
            foreach (MeshRenderer renderer in allRenderers)
            {
                if (renderer.material != null)
                {
                    // CRITICAL FIX: Only update renderers that match BOTH the texture name AND have the same tag
                    // This prevents cross-contamination between different materials
                    string expectedTextureName = rwxMaterial.texture ?? "default";
                    string rendererName = renderer.gameObject.name;
                    var rwxTag = renderer.GetComponentInParent<RWXTag>();
                    int rendererTag = rwxTag != null ? rwxTag.TagId : 0;
                    string rendererTextureName = rwxTag != null ? (rwxTag.TextureName ?? rendererName) : rendererName;

                    bool nameMatches = rendererTextureName == expectedTextureName;
                    bool tagMatches = rendererTag == rwxMaterial.tag;
                    
                    // Only update if the renderer matches both texture and tag
                    if (nameMatches && tagMatches)
                    {
                        // Update the renderer's material instance with the new texture
                        Material rendererMaterial = renderer.material;
                        
                        // Copy all texture properties from source material to renderer's material instance
                        if (sourceMaterial.mainTexture != null)
                        {
                            rendererMaterial.mainTexture = sourceMaterial.mainTexture;
                            
                            // For Standard shader, also set the albedo texture
                            if (rendererMaterial.shader.name.Contains("Standard"))
                            {
                                rendererMaterial.SetTexture("_MainTex", sourceMaterial.mainTexture);
                                rendererMaterial.SetTexture("_AlbedoMap", sourceMaterial.mainTexture);
                                
                                // CRITICAL: Copy all transparency settings from source material
                                rendererMaterial.SetFloat("_Mode", sourceMaterial.GetFloat("_Mode"));
                                rendererMaterial.SetInt("_SrcBlend", sourceMaterial.GetInt("_SrcBlend"));
                                rendererMaterial.SetInt("_DstBlend", sourceMaterial.GetInt("_DstBlend"));
                                rendererMaterial.SetInt("_ZWrite", sourceMaterial.GetInt("_ZWrite"));
                                rendererMaterial.renderQueue = sourceMaterial.renderQueue;
                                
                                // Copy keywords for transparency
                                if (sourceMaterial.IsKeywordEnabled("_ALPHABLEND_ON"))
                                {
                                    rendererMaterial.EnableKeyword("_ALPHABLEND_ON");
                                    rendererMaterial.DisableKeyword("_ALPHATEST_ON");
                                    rendererMaterial.DisableKeyword("_ALPHAPREMULTIPLY_ON");
                                }
                                else if (sourceMaterial.IsKeywordEnabled("_ALPHATEST_ON"))
                                {
                                    rendererMaterial.EnableKeyword("_ALPHATEST_ON");
                                    rendererMaterial.DisableKeyword("_ALPHABLEND_ON");
                                    rendererMaterial.DisableKeyword("_ALPHAPREMULTIPLY_ON");
                                }
                                else
                                {
                                    rendererMaterial.DisableKeyword("_ALPHATEST_ON");
                                    rendererMaterial.DisableKeyword("_ALPHABLEND_ON");
                                    rendererMaterial.DisableKeyword("_ALPHAPREMULTIPLY_ON");
                                }
                            }
                            
                            // Copy other material properties to ensure consistency
                            rendererMaterial.color = sourceMaterial.color;
                            
                            updatedRenderers++;
                        }
                    }
                }
            }
            
        }

        /// <summary>
        /// Clears all cached materials and textures
        /// </summary>
        public void ClearCache()
        {
            // Clear material cache
            foreach (var material in materialCache.Values)
            {
                if (material != null)
                {
                    UnityEngine.Object.DestroyImmediate(material);
                }
            }
            materialCache.Clear();

            // Clear texture cache
            if (textureLoader != null)
            {
                textureLoader.ClearCache();
            }
        }

        private void OnDestroy()
        {
            ClearCache();
        }
    }
}
