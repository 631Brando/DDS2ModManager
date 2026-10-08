using System.IO.Compression;

namespace DDS2ModManager.Tests;

/// Mod archives come from strangers. An entry named "..\..\something" must never be written outside
/// the folder it is being unpacked into ("zip slip") - the extractor is the one place every mod, UE4SS
/// release and UnrealModLoader release passes through, so it is pinned here rather than trusted.
public class ArchiveSafetyTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "dds_zipslip_" + Guid.NewGuid().ToString("N")[..8]);

    public void Dispose()
    {
        try { Directory.Delete(_root, true); } catch { }
    }

    [Theory]
    [InlineData("../escaped.txt")]
    [InlineData("../../escaped.txt")]
    [InlineData("inner/../../escaped.txt")]
    public void An_entry_that_climbs_out_of_the_destination_is_never_written(string entryName)
    {
        Directory.CreateDirectory(_root);
        var archive = Path.Combine(_root, "evil.zip");
        using (var zip = ZipFile.Open(archive, ZipArchiveMode.Create))
        {
            using (var w = new StreamWriter(zip.CreateEntry("ok/readme.txt").Open())) w.Write("fine");
            using (var w = new StreamWriter(zip.CreateEntry(entryName).Open())) w.Write("pwned");
        }

        var dest = Path.Combine(_root, "a", "b", "extract");
        try { ArchiveExtractionService.ExtractToDirectory(archive, dest); }
        catch { /* refusing the whole archive is an acceptable answer too */ }

        var everywhere = Directory.GetFiles(_root, "escaped.txt", SearchOption.AllDirectories);
        Assert.DoesNotContain(everywhere, f => !Path.GetFullPath(f).StartsWith(Path.GetFullPath(dest), StringComparison.OrdinalIgnoreCase));
    }
}
