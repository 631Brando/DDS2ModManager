using System.Text;

namespace DDS2ModManager.Services;

/// Minimal reader for Valve's KeyValues ("VDF") text format, which is how Steam stores
/// appmanifest_*.acf, localconfig.vdf and remotecache.vdf.
///
/// Only what's needed to look things up is implemented - nested blocks and quoted key/value
/// pairs. Conditionals, includes and unquoted tokens are ignored rather than half-supported,
/// because every file this reads is machine-written and uses the simple form. Anything it can't
/// make sense of yields a null lookup, which callers treat as "unknown".
public sealed class SteamVdf
{
    /// Case-insensitive: Steam is inconsistent about capitalising keys between versions.
    public Dictionary<string, SteamVdf> Children { get; } = new(StringComparer.OrdinalIgnoreCase);

    public string? Value { get; private set; }

    /// Walks a chain of keys, returning null if any step is missing.
    public SteamVdf? Find(params string[] path)
    {
        var node = this;
        foreach (var key in path)
        {
            if (!node.Children.TryGetValue(key, out var next)) return null;
            node = next;
        }
        return node;
    }

    public string? ValueOf(params string[] path) => Find(path)?.Value;

    public static SteamVdf? Load(string path)
    {
        try
        {
            return Parse(File.ReadAllText(path));
        }
        catch (Exception ex)
        {
            LoggingService.Instance.Warn($"Couldn't read '{Path.GetFileName(path)}': {ex.Message}");
            return null;
        }
    }

    public static SteamVdf? Parse(string text)
    {
        var root = new SteamVdf();
        var stack = new Stack<SteamVdf>();
        stack.Push(root);

        string? pendingKey = null;
        var pos = 0;

        while (pos < text.Length)
        {
            var c = text[pos];

            if (char.IsWhiteSpace(c)) { pos++; continue; }

            if (c == '/' && pos + 1 < text.Length && text[pos + 1] == '/')
            {
                while (pos < text.Length && text[pos] != '\n') pos++;
                continue;
            }

            if (c == '{')
            {
                pos++;
                if (pendingKey == null) continue;   // stray block; ignore rather than fail
                var child = new SteamVdf();
                stack.Peek().Children[pendingKey] = child;
                stack.Push(child);
                pendingKey = null;
                continue;
            }

            if (c == '}')
            {
                pos++;
                if (stack.Count > 1) stack.Pop();
                pendingKey = null;
                continue;
            }

            if (c != '"') { pos++; continue; }      // unquoted token - not used by these files

            var token = ReadQuoted(text, ref pos);
            if (token == null) return root;         // truncated file; keep what was parsed

            if (pendingKey == null) pendingKey = token;
            else
            {
                stack.Peek().Children[pendingKey] = new SteamVdf { Value = token };
                pendingKey = null;
            }
        }

        return root;
    }

    private static string? ReadQuoted(string text, ref int pos)
    {
        pos++;                                       // opening quote
        var sb = new StringBuilder();

        while (pos < text.Length)
        {
            var c = text[pos];

            if (c == '\\' && pos + 1 < text.Length)
            {
                pos++;
                sb.Append(text[pos] switch { 'n' => '\n', 't' => '\t', var e => e });
                pos++;
                continue;
            }

            if (c == '"') { pos++; return sb.ToString(); }

            sb.Append(c);
            pos++;
        }

        return null;                                 // unterminated
    }
}
