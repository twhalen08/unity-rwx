using System.Collections.Generic;
using UnityEngine;

namespace RWXLoader
{
    [System.Serializable]
    public class RWXMaterial
    {
        public Color color = Color.white;
        public bool hasExplicitColor = false;
        public Vector3 surface = new Vector3(0.69f, 0.0f, 0.0f); // Ambience, Diffusion, Specularity
        public float opacity = 1.0f;
        public LightSampling lightSampling = LightSampling.Facet;
        public GeometrySampling geometrySampling = GeometrySampling.Solid;
        public List<TextureMode> textureModes = new List<TextureMode> { TextureMode.Lit, TextureMode.Foreshorten, TextureMode.Filter };
        public MaterialMode materialMode = MaterialMode.Null;
        public string texture = null;
        public bool tint = false;
        public string mask = null;
        public string normalMap = null;
        public string specularMap = null;
        public TextureAddressMode textureAddressMode = TextureAddressMode.Wrap;
        public bool collision = true;
        public int tag = 0;
        public float ratio = 1.0f;

        public RWXMaterial Clone()
        {
            var cloned = new RWXMaterial();
            cloned.color = color;
            cloned.surface = surface;
            cloned.opacity = opacity;
            cloned.lightSampling = lightSampling;
            cloned.geometrySampling = geometrySampling;
            cloned.textureModes = new List<TextureMode>(textureModes);
            cloned.materialMode = materialMode;
            cloned.texture = texture;
            cloned.tint = tint;
            cloned.mask = mask;
            cloned.normalMap = normalMap;
            cloned.specularMap = specularMap;
            cloned.textureAddressMode = textureAddressMode;
            cloned.collision = collision;
            cloned.tag = tag;
            cloned.ratio = ratio;
            cloned.hasExplicitColor = hasExplicitColor;
            return cloned;
        }

        public Color GetEffectiveColor()
        {
            return hasExplicitColor ? color : Color.white;
        }

        public string GetMaterialSignature()
        {
            var baseColor = GetEffectiveColor();
            var colorStr = $"{baseColor.r:F3}{baseColor.g:F3}{baseColor.b:F3}";
            var surfaceStr = $"{surface.x:F3}{surface.y:F3}{surface.z:F3}";
            var opacityStr = opacity.ToString("F3");
            var lightSamplingStr = ((int)lightSampling).ToString();
            var geometrySamplingStr = ((int)geometrySampling).ToString();
            var textureModeStr = "";
            
            foreach (var tm in textureModes)
            {
                textureModeStr += ((int)tm).ToString();
            }
            
            var materialModeStr = ((int)materialMode).ToString();
            var textureStr = texture ?? "";
            var maskStr = mask ?? "";
            var normalStr = normalMap ?? "";
            var specularStr = specularMap ?? "";
            var textureAddressModeStr = ((int)textureAddressMode).ToString();
            var collisionStr = collision.ToString();
            var tagStr = tag.ToString();
            var ratioStr = ratio.ToString("F2");

            return $"{colorStr}_{surfaceStr}_{opacityStr}_{lightSamplingStr}_{geometrySamplingStr}_{textureModeStr}_{materialModeStr}_{textureStr}_{tint}_{maskStr}_{specularStr}_{normalStr}_{textureAddressModeStr}_{collisionStr}_{tagStr}_{ratioStr}";
        }
    }
}
