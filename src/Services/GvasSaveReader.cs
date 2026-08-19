using System.Text;

namespace DDS2ModManager.Services;

/// Reads Unreal's standard GVAS save files.
///
/// Not everything a game writes is a RamaSave. DDS2 keeps its global options in
/// SaveGames\UserSettings.sav and each cartel's identity in CartelDefaults.sav, and both are
/// plain GVAS - Unreal's own SaveGame format, no custom container and no compression. They hold
/// genuinely useful things (graphics settings, achievement progress, the cartel's name), so
/// they're worth showing rather than hiding from the save list as unreadable.
///
/// The property list itself is walked by RamaSaveReader.ReadTaggedProperties: the same engine
/// build wrote both kinds of file, so the tag layout is identical and there's no reason to keep a
/// second copy of that logic. Only the header differs, and that's what this class handles.
///
/// Read-only, like everything else here.
public class GvasSaveReader
{
    private const string Magic = "GVAS";

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

    /// Returns the file as a single record named after the save class, so the inspector can show
    /// GVAS saves and RamaSave progress files through one UI.
    public SaveFileData? Read(string path)
    {
        try
        {
            var d = File.ReadAllBytes(path);
            if (d.Length < 32 || Encoding.ASCII.GetString(d, 0, 4) != Magic) return null;

            var pos = 4;
            var result = new SaveFileData
            {
                Path = path,
                Format = SaveFormat.Gvas,
                CompressedBytes = d.Length,
                DecompressedBytes = d.Length   // GVAS isn't compressed
            };

            // Save game file version. Version 3 is where Unreal added PackageVersionUE5; a version 2
            // file - which is every UE4 title, DDS1 included - simply has no such field. Reading one
            // anyway consumes the engine version instead, and every field after it desynchronises,
            // so the engine reads as garbage and the branch string comes out empty.
            var saveGameFileVersion = ReadInt32(d, ref pos);
            result.PackageVersionUE4 = ReadInt32(d, ref pos);
            result.PackageVersionUE5 = saveGameFileVersion >= 3 ? ReadInt32(d, ref pos) : 0;

            result.EngineVersion = $"{BitConverter.ToUInt16(d, pos)}.{BitConverter.ToUInt16(d, pos + 2)}.{BitConverter.ToUInt16(d, pos + 4)}";
            pos += 6;
            pos += 4;                                   // engine changelist
            result.EngineBranch = RamaSaveReader.ReadFString(d, ref pos) ?? "";

            pos += 4;                                   // custom version format
            var customVersions = ReadInt32(d, ref pos);
            if (customVersions is < 0 or > 10_000) return result;
            pos += customVersions * 20;                 // FGuid + int32 each
            if (pos < 0 || pos >= d.Length) return result;

            var saveClass = RamaSaveReader.ReadFString(d, ref pos);
            if (saveClass == null) return result;

            var record = new SaveActorRecord { ClassName = ShortClassName(saveClass) };
            RamaSaveReader.ReadTaggedProperties(d, ref pos, d.Length, record.Properties, 0);

            // GVAS ends with the terminating "None" plus a trailing int32, so landing within four
            // bytes of the end means the whole property list was consumed. Same idea as the
            // per-record end-offset check on progress saves: verified, not assumed.
            record.FullyParsed = pos >= d.Length - 4;
            result.Actors.Add(record);

            return result;
        }
        catch (Exception ex)
        {
            LoggingService.Instance.Warn($"Couldn't read '{Path.GetFileName(path)}': {ex.Message}");
            return null;
        }
    }

    /// "/Game/SaveGame/LocalSaved.LocalSaved_C" reads better as "LocalSaved_C".
    private static string ShortClassName(string path)
    {
        var dot = path.LastIndexOf('.');
        return dot >= 0 && dot < path.Length - 1 ? path[(dot + 1)..] : path;
    }

    private static int ReadInt32(byte[] b, ref int pos)
    {
        var v = BitConverter.ToInt32(b, pos);
        pos += 4;
        return v;
    }
}
