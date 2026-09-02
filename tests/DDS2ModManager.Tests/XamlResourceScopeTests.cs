using System.Text.RegularExpressions;

namespace DDS2ModManager.Tests;

/// Every {StaticResource} key a window uses has to be reachable from that window.
///
/// This is a runtime-only failure that a green build says nothing about: XAML resolves resources
/// when the window is constructed, so a key that exists in MainWindow but not in App.xaml compiles
/// perfectly and then throws "Provide value on StaticResourceHolder threw an exception" the first
/// time a *different* window opens. That is exactly how the UE4SS build picker failed - it used
/// BoolToVisibility, which lives in MainWindow's own resources rather than App.xaml.
///
/// Checked by reading the markup, because no test can construct a WPF Window without an
/// Application, and the whole point is to catch this before anyone clicks the button.
public class XamlResourceScopeTests
{
    private static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);

        while (dir != null && !File.Exists(Path.Combine(dir.FullName, "DDS2ModManager.sln")))
            dir = dir.Parent;

        return dir?.FullName ?? throw new InvalidOperationException("Couldn't find the solution root.");
    }

    private static IEnumerable<string> KeysIn(string xaml) =>
        Regex.Matches(xaml, @"x:Key=""([^""]+)""").Select(m => m.Groups[1].Value);

    private static IEnumerable<string> StaticResourcesUsedIn(string xaml) =>
        Regex.Matches(xaml, @"StaticResource\s+([A-Za-z0-9_]+)").Select(m => m.Groups[1].Value);

    [Fact]
    public void Every_window_can_reach_every_resource_it_asks_for()
    {
        var root = RepoRoot();
        var appXaml = File.ReadAllText(Path.Combine(root, "src", "App.xaml"));
        var globalKeys = KeysIn(appXaml).ToHashSet(StringComparer.Ordinal);

        // App.xaml is where the theme lives; if this is empty the test is checking nothing.
        Assert.NotEmpty(globalKeys);

        var windows = Directory
            .GetFiles(Path.Combine(root, "src", "Views"), "*.xaml")
            .Append(Path.Combine(root, "src", "MainWindow.xaml"))
            .ToList();

        Assert.NotEmpty(windows);

        var problems = new List<string>();

        foreach (var path in windows)
        {
            var xaml = File.ReadAllText(path);

            // A window's own <Window.Resources> are in scope for it, and nothing else's are.
            var reachable = globalKeys.Concat(KeysIn(xaml)).ToHashSet(StringComparer.Ordinal);

            foreach (var key in StaticResourcesUsedIn(xaml).Distinct())
            {
                if (!reachable.Contains(key))
                    problems.Add($"{Path.GetFileName(path)} uses {{StaticResource {key}}}, which it cannot reach");
            }
        }

        Assert.True(problems.Count == 0, string.Join(Environment.NewLine, problems));
    }
}
