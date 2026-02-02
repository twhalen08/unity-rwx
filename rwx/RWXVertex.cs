using UnityEngine;

namespace RWXLoader
{
    public class RWXVertex
    {
        public UnityEngine.Vector3 position;
        public Vector2 uv;

        public RWXVertex(UnityEngine.Vector3 pos, Vector2 uvCoord)
        {
            position = pos;
            uv = uvCoord;
        }
    }
}
