using System.Collections.Generic;
using Avalonia.Controls;
using Avalonia.Interactivity;

namespace Arkadia;

public partial class MainWindow : Window
{
    private readonly List<Button> _navButtons = [];
    private Button? _activeButton;

    public MainWindow()
    {
        InitializeComponent();

        _navButtons.AddRange([
            NavDashboard, NavLibrary, NavVolumes, NavDisks, NavOperations,
            NavLogs, NavSettings,
        ]);

        SetActive(NavDashboard);
    }

    private void OnNavClick(object? sender, RoutedEventArgs e)
    {
        if (sender is not Button btn)
            return;

        SetActive(btn);
    }

    private void SetActive(Button btn)
    {
        if (_activeButton == btn)
            return;

        // Clear previous active
        _activeButton?.Classes.Remove("active");

        // Apply active to new
        btn.Classes.Add("active");
        _activeButton = btn;

        // Update header title from Tag
        if (btn.Tag is string label)
            PageTitle.Text = label;
    }
}
