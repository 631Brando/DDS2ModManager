namespace DDS2ModManager.Models;

/// One value read out of a progress save.
///
/// Two things produce these: RamaSave's own name/offset entries (the top level of every actor
/// record), and Unreal tagged properties nested inside an entry's value. Both end up here so the
/// UI can show one uniform tree.
public class SaveProperty
{
    public string Name { get; set; } = "";

    /// Either an Unreal property type ("IntProperty", "StructProperty", ...) for nested tagged
    /// properties, or an inferred type ("Bool", "Int32", "Guid", "Bytes") for RamaSave entries,
    /// which carry no type information of their own.
    public string Type { get; set; } = "";

    /// Decoded value, or null when the value is a container/struct (see Children) or a type this
    /// reader deliberately doesn't guess at.
    public object? Value { get; set; }

    /// True once the reader has identified what this value is, even if it turned out to hold
    /// nothing. Without it an empty array is indistinguishable from a blob we failed to read.
    public bool IsRecognisedContainer { get; set; }

    /// Tagged properties found inside this value, if any.
    public List<SaveProperty> Children { get; } = new();

    public int ByteLength { get; set; }

    /// Offset of this value within the decompressed payload. Kept so the UI can show the raw
    /// bytes behind anything the reader declines to decode.
    public int Offset { get; set; }

    public bool HasChildren => Children.Count > 0;

    /// Bytes inside this value that weren't part of any nested entry - section count fields and
    /// the like. Surfaced rather than hidden so the parse stays honest.
    public int UnreadBytes { get; set; }

    public string ValueDisplay => Value switch
    {
        null when HasChildren => $"{Children.Count} item{(Children.Count == 1 ? "" : "s")}",
        null when IsRecognisedContainer => "(empty)",
        null => $"<{Type.Replace("Property", "")}, {ByteLength} bytes>",
        bool b => b ? "true" : "false",
        float f => f.ToString("0.###"),
        double d => d.ToString("0.###"),
        _ => Value.ToString() ?? ""
    };

    /// "Name  (Type)" - used as the tree node header.
    public string Display => $"{Name}   {ValueDisplay}";
}

/// One saved actor inside a progress save. RamaSave writes one of these per persistent actor.
public class SaveActorRecord
{
    public string ClassName { get; set; } = "";

    /// The streaming level the actor belongs to, e.g. "PersistentLevel".
    public string LevelName { get; set; } = "";

    public List<SaveProperty> Properties { get; } = new();

    /// Everything except RamaSave's own fourteen bookkeeping entries - i.e. the actor's actual
    /// gameplay variables. This is what's worth showing first.
    public IEnumerable<SaveProperty> GameplayProperties =>
        Properties.Where(p => !RamaSaveMetadata.Contains(p.Name));

    /// True when the walk consumed exactly the bytes the record declared it occupies. This is the
    /// parser's correctness oracle: a misread desynchronises the stream immediately, so landing on
    /// the record's own end offset means the walk was right.
    public bool FullyParsed { get; set; }

    /// Bytes inside the record that weren't part of any entry - section count fields and the
    /// optional 64-byte physics block. Surfaced rather than hidden so the parse stays honest.
    public int UnreadBytes { get; set; }

    /// RamaSave writes these fourteen on every actor regardless of what the actor saves.
    public static readonly HashSet<string> RamaSaveMetadata = new(StringComparer.Ordinal)
    {
        "RamaSave_PersistentActorUniqueID",
        "RamaSave_LogPersistentActorGUID",
        "RamaSave_SaveTags",
        "ActorStreamingLevel",
        "RamaSave_ShouldLoadActorWorldPosition",
        "RamaSave_ShouldSaveActor",
        "DestroyBeforeLoad",
        "RamaSave_OwningActorVarsToSave",
        "RamaSave_ComponentVarsToSave",
        "RamaSave_VerboseLog",
        "RamaSave_LogAllSavedComponentProperties",
        "RamaSave_SavePhysicsData",
        "LoadedGameVersion",
        "OwningActorTransform"
    };
}

/// A parsed DDS2 "_Progress.save".
public class SaveFileData
{
    public string Path { get; set; } = "";
    public int PackageVersionUE4 { get; set; }
    public int PackageVersionUE5 { get; set; }
    public string EngineVersion { get; set; } = "";
    public string EngineBranch { get; set; } = "";

    /// The "&lt;GUID&gt;=True" entries RamaSave writes ahead of the actor list.
    public List<string> Tags { get; } = new();

    public List<SaveActorRecord> Actors { get; } = new();

    public long CompressedBytes { get; set; }
    public long DecompressedBytes { get; set; }

    public int FullyParsedActors => Actors.Count(a => a.FullyParsed);
    public bool AllActorsParsed => Actors.Count > 0 && FullyParsedActors == Actors.Count;
    public int TotalProperties => Actors.Sum(a => a.Properties.Count);

    public string ParseSummary => Actors.Count == 0
        ? "No actor records found."
        : $"{Actors.Count:N0} actors, {TotalProperties:N0} values - "
          + (AllActorsParsed
              ? "all records verified against their own end offsets."
              : $"{FullyParsedActors:N0} of {Actors.Count:N0} records verified.");

    public string ActorSummary => string.Join(", ", Actors
        .GroupBy(a => a.ClassName)
        .OrderByDescending(g => g.Count())
        .Take(6)
        .Select(g => $"{g.Key} x{g.Count()}"));
}
