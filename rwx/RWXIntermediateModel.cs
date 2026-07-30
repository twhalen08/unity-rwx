using System;
using System.Collections;
using System.Collections.Generic;

namespace RWXLoader
{
    // This file deliberately has no UnityEngine dependency.  Instances can be
    // produced by a Task/Job and handed to the main thread afterwards.
    public enum RWXCommandType { Unknown, Face, Vertex, Triangle, Quad, Polygon, Texture, Material, Transform, TransformStack, JointTransform }
    public enum RWXTransformStackAction { None, ClumpBegin, ClumpEnd, TransformBegin, TransformEnd, JointTransformBegin, JointTransformEnd, Identity, IdentityJoint }

    public struct RwxFloat2 { public float X, Y; public RwxFloat2(float x, float y) { X = x; Y = y; } }
    public struct RwxFloat3 { public float X, Y, Z; public RwxFloat3(float x, float y, float z) { X = x; Y = y; Z = z; } }
    public struct RwxFloat4 { public float X, Y, Z, W; public RwxFloat4(float x, float y, float z, float w) { X = x; Y = y; Z = z; W = w; } }

    public struct RWXMaterialDescriptor
    {
        public string Texture;
        public RwxFloat3 Color;
        public float Opacity;
        public bool HasColor, HasOpacity;
    }

    public struct RWXTransformDescriptor
    {
        public RWXTransformStackAction StackAction;
        public RwxFloat3 Translation, Scale;
        public RwxFloat4 AxisAngle;
        public float[] Matrix;
        public byte ValueKind; // 1 translation, 2 scale, 3 axis/angle, 4 matrix
    }

    public struct RWXParsedCommand
    {
        public RWXCommandType Type;
        public string Keyword;
        public string SourceLine;
        public RwxFloat3 Position;
        public RwxFloat2 Uv;
        public bool HasUv;
        public int[] Indices;
        public int Tag;
        public bool HasTag;
        public RWXMaterialDescriptor Material;
        public RWXTransformDescriptor Transform;
        public int EstimatedVertices;
        public int EstimatedTriangles;
    }

    public struct RWXClumpDescriptor { public int BeginCommand, EndCommand; }
    public struct RWXPrototypeReference { public string Name; public int CommandIndex; }

    /// <summary>Immutable-by-convention, CLR-only result of archive tokenization.</summary>
    public sealed class RWXParsedModel : IReadOnlyList<RWXParsedCommand>
    {
        public readonly List<RWXParsedCommand> Commands;
        public readonly List<RWXClumpDescriptor> Clumps;
        public readonly List<RWXPrototypeReference> PrototypeReferences;
        public int VertexCount { get; internal set; }
        public int TriangleCount { get; internal set; }

        internal RWXParsedModel(int capacity)
        {
            Commands = new List<RWXParsedCommand>(capacity);
            Clumps = new List<RWXClumpDescriptor>(Math.Max(1, capacity / 32));
            PrototypeReferences = new List<RWXPrototypeReference>(Math.Max(1, capacity / 64));
        }

        public int Count => Commands.Count;
        public RWXParsedCommand this[int index] => Commands[index];
        public IEnumerator<RWXParsedCommand> GetEnumerator() => Commands.GetEnumerator();
        IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
    }
}
