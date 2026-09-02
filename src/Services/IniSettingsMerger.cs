using System.Text;

namespace DDS2ModManager.Services;

/// Merges a user's edited .ini onto a newer default, keeping their values and taking everything
/// else - new keys, new sections, new comments, reworded documentation - from the new file.
///
/// This exists because the two obvious approaches are both wrong. Overwriting the file on every
/// UE4SS update throws away the user's settings, which is what used to happen. Preserving their
/// whole file instead pins them to an old one forever: options added by a later UE4SS release
/// never appear, and the comments documenting them never arrive either, so the setting exists but
/// nothing on disk mentions it.
///
/// The merge is driven by a baseline - the file as UE4SS shipped it, before the user touched it.
/// Comparing their current file against that baseline is what separates "the user chose this"
/// from "this was simply the default at the time". Without it a differing value is ambiguous: it
/// could be the user's choice or a default UE4SS itself changed, and carrying the latter over
/// would pin an old default forever.
///
/// Output is the new file's text with override values substituted in place, so its comments,
/// ordering and spacing survive exactly. Nothing is reformatted.
public static class IniSettingsMerger
{
    public record Result(string Text, List<string> Carried, List<string> Dropped)
    {
        public bool ChangedAnything => Carried.Count > 0;
    }

    /// The "key = value" lines where two .ini files disagree, as the FIRST file writes them.
    ///
    /// Used to report what a merge did not carry across, so a value that gets replaced by a new
    /// default is named rather than silently gone. Reads the left file's own text, so what is
    /// reported is exactly what the user would have to type back.
    public static List<string> DifferingLines(string left, string right)
    {
        var a = Parse(left, out _);
        var b = Parse(right, out _);
        var lines = new List<string>();

        foreach (var (slot, entries) in a)
        {
            if (SameValues(entries, b.GetValueOrDefault(slot))) continue;
            lines.Add(Describe(slot, entries));
        }

        return lines;
    }

    /// One "key = value" line, remembered with any +/- prefix UE4SS uses for list-style options so
    /// a repeated key round-trips as the set of lines the user actually wrote.
    private sealed record Entry(string Prefix, string Value, int LineIndex);

    private readonly record struct Slot(string Section, string Key);

    private static Slot SlotFor(string section, string key) =>
        new(section.ToLowerInvariant(), key.ToLowerInvariant());

    /// <param name="newDefault">The .ini as the incoming UE4SS version ships it.</param>
    /// <param name="current">The user's file as it is on disk right now.</param>
    /// <param name="baseline">
    /// The .ini as it shipped with the version the user currently has. Null when there is no
    /// record of it - see the fallback below, which is deliberately the generous one.
    /// </param>
    public static Result Merge(string newDefault, string current, string? baseline)
    {
        var newEntries = Parse(newDefault, out var newLines);
        var currentEntries = Parse(current, out _);
        var baselineEntries = baseline == null ? null : Parse(baseline, out _);

        var carried = new List<string>();
        var dropped = new List<string>();
        var overrides = new Dictionary<Slot, List<Entry>>();

        foreach (var (slot, entries) in currentEntries)
        {
            // With a baseline, an override is a value that differs from what UE4SS shipped.
            //
            // Without one, every value differing from the NEW default is treated as the user's.
            // That is the generous reading on purpose: it can pin a default UE4SS deliberately
            // changed, but the alternative silently discards real settings, and it only applies to
            // the single upgrade from a build that kept no baseline. Every later update has one.
            var reference = baselineEntries != null
                ? baselineEntries.GetValueOrDefault(slot)
                : newEntries.GetValueOrDefault(slot);

            if (SameValues(entries, reference)) continue;

            overrides[slot] = entries;
        }

        if (overrides.Count == 0) return new Result(newDefault, carried, dropped);

        // Rewrite the new file line by line. Only lines belonging to an overridden key are
        // touched; everything else is emitted exactly as it came.
        var replacedAt = new Dictionary<int, List<Entry>>();
        var deleteLines = new HashSet<int>();

        foreach (var (slot, entries) in overrides)
        {
            if (!newEntries.TryGetValue(slot, out var target))
            {
                // The user set something the new version no longer has. Re-adding it would put
                // back a key the new UE4SS does not read, so report it rather than pretend.
                dropped.Add(Describe(slot, entries));
                continue;
            }

            replacedAt[target[0].LineIndex] = entries;
            foreach (var extra in target.Skip(1)) deleteLines.Add(extra.LineIndex);
            carried.Add(Describe(slot, entries));
        }

        // Follow the new file's line endings rather than the ones SplitLines normalised away.
        //
        // This is a settings file for a Windows game and ships CRLF, so emitting a bare \n
        // rewrote every line in the file on any update that carried a value across - a whole-file
        // diff for a one-line change. It also made the merge disagree with itself: the early
        // return above hands back newDefault verbatim, so the same input came back CRLF when
        // there was nothing to carry and LF when there was, and merging twice did not land in
        // the same place.
        var newline = newDefault.Contains("\r\n") ? "\r\n" : "\n";

        var output = new StringBuilder();
        for (var i = 0; i < newLines.Count; i++)
        {
            if (deleteLines.Contains(i)) continue;

            if (replacedAt.TryGetValue(i, out var entries))
            {
                var name = NameOf(newLines[i]);
                foreach (var e in entries)
                    output.Append(e.Prefix).Append(name).Append(" = ").Append(e.Value).Append(newline);
                continue;
            }

            output.Append(newLines[i]).Append(newline);
        }

        // Follow the new file's trailing-newline habit rather than inventing one.
        var text = output.ToString();
        if (!newDefault.EndsWith("\n") && text.EndsWith(newline)) text = text[..^newline.Length];

        return new Result(text, carried, dropped);
    }

    private static string NameOf(string line)
    {
        var eq = line.IndexOf('=');
        var name = eq >= 0 ? line[..eq] : line;
        return name.TrimStart().TrimStart('+', '-').Trim();
    }

    private static string Describe(Slot slot, List<Entry> entries)
    {
        var section = slot.Section.Length == 0 ? "" : $"[{slot.Section}] ";
        var shown = entries.Count == 1
            ? (entries[0].Value.Length == 0 ? "(empty)" : entries[0].Value)
            : string.Join(", ", entries.Select(e => e.Prefix + e.Value));
        return $"{section}{slot.Key} = {shown}";
    }

    private static bool SameValues(List<Entry> a, List<Entry>? b)
    {
        if (b == null) return false;
        if (a.Count != b.Count) return false;
        for (var i = 0; i < a.Count; i++)
        {
            if (!string.Equals(a[i].Value.Trim(), b[i].Value.Trim(), StringComparison.Ordinal)) return false;
            if (a[i].Prefix != b[i].Prefix) return false;
        }
        return true;
    }

    /// Splits an .ini into (section, key) -> the lines that set it.
    ///
    /// Comment lines are skipped rather than parsed. UE4SS documents each option with a commented
    /// example directly above it, so reading "; Example: +ModsFolderPaths = ../SharedMods" as a
    /// value would invent settings nobody wrote.
    private static Dictionary<Slot, List<Entry>> Parse(string text, out List<string> lines)
    {
        lines = text.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n').ToList();

        // A trailing newline leaves a final empty element; dropping it stops the rewrite growing a
        // blank line every time it runs.
        if (lines.Count > 0 && lines[^1].Length == 0) lines.RemoveAt(lines.Count - 1);

        var result = new Dictionary<Slot, List<Entry>>();
        var section = "";

        for (var i = 0; i < lines.Count; i++)
        {
            var line = lines[i].Trim();

            if (line.Length == 0 || line.StartsWith(';') || line.StartsWith('#')) continue;

            if (line.StartsWith('[') && line.EndsWith(']'))
            {
                section = line[1..^1].Trim();
                continue;
            }

            var eq = line.IndexOf('=');
            if (eq < 0) continue;

            var name = line[..eq].Trim();
            var value = line[(eq + 1)..].Trim();
            if (name.Length == 0) continue;

            var prefix = "";
            if (name[0] is '+' or '-')
            {
                prefix = name[..1];
                name = name[1..].Trim();
                if (name.Length == 0) continue;
            }

            var slot = SlotFor(section, name);
            if (!result.TryGetValue(slot, out var list)) result[slot] = list = new List<Entry>();
            list.Add(new Entry(prefix, value, i));
        }

        return result;
    }
}
