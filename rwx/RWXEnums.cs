using UnityEngine;

namespace RWXLoader
{
    public enum LightSampling
    {
        Facet = 1,
        Vertex = 2
    }

    public enum GeometrySampling
    {
        PointCloud = 1,
        Wireframe = 2,
        Solid = 3
    }

    public enum TextureMode
    {
        Lit = 1,
        Foreshorten = 2,
        Filter = 3
    }

    public enum MaterialMode
    {
        None = 0,
        Null = 1,
        Double = 2
    }

    public enum TextureAddressMode
    {
        Wrap = 0,
        Mirror = 1,
        Clamp = 2
    }
}
