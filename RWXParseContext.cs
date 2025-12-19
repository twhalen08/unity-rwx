using System.Collections.Generic;
using UnityEngine;

namespace RWXLoader
{
    public class RWXParseContext
    {
        public GameObject rootObject;
        public GameObject currentObject;
        public RWXMaterial currentMaterial;
        public List<RWXVertex> vertices;
        public List<int> currentTriangles;
        public Matrix4x4 currentTransform;
        public Matrix4x4 currentJointTransform;
        public Stack<RWXMaterial> materialStack;
        public Stack<Matrix4x4> transformStack;
        public Stack<Matrix4x4> clumpTransformStack;
        public Stack<Matrix4x4> jointTransformStack;
        public Stack<GameObject> objectStack;
        public RWXMaterial currentMeshMaterial;
        public int meshCount = 0;

        public RWXParseContext()
        {
            vertices = new List<RWXVertex>();
            currentTriangles = new List<int>();
            currentTransform = Matrix4x4.identity;
            currentJointTransform = Matrix4x4.identity;
            materialStack = new Stack<RWXMaterial>();
            transformStack = new Stack<Matrix4x4>();
            clumpTransformStack = new Stack<Matrix4x4>();
            jointTransformStack = new Stack<Matrix4x4>();
            objectStack = new Stack<GameObject>();
            
            // Initialize with a proper default material instead of null
            currentMaterial = new RWXMaterial();
            // Set default material to white instead of black
            currentMaterial.color = Color.white;
            currentMaterial.opacity = 1.0f;
        }
    }
}
