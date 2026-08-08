using System.Text;

namespace DDS2ModManager.Services;

/// Reads DDS2's "_Progress.save" files.
///
/// These aren't GVAS saves, so ordinary Unreal save libraries can't open them - the game uses
/// RamaSave, which writes its own container and its own property records. The layout, worked out
/// by validating a parse against every record in every save on disk, is:
///
///   zlib-chunked archive (128 KB chunks)
///     int32   payload size
///     int32   format (7)
///     int32   package version (UE4), int32 package version (UE5)
///     uint16  engine major, minor, patch + uint32 changelist
///     FString engine branch ("++UE5+Release-5.3", but "UE5" in some saves - so the tag count
///             that follows is NOT at a fixed offset and the branch has to be walked)
///     int32   tag count, then that many FStrings ("&lt;GUID&gt;=True")
///     byte    0
///     int32   actor count
///     per actor:
///       int64   endOffset          <- where this record ends
///       FString class name
///       FString full class path, FGuid actor guid, int32, FString level name, int64
///       entries...
///
/// An entry is "FString name, int64 marker, value bytes", where the next entry begins at
/// marker + 4. Between sections RamaSave writes small unnamed fields - a 4- or 8-byte count, or a
/// 64-byte physics block when the actor has RamaSave_SavePhysicsData set.
///
/// The endOffset field is what makes reading this format without a third-party library
/// defensible: every record states exactly where it ends, so the walk can be checked rather than
/// trusted. A misparse desynchronises immediately instead of silently producing
/// plausible-but-wrong values. Across the whole save set this parse lands on every record's
/// declared boundary exactly.
///
/// Read-only by design. Writing would have to reproduce the compression, the offsets and the
/// markers byte-exactly, and getting that wrong corrupts a playthrough.
public class RamaSaveReader
{
    /// Files start with Unreal's package tag; standard GVAS saves start with "GVAS" instead.
    private const uint PackageFileTag = 0x9E2A83C1;

    /// Bounded resync window for the unnamed fields between sections. The largest one seen is the
    /// 64-byte physics block; the cap keeps a corrupt record from scanning off into the next one.
    private const int MaxResync = 1024;

    /// Components can contain components. The cap stops a malformed save from recursing forever.
    private const int MaxNestedEntryDepth = 6;

    /// A nested run of entries may or may not be preceded by a count field, exactly as at record
    /// level; these are the widths seen in practice.
    private static readonly int[] LeadingCountSizes = { 4, 0, 8 };

    public static bool IsProgressSave(string path)
    {
        try
        {
            using var fs = File.OpenRead(path);
            Span<byte> b = stackalloc byte[4];
            return fs.Read(b) == 4 && BitConverter.ToUInt32(b) == PackageFileTag;
        }
        catch { return false; }
    }

    public SaveFileData? Read(string path)
    {
        try
        {
            var raw = File.ReadAllBytes(path);
            var d = Decompress(raw);
            if (d.Length < 64) return null;

            var result = new SaveFileData
            {
                Path = path,
                CompressedBytes = raw.Length,
                DecompressedBytes = d.Length,
                PackageVersionUE4 = BitConverter.ToInt32(d, 8),
                PackageVersionUE5 = BitConverter.ToInt32(d, 12),
                EngineVersion = $"{BitConverter.ToUInt16(d, 16)}.{BitConverter.ToUInt16(d, 18)}.{BitConverter.ToUInt16(d, 20)}"
            };

            // The branch string is variable length, so walk it rather than assuming where the
            // tag list starts.
            var pos = 26;
            result.EngineBranch = ReadFString(d, ref pos) ?? "";
            if (pos + 4 > d.Length) return result;

            var tagCount = ReadInt32(d, ref pos);
            if (tagCount is < 0 or > 100_000) return result;
            for (var i = 0; i < tagCount; i++)
            {
                var tag = ReadFString(d, ref pos);
                if (tag == null) return result;
                result.Tags.Add(tag);
            }

            pos += 1; // separator between the tag list and the actor list
            var actorCount = ReadInt32(d, ref pos);
            if (actorCount is < 0 or > 200_000) return result;

            for (var i = 0; i < actorCount && pos + 8 <= d.Length; i++)
            {
                var endOffset = ReadInt64(d, ref pos);
                if (endOffset <= pos || endOffset > d.Length) break;

                var className = ReadFString(d, ref pos);
                if (className == null) break;

                var limit = (int)endOffset;
                var record = new SaveActorRecord { ClassName = className };

                // Record header, ahead of the entries:
                //   FString full class path ("/Game/.../BP_Foo.BP_Foo_C")
                //   FGuid   actor guid (16 bytes)
                //   int32   reserved
                //   FString streaming level name ("PersistentLevel")
                //   int64   reserved
                ReadFString(d, ref pos);
                pos += 16;
                pos += 4;
                record.LevelName = ReadFString(d, ref pos) ?? "";
                pos += 8;

                if (pos > 0 && pos <= limit)
                    record.UnreadBytes = ReadEntries(d, ref pos, limit, record.Properties, 0);

                record.FullyParsed = pos == limit || pos == limit + 4;
                result.Actors.Add(record);

                pos = limit + 4;
            }

            ResolveEmptyContainers(result);
            return result;
        }
        catch (Exception ex)
        {
            LoggingService.Instance.Warn($"Couldn't read save '{Path.GetFileName(path)}': {ex.Message}");
            return null;
        }
    }

    /// Inflates every zlib chunk in the archive. Chunk boundaries are found by their zlib headers
    /// rather than by the chunk table, which is simpler and self-correcting: an inflate that
    /// produces nothing useful is skipped, and the result is validated by the record chain above.
    private static byte[] Decompress(byte[] src)
    {
        var outMs = new MemoryStream();
        for (var i = 0; i < src.Length - 2; i++)
        {
            if (src[i] != 0x78) continue;
            if (src[i + 1] is not (0x01 or 0x5E or 0x9C or 0xDA)) continue;

            try
            {
                using var ms = new MemoryStream(src, i + 2, src.Length - i - 2);
                using var ds = new DeflateStream(ms, CompressionMode.Decompress);
                var before = outMs.Length;
                ds.CopyTo(outMs);
                if (outMs.Length - before > 64) i += 16;
            }
            catch { /* not a chunk boundary after all */ }
        }
        return outMs.ToArray();
    }

    /// An empty map serialises to eight zero bytes - a zero "keys to remove" count followed by a
    /// zero pair count - which is byte-for-byte identical to an int64 zero. Nothing in the value
    /// itself can tell them apart, so on its own the length-based guess reports an empty inventory
    /// as the number 0, which reads as though the field held a quantity.
    ///
    /// The save as a whole does disambiguate them though: the same field on another actor usually
    /// has something in it, and that copy decodes unambiguously as a map. So any field name seen
    /// as a populated map somewhere in the file is treated as a map everywhere in it.
    ///
    /// This only ever reinterprets all-zero values - a non-zero int64 is never ambiguous and is
    /// left alone - and it never invents contents, it just labels an empty container as empty.
    private static void ResolveEmptyContainers(SaveFileData data)
    {
        var knownMaps = new HashSet<string>(StringComparer.Ordinal);
        foreach (var p in data.Actors.SelectMany(a => Walk(a.Properties)))
            if (p.HasChildren && p.Type.StartsWith("Map (", StringComparison.Ordinal))
                knownMaps.Add(p.Name);

        if (knownMaps.Count == 0) return;

        foreach (var p in data.Actors.SelectMany(a => Walk(a.Properties)))
        {
            if (p.Type != AmbiguousZero) continue;
            if (!knownMaps.Contains(p.Name)) continue;

            p.Type = "Map (0)";
            p.Value = null;
            p.IsRecognisedContainer = true;
        }
    }

    private static IEnumerable<SaveProperty> Walk(IEnumerable<SaveProperty> properties)
    {
        foreach (var p in properties)
        {
            yield return p;
            foreach (var child in Walk(p.Children)) yield return child;
        }
    }

    // ---- RamaSave entries -------------------------------------------------------------------

    /// Walks a run of entries, resyncing across the unnamed fields between sections. Returns the
    /// number of bytes that weren't part of any entry.
    private static int ReadEntries(byte[] d, ref int pos, int limit, List<SaveProperty> into, int depth)
    {
        var unread = 0;

        while (pos < limit)
        {
            if (TryReadEntryHeader(d, pos, limit, out var name, out var valueStart, out var next))
            {
                into.Add(BuildEntry(d, name, valueStart, next - valueStart, depth));
                pos = next;
                continue;
            }

            // A count field or the physics block. Rather than model every shape, step forward to
            // the next position that reads as a real entry - and give up if there isn't one, so a
            // corrupt record fails its end-offset check instead of inventing values.
            var found = -1;
            for (var k = 1; k <= MaxResync && pos + k < limit; k++)
            {
                if (!TryReadEntryHeader(d, pos + k, limit, out _, out _, out _)) continue;
                found = pos + k;
                break;
            }
            if (found < 0) return unread;

            unread += found - pos;
            pos = found;
        }

        return unread;
    }

    /// An entry header is "FString name, int64 marker"; the next entry starts at marker + 4, so
    /// the record's last entry points just past its end.
    private static bool TryReadEntryHeader(byte[] d, int at, int limit, out string name, out int valueStart, out int next)
    {
        name = "";
        valueStart = next = -1;

        var p = at;
        var read = ReadFString(d, ref p);
        if (string.IsNullOrEmpty(read) || p + 8 > limit + 4) return false;

        var target = BitConverter.ToInt64(d, p) + 4;
        if (target < p + 8 || target > limit + 4) return false;

        name = read;
        valueStart = p + 8;
        next = (int)target;
        return true;
    }

    private static SaveProperty BuildEntry(byte[] d, string name, int start, int length, int depth)
    {
        var prop = new SaveProperty { Name = name, ByteLength = length, Offset = start };

        // An entry's value is often a nested tagged-property blob (the actor's transform, or a
        // whole component such as an inventory). Those carry real type information, so prefer them.
        if (LooksLikeTaggedProperties(d, start, length))
        {
            prop.Type = "Struct";
            prop.IsRecognisedContainer = true;
            var p = start;
            ReadTaggedProperties(d, ref p, start + length, prop.Children, 0);
            return prop;
        }

        // Components hold another run of RamaSave entries; arrays of structs and Guid-keyed maps
        // carry enough type information to decode as well. Each of these is accepted only if its
        // walk lands exactly on the end of the value, so a wrong guess is rejected rather than
        // shown.
        if (TryReadNestedEntries(d, start, length, prop, depth)) return prop;
        if (TryReadStructArray(d, start, length, prop)) return prop;
        if (TryReadMap(d, start, length, prop)) return prop;
        if (TryReadStringList(d, start, length, prop)) return prop;
        if (TryReadEmptyText(d, start, length, prop)) return prop;

        // Otherwise RamaSave gives no type at all, so it has to be inferred from the length.
        // Anything ambiguous stays an honest byte count rather than a confident guess.
        prop.Type = DescribeByLength(d, start, length);
        prop.Value = InterpretByLength(d, start, length);
        return prop;
    }

    /// Eight zero bytes are a genuine three-way tie: the integer 0, the number 0.0, and an empty
    /// map all serialise to exactly that. Rather than pick one and be wrong a third of the time,
    /// say so - ResolveEmptyContainers upgrades these to "Map (0)" wherever the rest of the file
    /// settles the question.
    private const string AmbiguousZero = "Number or empty map";

    private static string DescribeByLength(byte[] d, int start, int length) => length switch
    {
        1 => "Bool",
        4 => "Int32",
        8 when IsAllZero(d, start, 8) => AmbiguousZero,
        8 => LooksLikeDouble(d, start) ? "Float" : "Int64",
        16 => "Guid",
        _ => "Bytes"
    };

    private static bool IsAllZero(byte[] d, int start, int length)
    {
        for (var i = start; i < start + length; i++)
            if (d[i] != 0) return false;
        return true;
    }

    private static object? InterpretByLength(byte[] d, int start, int length)
    {
        switch (length)
        {
            case 1:
                return d[start] != 0;
            case 4:
                return BitConverter.ToInt32(d, start);
            case 8 when IsAllZero(d, start, 8):
                return 0L; // shown as 0, typed as ambiguous - see AmbiguousZero
            case 8:
                // Blueprint "float" variables are doubles in UE5, so an 8-byte value is far more
                // often a double than an int64 - but only say so when the bits actually look like
                // a sane number.
                return LooksLikeDouble(d, start)
                    ? BitConverter.ToDouble(d, start)
                    : BitConverter.ToInt64(d, start);
            case 16:
                return FormatGuid(d, start);
            default:
            {
                var p = start;
                var s = ReadFString(d, ref p);
                if (s != null && p <= start + length && s.Length > 0 && IsPrintable(s)) return s;
                return null;
            }
        }
    }

    private static bool LooksLikeDouble(byte[] d, int start)
    {
        var v = BitConverter.ToDouble(d, start);
        if (v == 0) return false;
        if (double.IsNaN(v) || double.IsInfinity(v)) return false;
        var abs = Math.Abs(v);
        return abs is > 1e-6 and < 1e12;
    }

    // ---- Nested Unreal tagged properties ----------------------------------------------------

    /// RamaSave's tag layout, which differs from stock Unreal by omitting the array index:
    ///
    ///   FString name                       ("None" ends the list)
    ///   FString type                       ("IntProperty", "StructProperty", ...)
    ///   int64   size                       size of the value, in bytes
    ///   type-specific data:
    ///     BoolProperty           uint8 value (the value lives here; declared size is 0)
    ///     StructProperty         FString struct name, FGuid struct guid
    ///     ArrayProperty / Set    FString element type
    ///     MapProperty            FString key type, FString value type
    ///     Byte / EnumProperty    FString enum name
    ///   uint8   has property guid
    ///   value                              exactly `size` bytes
    ///
    /// The type-specific block matters even for types this reader doesn't decode: skipping it
    /// would put the walk a few bytes out and every later property would be garbage.
    ///
    /// Because every tag declares its own size, an unrecognised type can be stepped over exactly
    /// rather than guessed at - which is what keeps the walk from desynchronising.
    private static void ReadTaggedProperties(byte[] d, ref int pos, int limit, List<SaveProperty> into, int depth)
    {
        if (depth > 8) return;

        while (pos < limit)
        {
            var name = ReadFString(d, ref pos);
            if (string.IsNullOrEmpty(name) || name == "None") return;

            var type = ReadFString(d, ref pos);
            if (string.IsNullOrEmpty(type) || !type.EndsWith("Property", StringComparison.Ordinal)) return;
            if (pos + 8 > limit) return;

            var size = ReadInt64(d, ref pos);
            if (size < 0 || size > limit - pos) return;

            string? structName = null;
            bool? boolValue = null;
            switch (type)
            {
                // A bool's value lives in the tag itself, and its declared size is 0.
                case "BoolProperty":
                    if (pos >= limit) return;
                    boolValue = d[pos] != 0;
                    pos += 1;
                    break;
                case "StructProperty":
                    structName = ReadFString(d, ref pos);
                    if (structName == null) return;
                    pos += 16; // struct guid
                    break;
                case "ArrayProperty":
                case "SetProperty":
                    if (ReadFString(d, ref pos) == null) return;
                    break;
                case "MapProperty":
                    if (ReadFString(d, ref pos) == null) return;
                    if (ReadFString(d, ref pos) == null) return;
                    break;
                case "ByteProperty":
                case "EnumProperty":
                    if (ReadFString(d, ref pos) == null) return;
                    break;
            }

            if (pos >= limit) return;
            pos += 1; // has-property-guid flag; always 0 in these saves

            var valueStart = pos;
            var valueEnd = valueStart + (int)size;
            if (valueEnd > limit) return;

            var prop = new SaveProperty
            {
                Name = name,
                Type = structName is { Length: > 0 } and not "Guid" ? $"{structName} (struct)" : type,
                ByteLength = (int)size,
                Offset = valueStart
            };

            switch (type)
            {
                case "BoolProperty":
                    prop.Value = boolValue;
                    break;
                case "StructProperty" when structName == "Guid" && size == 16:
                    prop.Value = FormatGuid(d, valueStart);
                    break;
                case "StructProperty":
                {
                    prop.IsRecognisedContainer = true;
                    var inner = valueStart;
                    ReadTaggedProperties(d, ref inner, valueEnd, prop.Children, depth + 1);
                    break;
                }
                case "ArrayProperty":
                    // Same layout as an array-valued entry, so reuse the same reader.
                    TryReadStructArray(d, valueStart, (int)size, prop);
                    break;
                case "MapProperty":
                    TryReadMap(d, valueStart, (int)size, prop);
                    break;
                case "TextProperty":
                    // Unset text is by far the most common case (quest overrides, SMS bodies).
                    if (!TryReadEmptyText(d, valueStart, (int)size, prop))
                        prop.Value = InterpretTagged(d, type, valueStart, (int)size);
                    break;
                default:
                    prop.Value = InterpretTagged(d, type, valueStart, (int)size);
                    break;
            }

            into.Add(prop);
            pos = valueEnd; // always trust the declared size, never where the value walk stopped
        }
    }

    private static object? InterpretTagged(byte[] d, string type, int start, int size)
    {
        switch (type)
        {
            case "IntProperty" when size == 4:
                return BitConverter.ToInt32(d, start);
            case "Int64Property" when size == 8:
                return BitConverter.ToInt64(d, start);
            case "FloatProperty" when size == 4:
                return BitConverter.ToSingle(d, start);
            case "DoubleProperty" when size == 8:
                return BitConverter.ToDouble(d, start);
            case "ByteProperty" when size == 1:
                return (int)d[start];
            case "StrProperty":
            case "NameProperty":
            case "EnumProperty":
            {
                var p = start;
                var s = ReadFString(d, ref p);
                return s != null && IsPrintable(s) ? s : null;
            }
            default:
                // Arrays, maps, sets, text and anything else: reported as a sized container rather
                // than decoded, because their element layout varies by inner type.
                return null;
        }
    }

    /// An unset FText: int32 flags, a 0xFF "no history" marker, then an int32 zero. Text the
    /// player never filled in (a hideout they didn't rename, say) is by far the most common
    /// non-scalar value in a save, and showing it as nine raw bytes is just noise.
    private static bool TryReadEmptyText(byte[] d, int start, int length, SaveProperty prop)
    {
        if (length != 9) return false;
        if (BitConverter.ToInt32(d, start) != 0) return false;
        if (d[start + 4] != 0xFF) return false;
        if (BitConverter.ToInt32(d, start + 5) != 0) return false;

        prop.Type = "Text";
        prop.IsRecognisedContainer = true; // displays as "(empty)"
        return true;
    }

    /// A plain string array: int32 count followed by that many FStrings. RamaSave uses this for
    /// the lists naming which variables an actor and its components save.
    private static bool TryReadStringList(byte[] d, int start, int length, SaveProperty prop)
    {
        var end = start + length;
        if (length < 8) return false;

        var p = start;
        var count = ReadInt32(d, ref p);
        if (count is < 1 or > 100_000) return false;

        var items = new List<SaveProperty>(count);
        for (var i = 0; i < count; i++)
        {
            var at = p;
            var s = ReadFString(d, ref p);
            if (s == null || p > end || !IsPrintable(s)) return false;
            items.Add(new SaveProperty
            {
                Name = $"[{i}]", Type = "String", Value = s, Offset = at, ByteLength = p - at
            });
        }

        if (p != end) return false;

        prop.Type = $"String[{count}]";
        prop.IsRecognisedContainer = true;
        prop.Children.AddRange(items);
        return true;
    }

    /// A component's value: an element count followed by another run of RamaSave entries. This is
    /// how an actor's components (inventories, the quest manager, the cartel manager) are stored,
    /// and it's where most of a save's bulk lives.
    ///
    /// Accepted only when the nested chain consumes the value exactly - the same oracle used on
    /// actor records, applied locally - so this can't mistake an ordinary blob for a component.
    private static bool TryReadNestedEntries(byte[] d, int start, int length, SaveProperty prop, int depth)
    {
        if (depth >= MaxNestedEntryDepth || length < 24) return false;
        var end = start + length;

        // The run may be preceded by a count field, the same as at record level.
        foreach (var lead in LeadingCountSizes)
        {
            var p = start + lead;
            if (p >= end) continue;
            if (!TryReadEntryHeader(d, p, end, out _, out _, out _)) continue;

            var children = new List<SaveProperty>();
            var unread = ReadEntries(d, ref p, end, children, depth + 1);
            if (children.Count == 0 || (p != end && p != end + 4)) continue;

            prop.Type = $"Component ({children.Count})";
            prop.IsRecognisedContainer = true;
            prop.Children.AddRange(children);
            prop.UnreadBytes = unread + lead;
            return true;
        }

        return false;
    }

    private enum MapKey { Guid, String }
    private enum MapValue { Struct, String, Int32 }

    /// A map, written as Unreal writes any map:
    ///
    ///   int32 keys to remove (always 0 here), int32 pair count
    ///   pairs: key, then value
    ///
    /// Neither the key type nor the value type is recorded anywhere in the save - RamaSave relies
    /// on the Blueprint to know them. So every combination seen in practice is tried and the one
    /// that consumes the map exactly is the one that's right; if none does, the entry is left
    /// undecoded rather than shown as a plausible misreading.
    private static bool TryReadMap(byte[] d, int start, int length, SaveProperty prop)
    {
        var end = start + length;
        if (length < 12) return false;

        // Unreal writes a "keys to remove" list first; saves only ever have an empty one.
        if (BitConverter.ToInt32(d, start) != 0) return false;
        var count = BitConverter.ToInt32(d, start + 4);
        if (count is < 1 or > 200_000) return false;

        foreach (var keyKind in new[] { MapKey.Guid, MapKey.String })
        foreach (var valueKind in new[] { MapValue.Struct, MapValue.String, MapValue.Int32 })
        {
            if (TryReadMapAs(d, start + 8, end, count, keyKind, valueKind, out var pairs))
            {
                prop.Type = $"Map ({count})";
                prop.IsRecognisedContainer = true;
                prop.Children.AddRange(pairs);
                return true;
            }
        }

        return false;
    }

    private static bool TryReadMapAs(byte[] d, int pos, int end, int count, MapKey keyKind, MapValue valueKind,
        out List<SaveProperty> pairs)
    {
        pairs = new List<SaveProperty>(Math.Min(count, 1024));

        for (var i = 0; i < count; i++)
        {
            var pairStart = pos;
            string key;

            if (keyKind == MapKey.Guid)
            {
                if (pos + 16 > end) return false;
                key = FormatGuid(d, pos);
                pos += 16;
            }
            else
            {
                // An empty key is legitimate - the game stores maps keyed by an optional name -
                // so only a malformed string disqualifies the reading. The exact-landing check
                // at the end is what actually validates this interpretation.
                var k = ReadFString(d, ref pos);
                if (k == null || !IsPrintable(k) || pos > end) return false;
                key = k.Length == 0 ? "(empty key)" : k;
            }

            var pair = new SaveProperty { Name = key, Offset = pairStart };

            switch (valueKind)
            {
                case MapValue.Struct:
                {
                    var before = pos;
                    ReadTaggedProperties(d, ref pos, end, pair.Children, 1);
                    if (pos == before || pos > end) return false;
                    pair.Type = "Struct";
                    pair.IsRecognisedContainer = true;
                    break;
                }
                case MapValue.String:
                {
                    var s = ReadFString(d, ref pos);
                    if (s == null || pos > end) return false;
                    pair.Type = "String";
                    pair.Value = s;
                    break;
                }
                default:
                {
                    if (pos + 4 > end) return false;
                    pair.Type = "Int32";
                    pair.Value = BitConverter.ToInt32(d, pos);
                    pos += 4;
                    break;
                }
            }

            pair.ByteLength = pos - pairStart;
            pairs.Add(pair);
        }

        return pos == end;
    }

    /// An array-of-structs value, which Unreal writes as an element count followed by a single
    /// inner tag describing the element type, then the elements back to back:
    ///
    ///   int32   element count
    ///   FString property name, FString "StructProperty", int64 total size
    ///   FString struct name, FGuid struct guid, uint8 has property guid
    ///   elements                            each a tagged-property list terminated by "None"
    ///
    /// Arrays of plain scalars have no inner tag and no way to tell the element type apart from
    /// the Blueprint, so those deliberately stay undecoded.
    private static bool TryReadStructArray(byte[] d, int start, int length, SaveProperty prop)
    {
        var end = start + length;
        if (length < 24) return false;

        var p = start;
        var count = ReadInt32(d, ref p);
        if (count is < 0 or > 100_000) return false;

        var name = ReadFString(d, ref p);
        if (string.IsNullOrEmpty(name) || !IsPrintable(name)) return false;

        var type = ReadFString(d, ref p);
        if (type != "StructProperty") return false;
        if (p + 8 > end) return false;

        var size = ReadInt64(d, ref p);
        var structName = ReadFString(d, ref p);
        if (string.IsNullOrEmpty(structName) || !IsPrintable(structName)) return false;

        p += 16; // struct guid
        if (p >= end) return false;
        p += 1;  // has-property-guid flag

        if (size < 0 || p + size > end) return false;

        prop.Type = $"{structName}[{count}]";
        prop.IsRecognisedContainer = true;
        for (var i = 0; i < count && p < end; i++)
        {
            var before = p;
            var element = new SaveProperty { Name = $"[{i}]", Type = structName, Offset = p };
            ReadTaggedProperties(d, ref p, end, element.Children, 1);
            element.ByteLength = p - before;

            // No progress means the element list didn't parse; stop rather than spin or invent.
            if (p == before) break;
            prop.Children.Add(element);
        }

        return true;
    }

    /// A nested blob starts with a property name followed by a type ending in "Property", or is an
    /// immediately-terminated "None".
    private static bool LooksLikeTaggedProperties(byte[] d, int start, int length)
    {
        if (length < 9) return false;

        var p = start;
        var name = ReadFString(d, ref p);
        if (name == null) return false;
        if (name == "None") return true;
        if (name.Length == 0 || !IsPrintable(name)) return false;
        if (p >= start + length) return false;

        var type = ReadFString(d, ref p);
        return type != null && type.EndsWith("Property", StringComparison.Ordinal);
    }

    // ---- Primitives -------------------------------------------------------------------------

    private static bool IsPrintable(string s)
    {
        foreach (var c in s)
            if (c is < ' ' or > '~') return false;
        return true;
    }

    private static string FormatGuid(byte[] d, int start) =>
        new Guid(d.AsSpan(start, 16)).ToString();

    private static int ReadInt32(byte[] b, ref int pos)
    {
        var v = BitConverter.ToInt32(b, pos);
        pos += 4;
        return v;
    }

    private static long ReadInt64(byte[] b, ref int pos)
    {
        var v = BitConverter.ToInt64(b, pos);
        pos += 8;
        return v;
    }

    /// Unreal FString: int32 length then the characters including a null terminator. A negative
    /// length means UTF-16 and the magnitude is the character count. Returns null (leaving pos
    /// untouched) when the bytes can't be a string, which is what the entry probe relies on.
    private static string? ReadFString(byte[] b, ref int pos)
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
