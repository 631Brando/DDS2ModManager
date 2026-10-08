// Renders the real MainWindow and GameSelectorWindow XAML to PNGs, offscreen, against this PC's
// actual game catalog - for checking a UI change without driving the app or taking over the screen.
//
//   dotnet run --project tools/RenderHarness [output folder]
//
// Then open the PNGs. It prints any WPF binding or resource error raised while rendering, which is
// the only place those show up: a misspelled path or a converter given the wrong type fails in
// silence in the real app. Three transient "Cannot find governing FrameworkElement ... Path=Capsule"
// lines per rendered card are expected - WPF retries an ImageBrush binding once the brush is
// attached to its Border, and the art does render.
//
// Two things it must keep doing:
//
// - Never construct DDS2ModManager.App. WPF's Application constructor queues OnStartup on the
//   dispatcher, so the first message loop - Dispatcher.Run here, not only Application.Run - would
//   open the real main window and run the app's whole startup: update checks, Nexus requests,
//   settings writes. A plain Application gets App.xaml's dictionary instead, read as loose XAML.
// - Leave the developer's settings as it found them. The mod grid saves its sort order whenever the
//   grid is rebuilt, and every render rebuilds it, so settings.json is snapshotted and restored.
using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Documents;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using DDS2ModManager.Models;
using DDS2ModManager.Services;
using DDS2ModManager.ViewModels;
using DDS2ModManager.Views;

internal static class Program
{
    private static readonly List<string> TraceLines = new();
    private static string _out = "";

    [STAThread]
    private static int Main(string[] args)
    {
        _out = args.Length > 0 ? Path.GetFullPath(args[0]) : Path.Combine(Path.GetTempPath(), "DDS2MM-renders");
        Directory.CreateDirectory(_out);

        var settingsSnapshot = File.Exists(AppPaths.Settings) ? File.ReadAllBytes(AppPaths.Settings) : null;
        try
        {
            return Run();
        }
        finally
        {
            if (settingsSnapshot != null) File.WriteAllBytes(AppPaths.Settings, settingsSnapshot);
        }
    }

    private static int Run()
    {
        PresentationTraceSources.Refresh();
        PresentationTraceSources.DataBindingSource.Listeners.Add(new Capture("binding"));
        PresentationTraceSources.DataBindingSource.Switch.Level = SourceLevels.Warning;
        PresentationTraceSources.ResourceDictionarySource.Listeners.Add(new Capture("resource"));
        PresentationTraceSources.ResourceDictionarySource.Switch.Level = SourceLevels.Warning;

        var app = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
        app.Resources = LoadAppResources();

        var dispatcher = Dispatcher.CurrentDispatcher;
        SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext(dispatcher));
        var exit = 0;
        dispatcher.InvokeAsync(async () =>
        {
            try { await RenderAllAsync(); }
            catch (Exception ex) { Console.WriteLine(ex); exit = 1; }
            finally { dispatcher.InvokeShutdown(); }
        });
        Dispatcher.Run();

        Console.WriteLine($"\nRenders: {_out}");
        Console.WriteLine($"WPF trace messages: {TraceLines.Count}");
        foreach (var line in TraceLines.Distinct()) Console.WriteLine("  " + line);
        return exit;
    }

    /// App.xaml's Application.Resources dictionary, found by walking up from the build output.
    private static ResourceDictionary LoadAppResources()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null && !File.Exists(Path.Combine(dir.FullName, "src", "App.xaml"))) dir = dir.Parent;
        if (dir == null) throw new FileNotFoundException("Couldn't find src\\App.xaml above " + AppContext.BaseDirectory);

        var xaml = File.ReadAllText(Path.Combine(dir.FullName, "src", "App.xaml"));
        var from = xaml.IndexOf("<ResourceDictionary>", StringComparison.Ordinal);
        var to = xaml.LastIndexOf("</ResourceDictionary>", StringComparison.Ordinal) + "</ResourceDictionary>".Length;
        return (ResourceDictionary)System.Windows.Markup.XamlReader.Parse(xaml[from..to].Replace("<ResourceDictionary>",
            "<ResourceDictionary xmlns=\"http://schemas.microsoft.com/winfx/2006/xaml/presentation\" " +
            "xmlns:x=\"http://schemas.microsoft.com/winfx/2006/xaml\">"));
    }

    private sealed class Capture(string kind) : TraceListener
    {
        private readonly System.Text.StringBuilder _line = new();
        public override void Write(string? message) => _line.Append(message);
        public override void WriteLine(string? message)
        {
            _line.Append(message);
            TraceLines.Add($"[{kind}] {_line}");
            _line.Clear();
        }
    }

    private static async Task RenderAllAsync()
    {
        var vm = new MainViewModel();
        await vm.RefreshCatalogAsync();

        Console.WriteLine("Catalog:");
        foreach (var e in vm.GameEntries)
            Console.WriteLine($"  {e.DisplayName,-34} {e.CardSubtitle,-30} full={e.IsFullSupport,-5} forget={e.CanForget}");

        foreach (var e in vm.GameEntries) await e.LoadArtAsync(includeHero: true);

        var builtIn = vm.GameEntries.FirstOrDefault(e => e.IsFullSupport && e.IsInstalled);
        var generic = vm.GameEntries.FirstOrDefault(e => !e.IsFullSupport && e.IsInstalled);

        foreach (var (entry, name) in new[] { (builtIn, "builtin"), (generic, "generic") })
        {
            if (entry == null) continue;
            Activate(vm, entry);
            await RenderMainAsync(vm, 1760, 980, $"main-{name}.png");
        }

        Activate(vm, builtIn ?? generic);
        await RenderMainAsync(vm, 980, 600, "main-minsize.png");

        Activate(vm, null);
        await RenderMainAsync(vm, 1760, 980, "main-nogame.png");

        // The UE4SS install dialog, for the cases its wording differs on: DDS2 (stable named as a crash),
        // a generic UE4 game, and a UE5.5 game past stable's range.
        await RenderUe4ssDialogAsync(GameProfiles.Dds2, "ue4ss-dialog-dds2.png");
        if (generic != null) await RenderUe4ssDialogAsync(generic.Game.ToInstallation().Profile, "ue4ss-dialog-generic.png");
        var ue55 = (CUE4Parse.UE4.Versions.EGame)((5 << 24) | (5 << 16));
        await RenderUe4ssDialogAsync(GameProfiles.Dds2 with
        {
            Id = "steam:0", IsBuiltIn = false, UE4SSStableCaveat = null, EngineVersion = ue55, EngineLabel = "UE 5.5",
            InstallableLoaders = LoaderCompatibility.InstallableFor(ue55)
        }, "ue4ss-dialog-ue55.png");

        Activate(vm, builtIn ?? generic);
        await RenderPickerAsync(vm, "picker.png", _ => { });
        await RenderPickerAsync(vm, "picker-tall-leftovers.png",
            p => ((Button)p.FindName("LeftoverToggle")).RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent)), height: 1500);
        await RenderPickerAsync(vm, "picker-search-nomatch.png", p => ((TextBox)p.FindName("SearchBox")).Text = "zzzz");
    }

    /// What MarkActiveEntry and SetupForGameAsync leave behind for the header, minus the setup -
    /// no paks are mounted and no mods are scanned.
    private static void Activate(MainViewModel vm, GameEntryViewModel? entry)
    {
        vm.Game = entry?.Game.ToInstallation();
        vm.GamePathDisplay = entry?.RootPath ?? "";
        foreach (var e in vm.GameEntries) e.IsActive = e == entry;
        vm.ActiveEntry = entry;

        // The loader cards' statuses - read from disk, as setup would, without the rest of setup.
        vm.Ue4ssStatus = vm.Game == null ? null : new UE4SSManagerService().GetCurrentStatus(vm.Game);
        vm.UmlStatus = vm.Game == null ? null : UnrealModLoaderService.GetStatus(vm.Game);
    }

    private static async Task RenderUe4ssDialogAsync(GameProfile profile, string file)
    {
        var dialog = new UE4SSBuildSelectionWindow(profile, preferDev: false, installedChannel: UE4SSChannel.Experimental);
        var content = (FrameworkElement)dialog.Content;
        dialog.Content = null;

        var host = new Border { Background = dialog.Background, Child = content, Resources = dialog.Resources };
        host.SetValue(TextElement.FontFamilyProperty, dialog.FontFamily);
        host.SetValue(TextElement.ForegroundProperty, dialog.Foreground);

        host.Measure(new Size(dialog.Width, double.PositiveInfinity));
        await RenderAsync(host, dialog.Width, Math.Ceiling(host.DesiredSize.Height), file);
    }

    private static async Task RenderMainAsync(MainViewModel vm, double w, double h, string file)
    {
        // The window is built for its XAML and never shown, so its Loaded handler - the app's
        // startup - never runs. Its content is moved into a host carrying the window's resources
        // and inherited text properties.
        var window = new DDS2ModManager.MainWindow();
        var content = (FrameworkElement)window.Content;
        window.Content = null;

        var host = new Border { Background = window.Background, Child = content, DataContext = vm, Resources = window.Resources };
        host.SetValue(TextElement.FontFamilyProperty, window.FontFamily);
        host.SetValue(TextElement.ForegroundProperty, window.Foreground);
        await RenderAsync(host, w, h, file);
    }

    private static async Task RenderPickerAsync(MainViewModel vm, string file, Action<GameSelectorWindow> setup, double? height = null)
    {
        var picker = new GameSelectorWindow(vm);
        var content = (FrameworkElement)picker.Content;
        picker.Content = null;

        // Roughly the main window behind it, so the transparent shadow margin reads as it would.
        var host = new Border { Background = new SolidColorBrush(Color.FromRgb(0x0C, 0x0D, 0x12)), Child = content, Resources = picker.Resources };
        host.SetValue(TextElement.FontFamilyProperty, picker.FontFamily);
        host.SetValue(TextElement.ForegroundProperty, picker.Foreground);

        setup(picker);
        await RenderAsync(host, picker.Width, height ?? picker.Height, file);
    }

    private static async Task RenderAsync(Border host, double w, double h, string file)
    {
        var size = new Size(w, h);
        for (var pass = 0; pass < 3; pass++)
        {
            host.Measure(size);
            host.Arrange(new Rect(size));
            host.UpdateLayout();
            for (var i = 0; i < 4; i++) await Dispatcher.Yield(DispatcherPriority.Background);
        }

        var bitmap = new RenderTargetBitmap((int)w, (int)h, 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(host);
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));
        await using (var fs = File.Create(Path.Combine(_out, file))) encoder.Save(fs);
        Console.WriteLine("wrote " + file);

        host.Child = null;
    }
}
