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
                Color baseColor = rwxMaterial.GetEffectiveColor();
                Color materialColor = new Color(baseColor.r, baseColor.g, baseColor.b, rwxMaterial.opacity);
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
                Color baseColor = rwxMaterial.GetEffectiveColor();
                material.color = new Color(baseColor.r, baseColor.g, baseColor.b, rwxMaterial.opacity);
                
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
            var cullMode = isDoubleSided ? UnityEngine.Rendering.CullMode.Off : UnityEngine.Rendering.CullMode.Back;
            material.SetInt("_Cull", (int)cullMode);
            material.SetOverrideTag("RwxTag", rwxMaterial.tag.ToString());

            return material;
        }

        private IEnumerator LoadTexturesForMaterial(Material material, RWXMaterial rwxMaterial)
        {
            Texture2D mainTexture = null;
            Texture2D maskTexture = null;
            bool mainTextureLoaded = false;
            bool maskTextureLoaded = false;

            // Load main texture asynchronously to avoid sync decode stalls on the main thread.
            if (!string.IsNullOrEmpty(rwxMaterial.texture))
            {
                yield return textureLoader.LoadTextureFromZipOrRemote(rwxMaterial.texture, false, (texture) => {
                    mainTexture = texture;
                    mainTextureLoaded = true;
                });
            }
            else
            {
                mainTextureLoaded = true; // No texture to load
            }

            // Load mask texture asynchronously (BMP masks can be expensive to decode).
            if (!string.IsNullOrEmpty(rwxMaterial.mask))
            {
                yield return textureLoader.LoadTextureFromZipOrRemote(rwxMaterial.mask, true, (texture) => {
                    maskTexture = texture;
                    maskTextureLoaded = true;
                });
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
                    yield return textureProcessor.ApplyTexturesWithMaskAsync(material, mainTexture, maskTexture, rwxMaterial);
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
