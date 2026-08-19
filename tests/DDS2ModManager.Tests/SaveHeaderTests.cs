using System.Text;

namespace DDS2ModManager.Tests;

/// Reading the header of a save written by UE4 rather than UE5.
///
/// UE4 has no PackageVersionUE5 field, so every field from the engine version onward sits four
/// bytes earlier. Read a UE4 save with UE5's offsets and nothing throws - the engine version comes
/// out as nonsense, the branch string comes out empty, and the record walk that follows starts in
/// the wrong place. That is the failure mode worth testing: it produces confident wrong answers
/// rather than an error.
///
/// Synthetic bytes on purpose, so these pin the format rather than one machine's save folder.
public class SaveHeaderTests : IDisposable
{
    private readonly List<string> _temps = [];

    public void Dispose()
    {
        foreach (var t in _temps) { try { File.Delete(t); } catch { } }
    }

    private string WriteTemp(byte[] bytes)
    {
        var p = Path.Combine(Path.GetTempPath(), "dds_hdr_" + Guid.NewGuid().ToString("N")[..8] + ".save");
        File.WriteAllBytes(p, bytes);
        _temps.Add(p);
        return p;
    }

    /// Unreal's FString: length including the terminator, then the bytes, then a NUL.
    private static void FString(BinaryWriter w, string s)
    {
        w.Write(s.Length + 1);
        w.Write(Encoding.ASCII.GetBytes(s));
        w.Write((byte)0);
    }

    /// A RamaSave payload with no container tag, so it is read as-is rather than inflated.
    private static byte[] RamaPayload(bool ue5, string branch, ushort major, ushort minor, ushort patch)
    {
        var ms = new MemoryStream();
        var w = new BinaryWriter(ms);

        w.Write(0);              // payload size (unused by the reader)
        w.Write(7);              // format version
        w.Write(ue5 ? 522 : 517); // PackageVersionUE4
        if (ue5) w.Write(1009);   // PackageVersionUE5 - absent on UE4, which is the whole point

        w.Write(major); w.Write(minor); w.Write(patch);
        w.Write(4753647);        // engine changelist
        FString(w, branch);

        w.Write(0);              // tag count
        w.Write((byte)0);        // separator
        w.Write(0);              // actor count

        // The reader ignores anything under 64 bytes as too short to be a save.
        while (ms.Length < 96) w.Write((byte)0);
        return ms.ToArray();
    }

    private static byte[] GvasPayload(int saveGameFileVersion, string branch, ushort major, ushort minor, ushort patch)
    {
        var ms = new MemoryStream();
        var w = new BinaryWriter(ms);

        w.Write(Encoding.ASCII.GetBytes("GVAS"));
        w.Write(saveGameFileVersion);
        w.Write(saveGameFileVersion >= 3 ? 522 : 517);   // PackageVersionUE4
        if (saveGameFileVersion >= 3) w.Write(1009);     // PackageVersionUE5

        w.Write(major); w.Write(minor); w.Write(patch);
        w.Write(0);              // engine changelist
        FString(w, branch);

        w.Write(0);              // custom version format
        w.Write(0);              // custom version count
        FString(w, "SomeSaveGame");
        FString(w, "None");      // property terminator

        while (ms.Length < 96) w.Write((byte)0);
        return ms.ToArray();
    }

    // ---- RamaSave -----------------------------------------------------------------------------

    [Fact]
    public void A_ue4_rama_header_is_read_at_the_shifted_offsets()
    {
        var path = WriteTemp(RamaPayload(ue5: false, "++UE4+Release-4.21", 4, 21, 2));

        var r = new RamaSaveReader().Read(path);

        Assert.NotNull(r);
        Assert.Equal("4.21.2", r!.EngineVersion);
        Assert.Equal("++UE4+Release-4.21", r.EngineBranch);
        Assert.Equal(517, r.PackageVersionUE4);
        Assert.Equal(0, r.PackageVersionUE5);   // no such field on UE4
    }

    // The regression direction: DDS2 must keep reading exactly as it did.
    [Fact]
    public void A_ue5_rama_header_is_unchanged()
    {
        var path = WriteTemp(RamaPayload(ue5: true, "UE5", 5, 3, 2));

        var r = new RamaSaveReader().Read(path);

        Assert.NotNull(r);
        Assert.Equal("5.3.2", r!.EngineVersion);
        Assert.Equal("UE5", r.EngineBranch);
        Assert.Equal(522, r.PackageVersionUE4);
        Assert.Equal(1009, r.PackageVersionUE5);
    }

    // A UE4 patch number of 4 or 5 would satisfy an engine-major check at UE5's offset, so the
    // probe also has to see a branch string that names an engine before it accepts a layout.
    [Fact]
    public void A_ue4_patch_number_that_looks_like_an_engine_major_does_not_fool_the_probe()
    {
        var path = WriteTemp(RamaPayload(ue5: false, "++UE4+Release-4.21", 4, 21, 5));

        var r = new RamaSaveReader().Read(path);

        Assert.NotNull(r);
        Assert.Equal("4.21.5", r!.EngineVersion);
        Assert.Equal("++UE4+Release-4.21", r.EngineBranch);
    }

    // ---- GVAS ---------------------------------------------------------------------------------

    // Version 3 is where Unreal added PackageVersionUE5. A version 2 file has no such field, and
    // reading one consumes the engine version instead.
    [Fact]
    public void A_version_2_gvas_save_has_no_ue5_package_version()
    {
        var path = WriteTemp(GvasPayload(2, "++UE4+Release-4.21", 4, 21, 2));

        var r = new GvasSaveReader().Read(path);

        Assert.NotNull(r);
        Assert.Equal("4.21.2", r!.EngineVersion);
        Assert.Equal("++UE4+Release-4.21", r.EngineBranch);
        Assert.Equal(0, r.PackageVersionUE5);
    }

    [Fact]
    public void A_version_3_gvas_save_is_unchanged()
    {
        var path = WriteTemp(GvasPayload(3, "UE5", 5, 3, 2));

        var r = new GvasSaveReader().Read(path);

        Assert.NotNull(r);
        Assert.Equal("5.3.2", r!.EngineVersion);
        Assert.Equal("UE5", r.EngineBranch);
        Assert.Equal(1009, r.PackageVersionUE5);
    }

    // ---- save entry identity --------------------------------------------------------------------

    // RootName is empty for the primary root, so a single-root game's list is unchanged; a second
    // root shows its name, which is how a DDS1 player tells the slot index apart from a playthrough.
    [Fact]
    public void Group_display_falls_back_to_the_root_name()
    {
        Assert.Equal("", new SaveEntry().GroupDisplay);
        Assert.Equal("Serialized", new SaveEntry { RootName = "Serialized" }.GroupDisplay);
        Assert.Equal("Cartels", new SaveEntry { GroupName = "Cartels" }.GroupDisplay);

        // A container name still wins - it is the more specific of the two.
        Assert.Equal("Cartels", new SaveEntry { GroupName = "Cartels", RootName = "Serialized" }.GroupDisplay);
    }

    // Cloning a save is only meaningful when the game will actually load the copy.
    [Fact]
    public void Only_a_game_with_self_describing_saves_supports_cloning()
    {
        Assert.True(GameProfiles.Dds2.SupportsSaveCloning);
        Assert.False(GameProfiles.Dds1.SupportsSaveCloning);
    }
}
