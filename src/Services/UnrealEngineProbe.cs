using System.Text;
using CUE4Parse.UE4.Versions;

namespace DDS2ModManager.Services;

/// Which Unreal Engine version a game was built with, and how sure we are.
public sealed record EngineGuess(EGame Game, string Label, bool IsExact, string Evidence)
{
    public bool IsUe5 => (int)Game >= (int)EGame.GAME_UE5_0;
}

/// Works out an unknown game's Unreal Engine version from its files.
///
/// CUE4Parse needs an EGame to deserialise assets, and for DDS1 and DDS2 that is written into
/// their profiles. For any other game it has to come from the install, and no single signal is
/// reliable on its own, so they are tried strongest first:
///
///   1. The build string compiled into the executable, e.g. "++UE4+Release-4.21". Exact - this is
///      how DDS1 was pinned to 4.21 when every other artifact in its folder claimed 4.27. But
///      studios can strip it, and Mordhau's is gone.
///   2. The base pak's format version, read from its footer. Each version was introduced by a
///      specific engine release, so it narrows the answer to a range - cheap, and always present.
///   3. The IoStore table-of-contents version, which splits ranges the pak version can't
///      (4.27 and UE5.0-5.2 share pak v11; their .utoc files differ).
///
/// Anything short of (1) is labelled an estimate. A wrong EGame still LISTS every path in a pak -
/// the index is version-agnostic - so mod type and conflict detection keep working; only reading
/// values out of assets fails. That is why a guess is acceptable here and the user can override it
/// in Settings.
public static class UnrealEngineProbe
{
    /// FPakInfo::PakFile_Magic, the marker in every .pak footer.
    public const uint PakMagic = 0x5A6F12E1;

    /// FIoStoreTocHeader::TocMagicImg - the 16 bytes every .utoc begins with.
    private static readonly byte[] TocMagic = Encoding.ASCII.GetBytes("-==--==--==--==-");

    /// The pak format version from a .pak's footer, or null if the file isn't a readable pak.
    ///
    /// The footer layout grew over the years (an encryption GUID in v7, compression names in v8),
    /// so rather than computing an offset per version this finds the magic in the tail and reads
    /// the int32 after it. Searched from the end backwards so the real footer wins over any
    /// coincidental byte pattern earlier in the file.
    public static int? ReadPakVersion(string pakPath)
    {
        try
        {
            using var fs = new FileStream(pakPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            var tail = (int)Math.Min(1024, fs.Length);
            if (tail < 8) return null;

            fs.Seek(-tail, SeekOrigin.End);
            var buf = new byte[tail];
            fs.ReadExactly(buf, 0, tail);

            for (var i = tail - 8; i >= 0; i--)
            {
                if (BitConverter.ToUInt32(buf, i) != PakMagic) continue;
                var version = BitConverter.ToInt32(buf, i + 4);
                if (version is >= 1 and <= 32) return version;
            }
        }
        catch
        {
            // Locked, missing or not a pak - no evidence rather than an error.
        }

        return null;
    }

    /// The IoStore TOC version from a .utoc header, or null if the file isn't a readable .utoc.
    public static int? ReadTocVersion(string utocPath)
    {
        try
        {
            using var fs = new FileStream(utocPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            var header = new byte[TocMagic.Length + 1];
            if (fs.Read(header, 0, header.Length) < header.Length) return null;
            if (!header.AsSpan(0, TocMagic.Length).SequenceEqual(TocMagic)) return null;

            var version = header[TocMagic.Length];
            return version is >= 1 and <= 32 ? version : null;
        }
        catch
        {
            return null;
        }
    }

    /// The engine version from the build string compiled into an executable, or null.
    ///
    /// Unreal stores it as UTF-16 ("++UE4+Release-4.21-CL-4753647"), so this streams the file
    /// looking for the UTF-16 bytes of "++UE" rather than loading a 50-200 MB executable whole.
    /// Chunks overlap by more than the longest string being read, so a match straddling a chunk
    /// boundary is still found.
    public static (int Major, int Minor)? ReadExeBuildVersion(string exePath)
    {
        var needle = Encoding.Unicode.GetBytes("++UE");
        const int chunk = 4 * 1024 * 1024;
        const int overlap = 128;

        try
        {
            using var fs = new FileStream(exePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite,
                bufferSize: 1 << 16, FileOptions.SequentialScan);

            var buf = new byte[chunk + overlap];
            var carried = 0;

            while (true)
            {
                var read = fs.Read(buf, carried, chunk);
                if (read <= 0) break;
                var len = carried + read;

                var span = buf.AsSpan(0, len);
                var at = 0;
                while (true)
                {
                    var hit = span[at..].IndexOf(needle);
                    if (hit < 0) break;
                    hit += at;

                    var end = Math.Min(len, hit + 96);
                    var text = Encoding.Unicode.GetString(buf, hit, (end - hit) & ~1);
                    var parsed = ParseBuildString(text);
                    if (parsed != null) return parsed;

                    at = hit + needle.Length;
                }

                // Keep the tail so a string split across the boundary is seen whole next time.
                carried = Math.Min(overlap, len);
                Buffer.BlockCopy(buf, len - carried, buf, 0, carried);
            }
        }
        catch
        {
            // Unreadable executable - fall back to the container formats.
        }

        return null;
    }

    /// "++UE5+Release-5.3-CL-27405482" -> (5, 3). Public for tests.
    public static (int Major, int Minor)? ParseBuildString(string text)
    {
        var m = System.Text.RegularExpressions.Regex.Match(text, @"\+\+UE(?<maj>[45])\+Release-(?<v>\d+)\.(?<min>\d+)");
        if (!m.Success) return null;

        var major = int.Parse(m.Groups["v"].Value);
        var minor = int.Parse(m.Groups["min"].Value);

        // The "++UE5" prefix and the release number have to agree, or this isn't the build string.
        return major.ToString() == m.Groups["maj"].Value ? (major, minor) : null;
    }

    /// The nearest EGame CUE4Parse actually defines at or below major.minor.
    ///
    /// Going down rather than up is deliberate: an engine newer than this CUE4Parse knows about is
    /// read with the newest rules it has, which is the best available answer, while rounding UP
    /// would claim features the game doesn't have.
    public static EGame ToEGame(int major, int minor)
    {
        var wanted = (major << 24) | (minor << 16);
        var best = EGame.GAME_UE4_0;

        foreach (var value in Enum.GetValues<EGame>())
        {
            var name = value.ToString();
            if (!name.StartsWith("GAME_UE", StringComparison.Ordinal)) continue;
            if (name.EndsWith("_LATEST", StringComparison.Ordinal) || name.EndsWith("_Plus", StringComparison.Ordinal)
                || name.EndsWith("_EA", StringComparison.Ordinal)) continue;

            var v = (int)value;
            if (v <= wanted && v > (int)best) best = value;
        }

        return best;
    }

    public static EngineGuess FromBuildString(int major, int minor) =>
        new(ToEGame(major, minor), $"UE {major}.{minor}", IsExact: true,
            Evidence: "the build string in the game's executable");

    /// A best guess from container formats alone. Ranges come from when each format version was
    /// introduced; within a range CUE4Parse's readers are forgiving, so the newest member is used.
    public static EngineGuess FromContainers(int? pakVersion, int? tocVersion, bool usesIoStore)
    {
        // IoStore TOC versions split what the pak version can't.
        if (usesIoStore && tocVersion is { } toc)
        {
            switch (toc)
            {
                case <= 2: return Estimate(4, 26, "UE 4.26", $"IoStore TOC v{toc}");
                case 3:    return Estimate(4, 27, "UE 4.27 or 5.0", $"IoStore TOC v{toc}");
                case 4 or 5: return Estimate(5, 1, "UE 5.0–5.2", $"IoStore TOC v{toc}");
                case 6:    return Estimate(5, 3, "UE 5.3", $"IoStore TOC v{toc}");
                case 7:    return Estimate(5, 4, "UE 5.4", $"IoStore TOC v{toc}");
                default:   return Estimate(5, 5, "UE 5.5 or newer", $"IoStore TOC v{toc}");
            }
        }

        if (pakVersion is { } pak)
        {
            var evidence = $"pak format v{pak}";
            return pak switch
            {
                <= 3 => Estimate(4, 15, "UE 4.15 or older", evidence),
                4    => Estimate(4, 19, "UE 4.16–4.19", evidence),
                5    => Estimate(4, 20, "UE 4.20", evidence),
                6 or 7 => Estimate(4, 21, "UE 4.21", evidence),
                8    => Estimate(4, 24, "UE 4.22–4.24", evidence),
                9    => Estimate(4, 25, "UE 4.25", evidence),
                10   => Estimate(4, 26, "UE 4.26", evidence),
                11   => usesIoStore ? Estimate(5, 1, "UE 5.0–5.2", evidence) : Estimate(4, 27, "UE 4.27", evidence),
                12   => Estimate(5, 3, "UE 5.3", evidence),
                _    => Estimate(5, 4, "UE 5.4 or newer", evidence)
            };
        }

        // Nothing readable at all. UE4.27 is the most common engine in the wild and the least
        // likely to misread a UE4 game badly, and the label says plainly that it's a guess.
        return new EngineGuess(EGame.GAME_UE4_27, "Unknown engine version", IsExact: false,
            Evidence: "no readable pak or executable");
    }

    private static EngineGuess Estimate(int major, int minor, string label, string evidence) =>
        new(ToEGame(major, minor), label, IsExact: false, Evidence: evidence);

    /// Everything above, applied to one install. Reading the executable is the slow step (up to a
    /// second on a large game), so callers scanning many games can skip it and run it only for the
    /// game the user actually opens.
    public static EngineGuess Probe(string projectPath, bool readExecutable)
    {
        if (readExecutable)
        {
            var exe = UnrealInstallInspector.FindGameExecutable(projectPath);
            if (exe != null && ReadExeBuildVersion(exe) is { } v)
                return FromBuildString(v.Major, v.Minor);
        }

        var paks = Path.Combine(projectPath, "Content", "Paks");
        var usesIoStore = UnrealInstallInspector.UsesIoStore(paks);

        var mainPak = UnrealInstallInspector.FindMainPak(paks);
        var pakVersion = mainPak == null ? null : ReadPakVersion(mainPak);

        int? tocVersion = null;
        if (usesIoStore)
        {
            var utoc = UnrealInstallInspector.FindBaseContainers(paks)
                .FirstOrDefault(f => f.EndsWith(".utoc", StringComparison.OrdinalIgnoreCase));
            if (utoc != null) tocVersion = ReadTocVersion(utoc);
        }

        return FromContainers(pakVersion, tocVersion, usesIoStore);
    }
}
