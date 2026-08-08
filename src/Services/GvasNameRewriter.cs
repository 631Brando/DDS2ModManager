using System.Text;

namespace DDS2ModManager.Services;

/// Updates the places a save file records its own name.
///
/// Cloning a save isn't just a folder copy. DDS2 stores the cartel's name *inside*
/// `CartelDefaults.sav` as `CartelSaveName`, and loads a cartel by looking for
/// `&lt;CartelSaveName&gt;_Progress.save` inside its folder. Copy the folder and rename the progress
/// file without updating that string and the clone still points at the original's progress file,
/// which isn't in the new folder - so the game finds nothing, skips the cartel, and the copy never
/// shows up in the load list at all.
///
/// Nothing here is DDS2-specific. It rewrites any string in a GVAS save whose value is *exactly*
/// the old save name, which is the general shape of the problem: a save that names itself has to
/// be told when it's been renamed. Fields that merely contain the name as a substring, or that
/// happen to hold something else, are left alone.
///
/// Two safeguards, because this writes to save files:
///   - The whole property list must parse before any edit is considered. A file this doesn't fully
///     understand is skipped rather than half-edited.
///   - The rewritten bytes are parsed again, and only written if they still walk cleanly to the
///     terminating "None" with no stale references left.
public static class GvasNameRewriter
{
    /// Standard Unreal SaveGame magic. RamaSave's own files start with the package tag instead and
    /// are deliberately not handled here - rewriting one means reproducing its compression and
    /// internal offsets byte-exactly, which isn't worth the risk of corrupting a playthrough.
    private const string Magic = "GVAS";

    /// FText header ahead of the string: int32 flags, uint8 history type, int32 "has
    /// culture-invariant string".
    private const int TextHeaderLength = 9;

    /// One editable string in the file, and everything needed to resize it in place.
    private sealed record NameSlot(
        string PropertyName, int SizeFieldOffset, long DeclaredSize, int StringOffset, int StringLength, string Value);

    public static bool IsGvasSave(string path)
    {
        try
        {
            using var fs = File.OpenRead(path);
            Span<byte> b = stackalloc byte[4];
            return fs.Read(b) == 4 && Encoding.ASCII.GetString(b) == Magic;
        }
        catch { return false; }
    }

    /// Points every string that spells out the save's old name at the new one. Returns how many
    /// were rewritten; 0 means nothing needed changing, or the file wasn't something this
    /// understands - in both cases the file is left exactly as it was.
    public static int RewriteSelfReferences(string path, string oldName, string newName)
    {
        if (string.Equals(oldName, newName, StringComparison.Ordinal)) return 0;
        if (string.IsNullOrEmpty(oldName) || string.IsNullOrEmpty(newName)) return 0;

        return Rewrite(path, slot => string.Equals(slot.Value, oldName, StringComparison.Ordinal), newName);
    }

    /// Sets a named string property to a given value, whatever it currently holds. Unlike
    /// RewriteSelfReferences this needs to know the property's name, so it's only safe to call
    /// when the game is known - see Dds2SaveRules.
    public static bool SetStringProperty(string path, string propertyName, string newValue)
    {
        if (string.IsNullOrEmpty(propertyName) || string.IsNullOrEmpty(newValue)) return false;

        return Rewrite(path,
            slot => string.Equals(slot.PropertyName, propertyName, StringComparison.Ordinal)
                    && !string.Equals(slot.Value, newValue, StringComparison.Ordinal),
            newValue) > 0;
    }

    /// Reads a named string property, or null if the file doesn't parse or has no such property.
    public static string? ReadStringProperty(string path, string propertyName)
    {
        try
        {
            var slots = new List<NameSlot>();
            if (!TryScan(File.ReadAllBytes(path), slots)) return null;
            return slots.FirstOrDefault(s => string.Equals(s.PropertyName, propertyName, StringComparison.Ordinal))?.Value;
        }
        catch { return null; }
    }

    /// Replaces every string the selector picks out. The same selector is re-run against the
    /// rewritten bytes and must then match nothing, which checks both that the edit landed and
    /// that the file still parses - otherwise the original is left alone.
    private static int Rewrite(string path, Func<NameSlot, bool> selector, string newValue)
    {
        byte[] original;
        try { original = File.ReadAllBytes(path); }
        catch (Exception ex)
        {
            LoggingService.Instance.Warn($"Couldn't read '{Path.GetFileName(path)}': {ex.Message}");
            return 0;
        }

        var slots = new List<NameSlot>();
        if (!TryScan(original, slots)) return 0;

        // Highest offset first, so each edit leaves the offsets of the remaining ones valid.
        var targets = slots
            .Where(selector)
            .OrderByDescending(s => s.StringOffset)
            .ToList();
        if (targets.Count == 0) return 0;

        var replacement = EncodeString(newValue);
        var data = new List<byte>(original);

        foreach (var slot in targets)
        {
            data.RemoveRange(slot.StringOffset, slot.StringLength);
            data.InsertRange(slot.StringOffset, replacement);

            // The property's declared size covers the string, so it moves by the same amount.
            var resized = BitConverter.GetBytes(slot.DeclaredSize + (replacement.Length - slot.StringLength));
            for (var i = 0; i < 8; i++) data[slot.SizeFieldOffset + i] = resized[i];
        }

        var rewritten = data.ToArray();

        var verify = new List<NameSlot>();
        if (!TryScan(rewritten, verify) || verify.Any(selector))
        {
            LoggingService.Instance.Warn(
                $"Left '{Path.GetFileName(path)}' untouched - the renamed version didn't read back cleanly.");
            return 0;
        }

        try
        {
            File.WriteAllBytes(path, rewritten);
            return targets.Count;
        }
        catch (Exception ex)
        {
            LoggingService.Instance.Warn($"Couldn't write '{Path.GetFileName(path)}': {ex.Message}");
            return 0;
        }
    }

    /// Walks the whole property list, collecting every string that could be a self-reference.
    /// Returns false unless the walk reaches the terminating "None", so a partially-understood
    /// file never gets edited.
    private static bool TryScan(byte[] d, List<NameSlot> slots)
    {
        if (d.Length < 32) return false;
        if (Encoding.ASCII.GetString(d, 0, 4) != Magic) return false;

        var p = 4;
        p += 12;    // save game version, UE4 package version, UE5 package version
        p += 6;     // engine major / minor / patch
        p += 4;     // engine changelist
        if (ReadString(d, ref p) == null) return false;   // engine branch

        if (p + 8 > d.Length) return false;
        p += 4;     // custom version format
        var customVersions = BitConverter.ToInt32(d, p);
        p += 4;
        if (customVersions is < 0 or > 10_000) return false;
        p += customVersions * 20;                          // FGuid + int32 each
        if (p < 0 || p > d.Length) return false;

        if (ReadString(d, ref p) == null) return false;    // save game class name

        while (p < d.Length)
        {
            var name = ReadString(d, ref p);
            if (name == null) return false;
            if (name == "None") return true;

            var type = ReadString(d, ref p);
            if (type == null || !type.EndsWith("Property", StringComparison.Ordinal)) return false;
            if (p + 8 > d.Length) return false;

            var sizeFieldOffset = p;
            var size = BitConverter.ToInt64(d, p);
            p += 8;

            // Type-specific tag data. This layout omits Unreal's ArrayIndex field - checked
            // against the game's own saves: including it desynchronises on the second property,
            // omitting it lands exactly on the terminating "None".
            switch (type)
            {
                case "StructProperty":
                    if (ReadString(d, ref p) == null) return false;
                    p += 16;   // struct guid
                    break;
                case "ArrayProperty":
                case "SetProperty":
                case "ByteProperty":
                case "EnumProperty":
                    if (ReadString(d, ref p) == null) return false;
                    break;
                case "MapProperty":
                    if (ReadString(d, ref p) == null) return false;
                    if (ReadString(d, ref p) == null) return false;
                    break;
                case "BoolProperty":
                    p += 1;    // the value lives in the tag; declared size is 0
                    break;
            }

            p += 1;            // has-property-guid flag
            if (size < 0 || p + size > d.Length) return false;

            RecordSlot(d, name, type, sizeFieldOffset, size, p, slots);
            p += (int)size;
        }

        return false;          // ran out of file without the terminator
    }

    private static void RecordSlot(byte[] d, string propertyName, string type, int sizeFieldOffset, long size, int valueStart, List<NameSlot> slots)
    {
        int stringOffset;
        switch (type)
        {
            case "StrProperty":
            case "NameProperty":
                stringOffset = valueStart;
                break;

            case "TextProperty":
                // Only plain culture-invariant text is handled. Anything localised has a different
                // history layout and is left alone rather than guessed at.
                if (size < TextHeaderLength + 4) return;
                if (d[valueStart + 4] != 0xFF) return;
                if (BitConverter.ToInt32(d, valueStart + 5) != 1) return;
                stringOffset = valueStart + TextHeaderLength;
                break;

            default:
                return;
        }

        var p = stringOffset;
        var value = ReadString(d, ref p);
        if (string.IsNullOrEmpty(value)) return;

        slots.Add(new NameSlot(propertyName, sizeFieldOffset, size, stringOffset, p - stringOffset, value));
    }

    /// Unreal FString: int32 length then the characters including a null terminator. A negative
    /// length means UTF-16, so names with non-ASCII characters round-trip correctly.
    private static byte[] EncodeString(string value)
    {
        var isAscii = value.All(c => c < 128);
        if (isAscii)
        {
            var bytes = new byte[4 + value.Length + 1];
            BitConverter.GetBytes(value.Length + 1).CopyTo(bytes, 0);
            Encoding.ASCII.GetBytes(value).CopyTo(bytes, 4);
            return bytes;
        }

        var wide = new byte[4 + (value.Length + 1) * 2];
        BitConverter.GetBytes(-(value.Length + 1)).CopyTo(wide, 0);
        Encoding.Unicode.GetBytes(value).CopyTo(wide, 4);
        return wide;
    }

    private static string? ReadString(byte[] b, ref int pos)
    {
        if (pos < 0 || pos + 4 > b.Length) return null;
        var len = BitConverter.ToInt32(b, pos);
        if (len == 0) { pos += 4; return ""; }

        if (len > 0)
        {
            if (len > 65536 || pos + 4 + len > b.Length) return null;
            if (b[pos + 4 + len - 1] != 0) return null;
            var s = Encoding.ASCII.GetString(b, pos + 4, len - 1);
            pos += 4 + len;
            return s;
        }

        if (len == int.MinValue) return null;
        var chars = -len;
        if (chars < 1 || chars > 65536 || pos + 4 + chars * 2 > b.Length) return null;
        var u = Encoding.Unicode.GetString(b, pos + 4, (chars - 1) * 2);
        pos += 4 + chars * 2;
        return u;
    }
}
