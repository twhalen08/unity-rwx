using UnityEngine;

namespace RWXLoader
{
    public class RWXVertex
    {
        public Vector3 position;
        public Vector2 uv;

        public RWXVertex(Vector3 pos, Vector2 uvCoord)
        {
            position = pos;
            uv = uvCoord;
        }
    }
}
