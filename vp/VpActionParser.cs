using System;
using System.Collections.Generic;
using UnityEngine;

public static class VpActionParser
{


    private enum Phase
    {
        None,
        Create,
        Activate
    }

    public static void Parse(string action, out List<VpActionCommand> createActions, out List<VpActionCommand> activateActions)
    {
        createActions = new List<VpActionCommand>();
        activateActions = new List<VpActionCommand>();

        if (string.IsNullOrWhiteSpace(action))
            return;

        Phase current = Phase.None;

        // Split into segments on ; , and newlines, but respect quoted strings.
        foreach (var segRaw in SplitSegments(action))
        {
            // ✅ FIX #1: VP often prefixes commands with commas (",texture ...")
            // Strip whitespace + leading commas so tokens become "texture", not ",texture".
            string seg = segRaw.Trim().TrimStart(',');
            if (seg.Length == 0)
                continue;

            // Tokenize (respects quotes)
            var tokens = Tokenize(seg);
            if (tokens.Count == 0)
                continue;

            // ✅ FIX #1 (extra safety): also strip commas from the first token
            string head = tokens[0].Trim().TrimStart(',').ToLowerInvariant();

            // "create"/"activate" can be a mode switch or an inline command
            if (head == "create" || head == "activate")
            {
                current = (head == "create") ? Phase.Create : Phase.Activate;

                // Standalone "create"/"activate"
                if (tokens.Count == 1)
                    continue;

                // Inline command: remainder is the actual command
                var cmdTokens = tokens.GetRange(1, tokens.Count - 1);
                AddCommand(current, cmdTokens, seg, createActions, activateActions);
                continue;
            }

            // Command inherits current mode
            if (current == Phase.None)
            {
                // If you prefer default-to-create behavior, uncomment:
                // current = Phase.Create;
                continue;
            }

            AddCommand(current, tokens, seg, createActions, activateActions);
        }
    }

    private static void AddCommand(
        Phase phase,
        List<string> tokens,
        string raw,
        List<VpActionCommand> createActions,
        List<VpActionCommand> activateActions)
    {
        if (tokens == null || tokens.Count == 0)
            return;

        var cmd = ParseCommand(tokens);
        cmd.raw = raw;

        if (phase == Phase.Create) createActions.Add(cmd);
        else if (phase == Phase.Activate) activateActions.Add(cmd);
    }

    private static VpActionCommand ParseCommand(List<string> tokens)
    {
        var cmd = new VpActionCommand();

        // ✅ FIX #1: verbs can arrive like ",texture" even after tokenization in some cases
        string first = tokens[0].Trim().TrimStart(',');

        // First token can be "texture" or "texture=wood1"
        if (TrySplitKeyValue(first, out var k0, out var v0))
        {
            cmd.verb = k0.ToLowerInvariant();
            if (!string.IsNullOrWhiteSpace(v0))
                cmd.positional.Add(v0);
        }
        else
        {
            cmd.verb = first.ToLowerInvariant();
        }

        for (int i = 1; i < tokens.Count; i++)
        {
            // ✅ FIX #1: strip leading commas from subsequent tokens too (rare, but VP strings can be messy)
            string t = tokens[i].Trim().TrimStart(',');

            if (t.Length == 0)
                continue;

            if (TrySplitKeyValue(t, out var k, out var v))
            {
                cmd.kv[k.ToLowerInvariant()] = v;
            }
            else
            {
                cmd.positional.Add(t);
            }
        }

        return cmd;
    }

    private static bool TrySplitKeyValue(string token, out string key, out string value)
    {
        int eq = token.IndexOf('=');
        if (eq <= 0 || eq >= token.Length - 1)
        {
            key = null;
            value = null;
            return false;
        }

        key = token.Substring(0, eq).Trim();
        value = token.Substring(eq + 1).Trim();
        return key.Length > 0;
    }

    // Tokenizer that respects quoted strings: web "http://google.com?q=1 2"
    private static List<string> Tokenize(string s)
    {
        var result = new List<string>();
        if (string.IsNullOrWhiteSpace(s))
            return result;

        bool inQuotes = false;
        var cur = new System.Text.StringBuilder();

        for (int i = 0; i < s.Length; i++)
        {
            char c = s[i];

            if (c == '"')
            {
                inQuotes = !inQuotes;
                continue;
            }

            if (!inQuotes && char.IsWhiteSpace(c))
            {
                if (cur.Length > 0)
                {
                    result.Add(cur.ToString());
                    cur.Clear();
                }
                continue;
            }

            cur.Append(c);
        }

        if (cur.Length > 0)
            result.Add(cur.ToString());

        return result;
    }

    // Splits on ; , and newline, but ignores separators inside quotes.
    private static IEnumerable<string> SplitSegments(string s)
    {
        bool inQuotes = false;
        var cur = new System.Text.StringBuilder();

        for (int i = 0; i < s.Length; i++)
        {
            char c = s[i];

            if (c == '"')
            {
                inQuotes = !inQuotes;
                cur.Append(c); // keep quote
                continue;
            }

            if (!inQuotes && (c == ';' || c == ',' || c == '\n' || c == '\r'))
            {
                if (cur.Length > 0)
                {
                    yield return cur.ToString();
                    cur.Clear();
                }
                continue;
            }

            cur.Append(c);
        }

        if (cur.Length > 0)
            yield return cur.ToString();
    }
}
