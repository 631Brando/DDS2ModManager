using System.Reflection;
using CUE4Parse.FileProvider;
using CUE4Parse.UE4.Assets.Exports.Engine;
using CUE4Parse.UE4.Kismet;
using CUE4Parse.UE4.Objects.UObject;

namespace DDS2ModManager.Services;

/// One AppendDataTables call found in a mod's ModActor: the mod merges its own table (Source)
/// into a base-game table (Target) at runtime.
public class DataTableAppend
{
    public string ModName { get; set; } = "";
    public string TargetPath { get; set; } = "";
    public string SourcePath { get; set; } = "";

    public string TargetName => LastSegment(TargetPath);
    public string SourceName => LastSegment(SourcePath);

    /// Row keys the mod contributes. These are what actually collide between two mods - two mods
    /// appending into the same table are only in conflict if these sets intersect.
    public List<string> SourceRows { get; set; } = new();

    /// Rows the mod contributes that already exist in the base-game table, i.e. rows it replaces
    /// rather than adds. Worth surfacing on its own: it's how a logic mod rebalances the game.
    public List<string> OverriddenBaseRows { get; set; } = new();

    private static string LastSegment(string path)
    {
        var afterSlash = path.Contains('/') ? path[(path.LastIndexOf('/') + 1)..] : path;
        return afterSlash.Contains('.') ? afterSlash[..afterSlash.IndexOf('.')] : afterSlash;
    }
}

/// Reads what a LogicMod actually does to the game's DataTables.
///
/// LogicMods don't ship base-game assets - they ship their own tables and merge them into the
/// real ones at runtime, from Blueprint logic inside their ModActor. That means file-path
/// comparison (which is all a normal conflict check can do) sees two LogicMods as completely
/// unrelated even when they're both rewriting the same balance values.
///
/// This resolves that by reading the ModActor's compiled Blueprint bytecode, finding the
/// AppendDataTables(target, source) calls, and pulling the row keys out of both tables. Conflict
/// detection can then work at row level, which is the granularity that actually matters: two mods
/// appending to the same table are fine as long as they don't touch the same rows.
public class DataTableAppendScanner
{
    /// UStruct.Deserialize skips Blueprint bytecode entirely unless the provider opts in, so
    /// without this every ModActor comes back with ScriptBytecode == null and no appends are found.
    public static void EnableScriptReading(AbstractFileProvider provider) => provider.ReadScriptData = true;

    /// modAssetPaths is the mod's own ContainedAssetPaths (as captured at install time), used to
    /// locate its ModActor. Returns an empty list for mods that have no ModActor (patch mods) or
    /// whose bytecode couldn't be read - callers treat that as "no row info", not as an error.
    public List<DataTableAppend> Scan(AbstractFileProvider provider, string modName, IEnumerable<string> modAssetPaths)
    {
        var results = new List<DataTableAppend>();

        var modActorPath = modAssetPaths.FirstOrDefault(p =>
            Path.GetFileNameWithoutExtension(p).Equals("ModActor", StringComparison.OrdinalIgnoreCase));
        if (modActorPath == null) return results;

        try
        {
            var pkg = provider.LoadPackage(modActorPath);
            var pairs = new List<(string Target, string Source)>();

            foreach (var export in pkg.GetExports())
            {
                if (export is not UStruct us || us.ScriptBytecode is not { Length: > 0 }) continue;
                var seen = new HashSet<object>(ReferenceEqualityComparer.Instance);
                foreach (var expr in us.ScriptBytecode) Walk(expr, seen, pairs);
            }

            foreach (var (target, source) in pairs.Distinct())
            {
                var append = new DataTableAppend
                {
                    ModName = modName,
                    TargetPath = target,
                    SourcePath = source,
                    SourceRows = ReadRows(provider, source)
                };

                var targetRows = ReadRows(provider, target);
                append.OverriddenBaseRows = append.SourceRows
                    .Intersect(targetRows, StringComparer.OrdinalIgnoreCase)
                    .ToList();

                results.Add(append);
            }
        }
        catch (Exception ex)
        {
            LoggingService.Instance.Warn($"Couldn't read DataTable appends from '{modName}': {ex.Message}");
        }

        return results;
    }

    /// Blueprint bytecode is a tree whose node types each expose their children on differently
    /// named fields. Rather than hand-coding a case for every one of the ~90 EX_* token types
    /// (and silently missing appends nested inside whichever ones we forgot), this follows any
    /// public field that is or contains a KismetExpression.
    private static void Walk(object? node, HashSet<object> seen, List<(string, string)> sink)
    {
        if (node == null || !seen.Add(node)) return;

        if (node is EX_CallMath call &&
            (call.StackNode?.Name?.Contains("AppendDataTable", StringComparison.OrdinalIgnoreCase) ?? false))
        {
            // Parameter order matches the game's AppendDataTables(target, source, bool) helper:
            // first object constant is the base-game table being merged into, second is the
            // mod's own table supplying the rows.
            var objects = call.Parameters
                .OfType<EX_ObjectConst>()
                .Select(p => p.Value?.ResolvedObject?.GetPathName())
                .Where(s => !string.IsNullOrEmpty(s))
                .ToList();

            if (objects.Count >= 2) sink.Add((objects[0]!, objects[1]!));
        }

        foreach (var field in node.GetType().GetFields(BindingFlags.Public | BindingFlags.Instance))
        {
            object? value;
            try { value = field.GetValue(node); }
            catch { continue; }

            if (value is KismetExpression child) Walk(child, seen, sink);
            else if (value is KismetExpression[] children)
                foreach (var c in children) Walk(c, seen, sink);
        }
    }

    private static List<string> ReadRows(AbstractFileProvider provider, string objectPath)
    {
        // Object paths are "<package>.<object>"; the provider loads packages, not objects.
        var packagePath = objectPath.Contains('.') ? objectPath[..objectPath.LastIndexOf('.')] : objectPath;
        try
        {
            if (!provider.TryLoadPackage(packagePath, out var pkg)) return new List<string>();
            var table = pkg.GetExports().OfType<UDataTable>().FirstOrDefault();
            return table?.RowMap?.Keys.Select(k => k.Text).ToList() ?? new List<string>();
        }
        catch
        {
            return new List<string>();
        }
    }
}
