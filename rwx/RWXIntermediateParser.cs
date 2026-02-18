using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text.RegularExpressions;
using UnityEngine;

namespace RWXLoader
{
    public class RWXIntermediateParser
    {
        private static readonly Regex FloatRegex = new Regex(@"([+-]?([0-9]+([.][0-9]*)?|[.][0-9]+)([eE][-+][0-9]+)?)", RegexOptions.IgnoreCase | RegexOptions.Compiled);
        private static readonly Regex IntegerRegex = new Regex(@"([-+]?[0-9]+)", RegexOptions.IgnoreCase | RegexOptions.Compiled);

        public RWXIntermediateCommand ParseLine(string line)
        {
            if (string.IsNullOrWhiteSpace(line))
            {
                return null;
            }

            string stripped = StripComments(line).Trim();
            if (string.IsNullOrWhiteSpace(stripped))
            {
                return null;
            }

            string keyword = ExtractCommandToken(stripped);
            if (string.IsNullOrEmpty(keyword))
            {
                return new RWXIntermediateCommand(RWXCommandType.Unknown, string.Empty, stripped);
            }

            return keyword.ToLowerInvariant() switch
            {
                "vertex" or "vertexext" => ParseVertex(keyword, stripped),
                "triangle" or "triangleext" or "quad" or "quadext" or "polygon" or "polygonext" => ParseFace(keyword, stripped),
                "texture" or "color" or "opacity" or "surface" or "ambient" or "diffuse" or "specular" or "materialmode" or "addmaterialmode" or "materialmodes" => new RWXIntermediateCommand(RWXCommandType.Material, keyword, stripped, material: ParseMaterial(keyword, stripped)),
                "clumpbegin" => new RWXIntermediateCommand(RWXCommandType.TransformStack, keyword, stripped, transform: new RWXTransformCommand(RWXTransformStackAction.ClumpBegin)),
                "clumpend" => new RWXIntermediateCommand(RWXCommandType.TransformStack, keyword, stripped, transform: new RWXTransformCommand(RWXTransformStackAction.ClumpEnd)),
                "transformbegin" => new RWXIntermediateCommand(RWXCommandType.TransformStack, keyword, stripped, transform: new RWXTransformCommand(RWXTransformStackAction.TransformBegin)),
                "transformend" => new RWXIntermediateCommand(RWXCommandType.TransformStack, keyword, stripped, transform: new RWXTransformCommand(RWXTransformStackAction.TransformEnd)),
                "jointtransformbegin" => new RWXIntermediateCommand(RWXCommandType.JointTransform, keyword, stripped, transform: new RWXTransformCommand(RWXTransformStackAction.JointTransformBegin)),
                "jointtransformend" => new RWXIntermediateCommand(RWXCommandType.JointTransform, keyword, stripped, transform: new RWXTransformCommand(RWXTransformStackAction.JointTransformEnd)),
                "identity" => new RWXIntermediateCommand(RWXCommandType.Transform, keyword, stripped, transform: new RWXTransformCommand(RWXTransformStackAction.Identity)),
                "identityjoint" => new RWXIntermediateCommand(RWXCommandType.JointTransform, keyword, stripped, transform: new RWXTransformCommand(RWXTransformStackAction.IdentityJoint)),
                "translate" or "rotate" or "scale" or "transform" or "rotatejointtm" => new RWXIntermediateCommand(RWXCommandType.Transform, keyword, stripped, transform: ParseTransform(keyword, stripped)),
                _ => new RWXIntermediateCommand(RWXCommandType.Unknown, keyword, stripped)
            };
        }

        public List<RWXIntermediateCommand> ParseContent(string content)
        {
            var commands = new List<RWXIntermediateCommand>();
            if (string.IsNullOrEmpty(content))
            {
                return commands;
            }

            string[] lines = content.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries);
            foreach (var line in lines)
            {
                var command = ParseLine(line);
                if (command != null)
                {
                    commands.Add(command);
                }
            }

            return commands;
        }

        private RWXIntermediateCommand ParseVertex(string keyword, string line)
        {
            var floats = FloatRegex.Matches(line);
            if (floats.Count < 3)
            {
                return new RWXIntermediateCommand(RWXCommandType.Vertex, keyword, line);
            }

            var vertex = new RWXVertexCommand(
                new Vector3(ParseFloat(floats[0].Value), ParseFloat(floats[1].Value), ParseFloat(floats[2].Value)),
                floats.Count >= 5 ? new Vector2(ParseFloat(floats[3].Value), ParseFloat(floats[4].Value)) : null);

            return new RWXIntermediateCommand(RWXCommandType.Vertex, keyword, line, vertex: vertex);
        }

        private RWXIntermediateCommand ParseFace(string keyword, string line)
        {
            var ints = IntegerRegex.Matches(line);
            var indices = new List<int>();
            for (int i = 0; i < ints.Count; i++)
            {
                if (i == 0 && keyword.StartsWith("polygon", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                indices.Add(int.Parse(ints[i].Value, CultureInfo.InvariantCulture));
            }

            return new RWXIntermediateCommand(RWXCommandType.Face, keyword, line, face: new RWXFaceCommand(indices));
        }

        private RWXMaterialDirective ParseMaterial(string keyword, string line)
        {
            var floats = FloatRegex.Matches(line);
            if (keyword.Equals("texture", StringComparison.OrdinalIgnoreCase))
            {
                var parts = line.Split(new[] { ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries);
                return new RWXMaterialDirective(texture: parts.Length > 1 ? parts[1] : null);
            }

            if (keyword.Equals("color", StringComparison.OrdinalIgnoreCase) && floats.Count >= 3)
            {
                return new RWXMaterialDirective(color: new Color(ParseFloat(floats[0].Value), ParseFloat(floats[1].Value), ParseFloat(floats[2].Value)));
            }

            if (keyword.Equals("opacity", StringComparison.OrdinalIgnoreCase) && floats.Count >= 1)
            {
                return new RWXMaterialDirective(opacity: ParseFloat(floats[0].Value));
            }

            return new RWXMaterialDirective();
        }

        private RWXTransformCommand ParseTransform(string keyword, string line)
        {
            var floats = FloatRegex.Matches(line);
            if (keyword.Equals("translate", StringComparison.OrdinalIgnoreCase) && floats.Count >= 3)
            {
                return new RWXTransformCommand(translation: new Vector3(ParseFloat(floats[0].Value), ParseFloat(floats[1].Value), ParseFloat(floats[2].Value)));
            }

            if (keyword.Equals("scale", StringComparison.OrdinalIgnoreCase) && floats.Count >= 3)
            {
                return new RWXTransformCommand(scale: new Vector3(ParseFloat(floats[0].Value), ParseFloat(floats[1].Value), ParseFloat(floats[2].Value)));
            }

            if ((keyword.Equals("rotate", StringComparison.OrdinalIgnoreCase) || keyword.Equals("rotatejointtm", StringComparison.OrdinalIgnoreCase)) && floats.Count >= 4)
            {
                return new RWXTransformCommand(axisAngle: new Vector4(ParseFloat(floats[0].Value), ParseFloat(floats[1].Value), ParseFloat(floats[2].Value), ParseFloat(floats[3].Value)));
            }

            return new RWXTransformCommand();
        }

        private static float ParseFloat(string token) => float.Parse(token, CultureInfo.InvariantCulture);

        private static string ExtractCommandToken(string line)
        {
            int i = 0;
            while (i < line.Length && char.IsWhiteSpace(line[i])) i++;
            int start = i;
            while (i < line.Length && char.IsLetter(line[i])) i++;
            return i > start ? line.Substring(start, i - start) : string.Empty;
        }

        private static string StripComments(string line)
        {
            int commentIndex = line.IndexOf('#');
            if (commentIndex < 0)
            {
                return line;
            }

            if (commentIndex + 1 < line.Length && line[commentIndex + 1] == '!')
            {
                return line;
            }

            return line.Substring(0, commentIndex);
        }
    }
}
