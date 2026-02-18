using System;
using System.Collections.Generic;
using UnityEngine;

namespace RWXLoader
{
    public enum RWXCommandType
    {
        Unknown,
        Face,
        Vertex,
        Triangle,
        Quad,
        Polygon,
        Texture,
        Material,
        Transform,
        TransformStack,
        JointTransform
    }

    public enum RWXTransformStackAction
    {
        None,
        ClumpBegin,
        ClumpEnd,
        TransformBegin,
        TransformEnd,
        JointTransformBegin,
        JointTransformEnd,
        Identity,
        IdentityJoint
    }

    public sealed class RWXIntermediateCommand
    {
        public RWXCommandType Type { get; }
        public string Keyword { get; }
        public string RawLine { get; }
        public RWXVertexCommand Vertex { get; }
        public RWXFaceCommand Face { get; }
        public RWXMaterialDirective Material { get; }
        public RWXTransformCommand Transform { get; }

        public RWXIntermediateCommand(
            RWXCommandType type,
            string keyword,
            string rawLine,
            RWXVertexCommand vertex = null,
            RWXFaceCommand face = null,
            RWXMaterialDirective material = null,
            RWXTransformCommand transform = null)
        {
            Type = type;
            Keyword = keyword;
            RawLine = rawLine;
            Vertex = vertex;
            Face = face;
            Material = material;
            Transform = transform;
        }
    }

    public sealed class RWXVertexCommand
    {
        public Vector3 Position { get; }
        public Vector2? Uv { get; }

        public RWXVertexCommand(Vector3 position, Vector2? uv = null)
        {
            Position = position;
            Uv = uv;
        }
    }

    public sealed class RWXFaceCommand
    {
        public IReadOnlyList<int> Indices { get; }
        public int? Tag { get; }

        public RWXFaceCommand(IReadOnlyList<int> indices, int? tag = null)
        {
            Indices = indices;
            Tag = tag;
        }
    }

    public sealed class RWXMaterialDirective
    {
        public string Texture { get; }
        public Color? Color { get; }
        public float? Opacity { get; }

        public RWXMaterialDirective(string texture = null, Color? color = null, float? opacity = null)
        {
            Texture = texture;
            Color = color;
            Opacity = opacity;
        }
    }

    public sealed class RWXTransformCommand
    {
        public RWXTransformStackAction StackAction { get; }
        public Matrix4x4? Matrix { get; }
        public Vector3? Translation { get; }
        public Vector3? Scale { get; }
        public Vector4? AxisAngle { get; }

        public RWXTransformCommand(
            RWXTransformStackAction stackAction = RWXTransformStackAction.None,
            Matrix4x4? matrix = null,
            Vector3? translation = null,
            Vector3? scale = null,
            Vector4? axisAngle = null)
        {
            StackAction = stackAction;
            Matrix = matrix;
            Translation = translation;
            Scale = scale;
            AxisAngle = axisAngle;
        }
    }
}
