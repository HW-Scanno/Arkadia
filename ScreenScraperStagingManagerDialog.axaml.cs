using System;
using System.Diagnostics;
using System.IO;
using Arkadia.Data;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Media;

namespace Arkadia;

public partial class ScreenScraperStagingManagerDialog : Window
{
    private readonly ScreenScraperStagingService _service;
    private ScreenScraperStagingRecord?           _selected;

    public ScreenScraperStagingManagerDialog() : this(AppContext.BaseDirectory) { }

    public ScreenScraperStagingManagerDialog(string baseDir)
    {
        InitializeComponent();
        _service = new ScreenScraperStagingService(baseDir);
    }

    protected override void OnLoaded(RoutedEventArgs e)
    {
        base.OnLoaded(e);
        RefreshList();
    }

    // ── List management ───────────────────────────────────────────────────────

    private void RefreshList()
    {
        var records = _service.LoadStagingRecords();
        StagingList.ItemsSource  = records;
        _selected                = null;
        StagingList.SelectedItem = null;
        EmptyMsg.IsVisible       = records.Count == 0;
        UpdateButtons();
        HideValidation();
    }

    private void OnSelectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        _selected = StagingList.SelectedItem as ScreenScraperStagingRecord;
        UpdateButtons();
    }

    private void UpdateButtons()
    {
        OpenFolderBtn.IsEnabled = _selected is not null;
        DeleteBtn.IsEnabled     = _selected is not null;
    }

    // ── Actions ───────────────────────────────────────────────────────────────

    private void OnRefresh(object? sender, RoutedEventArgs e) => RefreshList();

    private void OnOpenFolder(object? sender, RoutedEventArgs e)
    {
        if (_selected is null) return;

        if (!Directory.Exists(_selected.FolderPath))
        {
            ShowValidation($"Folder no longer exists: {_selected.FolderPath}");
            RefreshList();
            return;
        }

        try
        {
            Process.Start(new ProcessStartInfo
            {
                FileName        = _selected.FolderPath,
                UseShellExecute = true,
            });
        }
        catch (Exception ex)
        {
            ShowValidation($"Could not open folder: {ex.Message}");
        }
    }

    private async void OnDeleteStaging(object? sender, RoutedEventArgs e)
    {
        if (_selected is null) return;

        var confirmed = await new ConfirmDialog(
            "Delete Staging Folder",
            $"Delete staging folder \"{_selected.PackageName}\"?\n\n" +
            "This removes resumable build data but does not delete completed cache ZIP packages.")
            .ShowDialog<bool>(this);

        if (!confirmed) return;

        try
        {
            _service.DeleteStaging(_selected.FolderPath);
            ShowValidation($"Staging folder \"{_selected.PackageName}\" deleted.", isError: false);
        }
        catch (Exception ex)
        {
            ShowValidation($"Deletion failed: {ex.Message}");
        }

        RefreshList();
    }

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
