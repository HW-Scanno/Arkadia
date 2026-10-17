using System;
using System.IO;
using System.Threading.Tasks;
using Arkadia.Data;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.Platform.Storage;

namespace Arkadia;

public partial class ScreenScraperCacheManagerDialog : Window
{
    private readonly CatalogService _catalog;
    private ScreenScraperCachePackageRecord? _selected;
    private bool _registering;   // one registration at a time; the other actions are disabled and the window cannot close meanwhile

    public ScreenScraperCacheManagerDialog() : this(null!) { }

    public ScreenScraperCacheManagerDialog(CatalogService catalog)
    {
        InitializeComponent();
        _catalog = catalog;
    }

    protected override void OnLoaded(RoutedEventArgs e)
    {
        base.OnLoaded(e);
        RefreshList();
    }

    // ── List management ───────────────────────────────────────────────────────

    private void RefreshList()
    {
        var packages = _catalog.LoadCachePackages();
        PackageList.ItemsSource  = packages;
        _selected                = null;
        PackageList.SelectedItem = null;
        EmptyMsg.IsVisible       = packages.Count == 0;
        UpdateButtons();
        HideValidation();
    }

    private void OnSelectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        _selected = PackageList.SelectedItem as ScreenScraperCachePackageRecord;
        UpdateButtons();
    }

    private void UpdateButtons()
    {
        VerifyBtn.IsEnabled = !_registering && _selected is { Status: "Available" };
        DetachBtn.IsEnabled = !_registering && _selected is not null;
        DeleteBtn.IsEnabled = !_registering && _selected is { Status: "Available" };
        RegisterBtn.IsEnabled = !_registering;
        RefreshBtn.IsEnabled  = !_registering;
    }

    // ── Actions ───────────────────────────────────────────────────────────────

    private async void OnVerifyPackage(object? sender, RoutedEventArgs e)
    {
        if (_selected is null || _selected.IsMissing) return;

        VerifyBtn.IsEnabled = false;
        var prevContent     = (string)VerifyBtn.Content!;
        VerifyBtn.Content   = "Verifying…";

        CachePackageVerificationResult result;
        try
        {
            var verifier = new ScreenScraperCachePackageVerifier(_catalog);
            result = await Task.Run(() => Arkadia.Activities.ActivityMonitor.Shared.Run(
                Arkadia.Activities.ActivityKind.Verifying, () => verifier.Verify(_selected.Id), "verify cache package"));
        }
        catch (Exception ex)
        {
            ShowValidation($"Verification failed: {ex.Message}");
            VerifyBtn.Content = prevContent;
            UpdateButtons();
            return;
        }

        VerifyBtn.Content = prevContent;
        UpdateButtons();

        var dialog = new ScreenScraperCachePackageVerifyDialog(result);
        await dialog.ShowDialog(this);
    }

    private async void OnRegisterPackage(object? sender, RoutedEventArgs e)
    {
        var files = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title         = "Select ScreenScraper Cache ZIP Package",
            AllowMultiple = false,
            FileTypeFilter =
            [
                new FilePickerFileType("ZIP Archive") { Patterns = ["*.zip"] },
                new FilePickerFileType("All Files")   { Patterns = ["*.*"]   },
            ],
        });
        if (files.Count == 0) return;
        if (files[0].TryGetLocalPath() is not string path) return;

        if (_registering) return;
        _registering = true;
        UpdateButtons();

        try
        {
            var importer = new ScreenScraperCachePackageImporter(_catalog);
            // Off the UI thread (same pattern as OnVerifyPackage) so the IMPORTING animation keeps running.
            var result   = await Task.Run(() => Arkadia.Activities.ActivityMonitor.Shared.Run(
                Arkadia.Activities.ActivityKind.Importing, () => importer.IndexPackage(path), "register cache package"));
            _registering = false;
            if (result.WasAlreadyIndexed)
                ShowValidation("Package already registered.", isError: false);
            else
                ShowValidation(
                    $"Registered: {result.GameCount} games, {result.MediaCount} media entries.",
                    isError: false);
            RefreshList();
        }
        catch (Exception ex)
        {
            _registering = false;
            UpdateButtons();
            ShowValidation($"Registration failed: {ex.Message}");
        }
    }

    protected override void OnClosing(WindowClosingEventArgs e)
    {
        if (_registering) e.Cancel = true;   // the index transaction is running: no closing mid-way
        base.OnClosing(e);
    }

    private async void OnDetach(object? sender, RoutedEventArgs e)
    {
        if (_selected is null) return;
        var name      = Path.GetFileName(_selected.PackagePath);
        var confirmed = await new ConfirmDialog(
            "Detach Cache Package",
            $"Detach \"{name}\" from Arkadia?\n\nThe ZIP file will remain on disk.")
            .ShowDialog<bool>(this);
        if (!confirmed) return;
        _catalog.DetachCachePackage(_selected.Id);
        RefreshList();
    }

    private async void OnDeleteAndDetach(object? sender, RoutedEventArgs e)
    {
        if (_selected is null) return;
        var name      = Path.GetFileName(_selected.PackagePath);
        var confirmed = await new ConfirmDialog(
            "Delete File + Detach",
            $"Delete the ZIP file from disk and detach \"{name}\" from Arkadia?\n\nThis cannot be undone.")
            .ShowDialog<bool>(this);
        if (!confirmed) return;

        try
        {
            File.Delete(_selected.PackagePath);
        }
        catch (Exception ex)
        {
            ShowValidation($"File deletion failed: {ex.Message}");
            return;
        }

        _catalog.DetachCachePackage(_selected.Id);
        RefreshList();
    }

    private void OnRefresh(object? sender, RoutedEventArgs e) => RefreshList();

    private void OnClose(object? sender, RoutedEventArgs e) => Close();

    // ── UI helpers ────────────────────────────────────────────────────────────

    private void ShowValidation(string message, bool isError = true)
    {
        ValidationMsg.Text       = message;
        ValidationMsg.Foreground = isError
            ? new SolidColorBrush(Color.Parse("#EF5350"))
            : new SolidColorBrush(Color.Parse("#4CAF50"));
        ValidationMsg.IsVisible  = true;
    }

    private void HideValidation() => ValidationMsg.IsVisible = false;
}
