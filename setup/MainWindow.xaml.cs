using System.Diagnostics;
using System.Windows;
using Microsoft.Win32;

namespace DDS2ModManagerSetup;

public partial class MainWindow : Window
{
    private const string RepoOwner = "631Brando";
    private const string RepoName = "DDS2ModManager";
    private const string AssetName = "DDS2ModManager.exe";
    private const string UninstallKeyName = "DDS2ModManager";

    private readonly DDS2ModManager.Services.GitHubReleaseService _github = new();

    public MainWindow()
    {
        InitializeComponent();
        InstallPathBox.Text = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Programs", "DDS2ModManager");
    }

    private void Browse_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFolderDialog { Title = "Choose an install folder" };
        if (dialog.ShowDialog() == true) InstallPathBox.Text = dialog.FolderName;
    }

    private void Cancel_Click(object sender, RoutedEventArgs e) => Close();

    private async void Install_Click(object sender, RoutedEventArgs e)
    {
        var installDir = InstallPathBox.Text.Trim();
        if (string.IsNullOrWhiteSpace(installDir))
        {
            MessageBox.Show("Choose an install location first.", "DDS2 Mod Manager Setup", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        InstallButton.IsEnabled = false;
        CancelButton.IsEnabled = false;
        Progress.Visibility = Visibility.Visible;

        try
        {
            StatusText.Text = "Checking the latest release on GitHub...";
            var release = await _github.GetLatestReleaseAsync(RepoOwner, RepoName);
            if (release == null)
            {
                Fail($"Couldn't reach GitHub, or {RepoOwner}/{RepoName} has no releases yet. Check your internet connection and " +
                     $"that a release exists at github.com/{RepoOwner}/{RepoName}/releases.");
                return;
            }

            var asset = release.Assets.FirstOrDefault(a => a.Name.Equals(AssetName, StringComparison.OrdinalIgnoreCase));
            if (asset == null)
            {
                Fail($"The latest release ({release.TagName}) doesn't have a \"{AssetName}\" asset. " +
                     "This installer expects that exact file name - if you built this yourself, check the release workflow.");
                return;
            }

            Directory.CreateDirectory(installDir);
            var exeDestination = Path.Combine(installDir, AssetName);

            StatusText.Text = $"Downloading {release.TagName} ({asset.Size / 1024.0 / 1024.0:F1} MB)...";
            var tempPath = exeDestination + ".download";
            await _github.DownloadAssetAsync(asset.BrowserDownloadUrl, tempPath, new Progress<double>(p => Progress.Value = p));

            // Swap in the freshly-downloaded exe. If a previous copy is running, this overwrite
            // will fail with a locked-file error - that's a reasonable thing to surface as-is
            // rather than silently working around, since it means the app needs to be closed first.
            File.Move(tempPath, exeDestination, true);

            StatusText.Text = "Setting up shortcuts...";
            if (DesktopShortcutBox.IsChecked == true)
            {
                var desktopPath = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory), "DDS2 Mod Manager.lnk");
                DDS2ModManager.Services.ShortcutCreator.Create(desktopPath, exeDestination, "DDS2 Mod Manager");
            }
            if (StartMenuShortcutBox.IsChecked == true)
            {
                var startMenuPath = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.StartMenu), "Programs", "DDS2 Mod Manager.lnk");
                DDS2ModManager.Services.ShortcutCreator.Create(startMenuPath, exeDestination, "DDS2 Mod Manager");
            }

            WriteUninstallEntry(installDir, exeDestination, release.TagName);

            StatusText.Text = $"Installed {release.TagName} to {installDir}.";
            DDS2ModManager.Services.LoggingService.Instance.Success($"Installed DDS2 Mod Manager {release.TagName} to {installDir}.");

            if (LaunchAfterInstallBox.IsChecked == true)
                Process.Start(new ProcessStartInfo { FileName = exeDestination, WorkingDirectory = installDir, UseShellExecute = true });

            MessageBox.Show($"DDS2 Mod Manager {release.TagName} is installed.", "Setup Complete", MessageBoxButton.OK, MessageBoxImage.Information);
            Close();
        }
        catch (Exception ex)
        {
            Fail($"Install failed: {ex.Message}");
        }
        finally
        {
            InstallButton.IsEnabled = true;
            CancelButton.IsEnabled = true;
        }
    }

    private void Fail(string message)
    {
        StatusText.Text = message;
        Progress.Visibility = Visibility.Collapsed;
        DDS2ModManager.Services.LoggingService.Instance.Error(message);
        MessageBox.Show(message, "DDS2 Mod Manager Setup", MessageBoxButton.OK, MessageBoxImage.Error);
    }

    /// Registers in Windows "Apps & Features" / "Add or Remove Programs", HKCU only (no admin
    /// needed). UninstallString points back at the installed exe with --uninstall, which the main
    /// app handles by running the same removal flow as Settings' own "Uninstall" button.
    private void WriteUninstallEntry(string installDir, string exePath, string version)
    {
        using var key = Registry.CurrentUser.CreateSubKey(
            $@"Software\Microsoft\Windows\CurrentVersion\Uninstall\{UninstallKeyName}");
        key.SetValue("DisplayName", "DDS2 Mod Manager");
        key.SetValue("DisplayVersion", version.TrimStart('v', 'V'));
        key.SetValue("Publisher", "631Brando");
        key.SetValue("InstallLocation", installDir);
        key.SetValue("DisplayIcon", exePath);
        key.SetValue("UninstallString", $"\"{exePath}\" --uninstall");
        key.SetValue("NoModify", 1, RegistryValueKind.DWord);
        key.SetValue("NoRepair", 1, RegistryValueKind.DWord);
    }
}
