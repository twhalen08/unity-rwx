using System;
using System.Collections.Generic;
using System.Globalization;

namespace RWXLoader
{
    /// <summary>Allocation-light, single-pass RWX tokenizer. Safe to run off the Unity thread.</summary>
    public sealed class RWXIntermediateParser
    {
        public RWXParsedModel ParseContent(string content)
        {
            int lineEstimate = 1;
            for (int i = 0; i < (content?.Length ?? 0); i++) if (content[i] == '\n') lineEstimate++;
            var model = new RWXParsedModel(lineEstimate);
            if (string.IsNullOrEmpty(content)) return model;

            var clumps = new Stack<int>();
            int start = 0;
            for (int i = 0; i <= content.Length; i++)
            {
                if (i != content.Length && content[i] != '\n' && content[i] != '\r') continue;
                if (i > start) ParseLineSpan(content, start, i, model, clumps);
                if (i < content.Length && content[i] == '\r' && i + 1 < content.Length && content[i + 1] == '\n') i++;
                start = i + 1;
            }
            return model;
        }

        public RWXParsedCommand? ParseLine(string line)
        {
            var model = new RWXParsedModel(1);
            ParseLineSpan(line ?? string.Empty, 0, line?.Length ?? 0, model, new Stack<int>());
            return model.Commands.Count == 0 ? (RWXParsedCommand?)null : model.Commands[0];
        }

        private static void ParseLineSpan(string source, int begin, int end, RWXParsedModel model, Stack<int> clumps)
        {
            while (begin < end && char.IsWhiteSpace(source[begin])) begin++;
            int hash = begin;
            while (hash < end && source[hash] != '#') hash++;
            if (hash < end && !(hash + 1 < end && source[hash + 1] == '!')) end = hash;
            while (end > begin && char.IsWhiteSpace(source[end - 1])) end--;
            if (begin >= end) return;

            int tokenEnd = begin;
            while (tokenEnd < end && !char.IsWhiteSpace(source[tokenEnd])) tokenEnd++;
            string keyword = source.Substring(begin, tokenEnd - begin);
            string lower = keyword.ToLowerInvariant();
            string line = source.Substring(begin, end - begin); // retained only for the compatibility builder
            var values = new List<string>(8);
            int p = tokenEnd;
            while (p < end)
            {
                while (p < end && char.IsWhiteSpace(source[p])) p++;
                int s = p;
                while (p < end && !char.IsWhiteSpace(source[p])) p++;
                if (p > s) values.Add(source.Substring(s, p - s));
            }

            var c = new RWXParsedCommand { Keyword = keyword, SourceLine = line, Type = RWXCommandType.Unknown };
            if (lower == "vertex" || lower == "vertexext")
            {
                c.Type = RWXCommandType.Vertex;
                if (TryFloat(values, 0, out float x) && TryFloat(values, 1, out float y) && TryFloat(values, 2, out float z))
                { c.Position = new RwxFloat3(x, y, z); c.EstimatedVertices = 1; model.VertexCount++; }
                int uv = IndexOf(values, "uv");
                if (uv >= 0 && TryFloat(values, uv + 1, out float u) && TryFloat(values, uv + 2, out float v)) { c.Uv = new RwxFloat2(u, v); c.HasUv = true; }
            }
            else if (lower == "triangle" || lower == "triangleext" || lower == "quad" || lower == "quadext" || lower == "polygon" || lower == "polygonext")
            {
                c.Type = RWXCommandType.Face;
                int first = lower.StartsWith("polygon", StringComparison.Ordinal) ? 1 : 0;
                int tagAt = IndexOf(values, "tag");
                int count = (tagAt < 0 ? values.Count : tagAt) - first;
                c.Indices = new int[Math.Max(0, count)];
                for (int n = 0; n < c.Indices.Length; n++) int.TryParse(values[first + n], NumberStyles.Integer, CultureInfo.InvariantCulture, out c.Indices[n]);
                c.EstimatedTriangles = Math.Max(0, c.Indices.Length - 2); model.TriangleCount += c.EstimatedTriangles;
                if (tagAt >= 0 && TryInt(values, tagAt + 1, out c.Tag)) c.HasTag = true;
            }
            else if (lower == "texture" || lower == "color" || lower == "opacity" || lower == "surface" || lower == "ambient" || lower == "diffuse" || lower == "specular" || lower.Contains("materialmode"))
            {
                c.Type = RWXCommandType.Material;
                if (lower == "texture" && values.Count > 0) c.Material.Texture = values[0];
                if (lower == "color" && TryFloat(values, 0, out float r) && TryFloat(values, 1, out float g) && TryFloat(values, 2, out float b)) { c.Material.Color = new RwxFloat3(r, g, b); c.Material.HasColor = true; }
                if (lower == "opacity" && TryFloat(values, 0, out float a)) { c.Material.Opacity = a; c.Material.HasOpacity = true; }
            }
            else if (TryStack(lower, out var action))
            {
                c.Type = lower.StartsWith("joint", StringComparison.Ordinal) ? RWXCommandType.JointTransform : RWXCommandType.TransformStack;
                c.Transform.StackAction = action;
            }
            else if (lower == "translate" || lower == "scale" || lower == "rotate" || lower == "rotatejointtm" || lower == "transform")
            {
                c.Type = RWXCommandType.Transform;
                if ((lower == "translate" || lower == "scale") && TryFloat(values, 0, out float x) && TryFloat(values, 1, out float y) && TryFloat(values, 2, out float z))
                {
                    if (lower == "translate") { c.Transform.Translation = new RwxFloat3(x, y, z); c.Transform.ValueKind = 1; }
                    else { c.Transform.Scale = new RwxFloat3(x, y, z); c.Transform.ValueKind = 2; }
                }
                else if ((lower == "rotate" || lower == "rotatejointtm") && TryFloat(values, 0, out float ax) && TryFloat(values, 1, out float ay) && TryFloat(values, 2, out float az) && TryFloat(values, 3, out float angle))
                { c.Transform.AxisAngle = new RwxFloat4(ax, ay, az, angle); c.Transform.ValueKind = 3; }
                else if (lower == "transform" && values.Count >= 16)
                {
                    c.Transform.Matrix = new float[16];
                    bool valid = true;
                    for (int n = 0; n < 16; n++) valid &= TryFloat(values, n, out c.Transform.Matrix[n]);
                    if (valid) c.Transform.ValueKind = 4;
                }
            }

            int commandIndex = model.Commands.Count;
            model.Commands.Add(c);
            if (lower == "clumpbegin") clumps.Push(commandIndex);
            else if (lower == "clumpend" && clumps.Count > 0) model.Clumps.Add(new RWXClumpDescriptor { BeginCommand = clumps.Pop(), EndCommand = commandIndex });
            else if ((lower == "prototypedef" || lower == "prototype") && values.Count > 0) model.PrototypeReferences.Add(new RWXPrototypeReference { Name = values[0], CommandIndex = commandIndex });
        }

        private static bool TryStack(string s, out RWXTransformStackAction a)
        {
            switch (s) { case "clumpbegin": a = RWXTransformStackAction.ClumpBegin; return true; case "clumpend": a = RWXTransformStackAction.ClumpEnd; return true; case "transformbegin": a = RWXTransformStackAction.TransformBegin; return true; case "transformend": a = RWXTransformStackAction.TransformEnd; return true; case "jointtransformbegin": a = RWXTransformStackAction.JointTransformBegin; return true; case "jointtransformend": a = RWXTransformStackAction.JointTransformEnd; return true; case "identity": a = RWXTransformStackAction.Identity; return true; case "identityjoint": a = RWXTransformStackAction.IdentityJoint; return true; default: a = RWXTransformStackAction.None; return false; }
        }
        private static int IndexOf(List<string> v, string token) { for (int i = 0; i < v.Count; i++) if (string.Equals(v[i], token, StringComparison.OrdinalIgnoreCase)) return i; return -1; }
        private static bool TryFloat(List<string> v, int i, out float value) { value = 0; return i >= 0 && i < v.Count && float.TryParse(v[i], NumberStyles.Float, CultureInfo.InvariantCulture, out value); }
        private static bool TryInt(List<string> v, int i, out int value) { value = 0; return i >= 0 && i < v.Count && int.TryParse(v[i], NumberStyles.Integer, CultureInfo.InvariantCulture, out value); }
    }
}
