using System;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Media;

namespace Arkadia;

public partial class MoveCompleteDialog : Window
{
    public MoveCompleteDialog() { InitializeComponent(); }

    public MoveCompleteDialog(
        string   volumeLabel,
        string   sourcePath,
        string   destPath,
        int      fileCount,
        long     copiedBytes,
        TimeSpan elapsed,
        string?  cleanupError)
    {
        InitializeComponent();

        SumVolume.Text     = volumeLabel;
        SumSourcePath.Text = sourcePath;
        SumDestPath.Text   = destPath;
        SumFileCount.Text  = fileCount.ToString("N0");
        SumTotalSize.Text  = FormatBytes(copiedBytes);

        var secs = elapsed.TotalSeconds;
        SumSpeed.Text = secs > 0 ? FormatSpeed(copiedBytes / secs) : "—";

        SumElapsed.Text = elapsed.TotalHours >= 1
            ? $"{(int)elapsed.TotalHours}:{elapsed.Minutes:D2}:{elapsed.Seconds:D2}"
            : $"{elapsed.Minutes}:{elapsed.Seconds:D2}";

        var green = new SolidColorBrush(Color.Parse("#4CAF50"));
        var red   = new SolidColorBrush(Color.Parse("#EF5350"));

        SumVerification.Text       = "OK";
        SumVerification.Foreground = green;

        if (cleanupError is null)
        {
            SumSourceRemoval.Text       = "OK";
            SumSourceRemoval.Foreground = green;
        }
        else
        {
            SumSourceRemoval.Text       = $"FAILED — {cleanupError}";
            SumSourceRemoval.Foreground = red;
        }
    }

    private void OnOk(object? sender, RoutedEventArgs e) => Close();

    private static string FormatBytes(long b)
    {
        if (b <= 0)                        return "0 B";
        if (b < 1024L)                     return $"{b} B";
        if (b < 1024L * 1024)              return $"{b / 1024.0:F1} KB";
        if (b < 1024L * 1024 * 1024)       return $"{b / (1024.0 * 1024):F1} MB";
        return $"{b / (1024.0 * 1024 * 1024):F2} GB";
    }

    private static string FormatSpeed(double bps)
    {
        if (bps >= 1024.0 * 1024 * 1024) return $"{bps / (1024.0 * 1024 * 1024):F2} GB/s";
        if (bps >= 1024.0 * 1024)        return $"{bps / (1024.0 * 1024):F1} MB/s";
        if (bps >= 1024.0)               return $"{bps / 1024.0:F0} KB/s";
        return $"{bps:F0} B/s";
    }
}
