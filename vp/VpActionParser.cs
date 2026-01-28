using System;
using System.Collections.Generic;
using UnityEngine;

public static class VpActionParser
{

    private sealed class CachedActionLists
    {
        public readonly List<VpActionCommand> create;
        public readonly List<VpActionCommand> activate;

        public CachedActionLists(List<VpActionCommand> create, List<VpActionCommand> activate)
        {
            this.create = create;
            this.activate = activate;
        }
    }

    private static readonly Dictionary<string, CachedActionLists> CachedParsedActions = new(StringComparer.Ordinal);
    private static readonly List<VpActionCommand> EmptyActions = new();

    private enum Phase
    {
        None,
        Create,
        Activate
    }

    public static void Parse(string action, out List<VpActionCommand> createActions, out List<VpActionCommand> activateActions, bool cloneLists = false)
    {
        createActions = EmptyActions;
        activateActions = EmptyActions;

        if (string.IsNullOrWhiteSpace(action))
            return;

        if (CachedParsedActions.TryGetValue(action, out var cached))
        {
            if (cloneLists)
            {
                createActions = CloneActions(cached.create);
                activateActions = CloneActions(cached.activate);
                return;
            }

            createActions = cached.create;
            activateActions = cached.activate;
            return;
        }

        var parsedCreate = new List<VpActionCommand>();
        var parsedActivate = new List<VpActionCommand>();

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
                AddCommand(current, cmdTokens, seg, parsedCreate, parsedActivate);
                continue;
            }

            // Command inherits current mode
            if (current == Phase.None)
            {
                // If you prefer default-to-create behavior, uncomment:
                // current = Phase.Create;
                continue;
            }

            AddCommand(current, tokens, seg, parsedCreate, parsedActivate);
        }

        CachedParsedActions[action] = new CachedActionLists(parsedCreate, parsedActivate);

        if (cloneLists)
        {
            createActions = CloneActions(parsedCreate);
            activateActions = CloneActions(parsedActivate);
            return;
        }

        createActions = parsedCreate;
        activateActions = parsedActivate;
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

    private static List<VpActionCommand> CloneActions(List<VpActionCommand> source)
    {
        if (source == null || source.Count == 0)
            return new List<VpActionCommand>();

        var clone = new List<VpActionCommand>(source.Count);
        for (int i = 0; i < source.Count; i++)
        {
            var cmd = source[i];
            if (cmd == null)
            {
                clone.Add(null);
                continue;
            }

            var cmdClone = new VpActionCommand
            {
                raw = cmd.raw,
                verb = cmd.verb,
                positional = cmd.positional != null ? new List<string>(cmd.positional) : new List<string>(),
                kv = cmd.kv != null ? new Dictionary<string, string>(cmd.kv) : new Dictionary<string, string>()
            };
            clone.Add(cmdClone);
        }

        return clone;
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
