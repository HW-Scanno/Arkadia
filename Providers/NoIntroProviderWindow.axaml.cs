using System;
using System.Diagnostics;
using System.IO;
using Avalonia.Controls;
using Avalonia.Interactivity;

namespace Arkadia;

public partial class NoIntroProviderWindow : Window
{
    public NoIntroProviderWindow()
    {
        InitializeComponent();
        NoIntroFolderPath.Text = ProviderHelpers.GetNoIntroOutputDir();
    }

    private void OnNiCreateFolder(object? sender, RoutedEventArgs e)
    {
        var dir = ProviderHelpers.GetNoIntroOutputDir();
        try
        {
            bool existed = Directory.Exists(dir);
            Directory.CreateDirectory(dir);
            NoIntroFolderPath.Text = dir;
            AppendLog(existed
                ? $"Folder already exists: {dir}"
                : $"Created: {dir}",
                existed ? "#888899" : "#4CAF50");
        }
        catch (Exception ex)
        {
            AppendLog($"Failed to create folder: {ex.Message}", "#EF5350");
        }
    }

    private void OnNiOpenFolder(object? sender, RoutedEventArgs e)
    {
        var dir = ProviderHelpers.GetNoIntroOutputDir();
        try
        {
            Directory.CreateDirectory(dir);
            NoIntroFolderPath.Text = dir;
            Process.Start(new ProcessStartInfo { FileName = dir, UseShellExecute = true });
            AppendLog($"Opened: {dir}");
        }
        catch (Exception ex)
        {
            AppendLog($"Failed to open folder: {ex.Message}", "#EF5350");
        }
    }

    private void OnNiOpenBrowser(object? sender, RoutedEventArgs e)
    {
        const string url = "https://datomatic.no-intro.org/index.php?page=download&op=dat";
        try
        {
            Process.Start(new ProcessStartInfo { FileName = url, UseShellExecute = true });
            AppendLog($"Opened browser → {url}");
        }
        catch (Exception ex)
        {
            AppendLog($"Failed to open browser: {ex.Message}", "#EF5350");
        }
    }

    private void OnClose(object? sender, RoutedEventArgs e) => Close();

    private void AppendLog(string text, string color = "#888899") =>
        ProviderHelpers.AppendLog(LogPanel, LogScrollViewer, text, color);
}
