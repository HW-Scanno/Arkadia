using System;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using Arkadia.Data;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Platform.Storage;

namespace Arkadia;

public partial class AuthorityManagerDialog : Window
{
    private readonly CatalogService _catalog;
    private readonly string         _dataDir;

    private AuthorityRecord? _selectedAuthority;

    // Parameterless ctor required by Avalonia XAML compiler
    public AuthorityManagerDialog() : this(null!, string.Empty) { }

    public AuthorityManagerDialog(CatalogService catalog, string dataDir)
    {
        InitializeComponent();
        _catalog = catalog;
        _dataDir = dataDir;
        BuildAuthorityList();
    }

    // ── List ─────────────────────────────────────────────────────────────────

    private void BuildAuthorityList()
    {
        AuthorityListPanel.Children.Clear();

        var authorities = _catalog.LoadAuthorities();

        foreach (var a in authorities)
        {
            var captured = a;

            var idLabel = new TextBlock
            {
                Text       = a.Id,
                FontSize   = 11,
                Foreground = Brushes.Gray,
                FontFamily = new FontFamily("Consolas,Courier New,monospace"),
            };
            var nameLabel = new TextBlock
            {
                Text       = a.Name,
                FontSize   = 13,
                FontWeight = FontWeight.SemiBold,
                Foreground = new SolidColorBrush(Color.FromRgb(0xF0, 0xF0, 0xF0)),
            };
            var badge = a.IsSeeded ? new TextBlock
            {
                Text       = "seeded",
                FontSize   = 10,
                Foreground = new SolidColorBrush(Color.FromRgb(0x88, 0x88, 0x99)),
                VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center,
                Margin = new Avalonia.Thickness(8, 0, 0, 0),
            } : null;

            var headerRow = new StackPanel { Orientation = Avalonia.Layout.Orientation.Horizontal, Spacing = 0 };
            headerRow.Children.Add(nameLabel);
            if (badge is not null) headerRow.Children.Add(badge);

            var inner = new StackPanel { Spacing = 2 };
            inner.Children.Add(headerRow);
            inner.Children.Add(idLabel);

            var row = new Border();
            row.Classes.Add("authority-row");
            row.Child = inner;

            var isSelected = _selectedAuthority?.Id == a.Id;
            if (isSelected) row.Classes.Add("selected");

            row.PointerPressed += (_, _) => OnRowClicked(captured, row);

            AuthorityListPanel.Children.Add(row);
        }

        if (authorities.Count == 0)
        {
            AuthorityListPanel.Children.Add(new TextBlock
            {
                Text       = "No authorities defined.",
                FontSize   = 12,
                Foreground = Brushes.Gray,
                Margin     = new Avalonia.Thickness(0, 4),
            });
        }
    }

    // ── Row selection ─────────────────────────────────────────────────────────

    private void OnRowClicked(AuthorityRecord authority, Border clickedRow)
    {
        _selectedAuthority = authority;
        AddPanel.IsVisible    = false;

        // Update selection highlight
        foreach (var child in AuthorityListPanel.Children)
        {
            if (child is Border b)
                b.Classes.Remove("selected");
        }
        clickedRow.Classes.Add("selected");

        EditorIdInput.Text   = authority.Id;
        EditorNameInput.Text = authority.Name;
        RefreshEditorLogo(authority.Id);

        EditorDeleteButton.IsEnabled = !authority.IsSeeded;
        EditorSaveButton.IsEnabled   = false;

        EditorPanel.IsVisible = true;
    }

    private void RefreshEditorLogo(string id)
    {
        var path = LogoPath(id);
        if (File.Exists(path))
        {
            try
            {
                EditorLogoImage.Source  = new Bitmap(path);
                RemoveLogoButton.IsEnabled = true;
                return;
            }
            catch { }
        }
        EditorLogoImage.Source     = null;
        RemoveLogoButton.IsEnabled = false;
    }

    // ── Editor handlers ───────────────────────────────────────────────────────

    private void OnEditorNameChanged(object? sender, TextChangedEventArgs e)
    {
        if (_selectedAuthority is null) return;
        var name = EditorNameInput.Text?.Trim() ?? "";
        EditorSaveButton.IsEnabled = name.Length > 0 && name != _selectedAuthority.Name;
    }

    private void OnSaveChanges(object? sender, RoutedEventArgs e)
    {
        if (_selectedAuthority is null) return;
        var name = EditorNameInput.Text?.Trim() ?? "";
        if (name.Length == 0) return;

        _selectedAuthority.Name = name;
        _catalog.SaveAuthority(_selectedAuthority);

        BuildAuthorityList();
        EditorSaveButton.IsEnabled = false;
    }

    private async void OnBrowseLogo(object? sender, RoutedEventArgs e)
    {
        if (_selectedAuthority is null) return;

        var files = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title         = "Select Logo",
            AllowMultiple = false,
            FileTypeFilter =
            [
                new FilePickerFileType("Image") { Patterns = ["*.png", "*.jpg", "*.jpeg", "*.bmp"] },
            ],
        });

        if (files.Count == 0) return;
        if (files[0].TryGetLocalPath() is not string src) return;

        var dest = LogoPath(_selectedAuthority.Id);
        Directory.CreateDirectory(Path.GetDirectoryName(dest)!);
        File.Copy(src, dest, overwrite: true);

        RefreshEditorLogo(_selectedAuthority.Id);
    }

    private void OnRemoveLogo(object? sender, RoutedEventArgs e)
    {
        if (_selectedAuthority is null) return;
        var path = LogoPath(_selectedAuthority.Id);
        try { File.Delete(path); } catch { }
        RefreshEditorLogo(_selectedAuthority.Id);
    }

    private async void OnDeleteAuthority(object? sender, RoutedEventArgs e)
    {
        if (_selectedAuthority is null) return;

        if (_selectedAuthority.IsSeeded)
        {
            await new InfoDialog("Cannot Delete", "Seeded authorities cannot be deleted.").ShowDialog(this);
            return;
        }

        if (_catalog.AuthorityHasDependencies(_selectedAuthority.Id))
        {
            await new InfoDialog("Cannot Delete",
                $"Authority '{_selectedAuthority.Name}' is referenced by one or more DAT lines and cannot be deleted.")
                .ShowDialog(this);
            return;
        }

        var confirmed = await new ConfirmDialog(
            "Delete Authority",
            $"Delete '{_selectedAuthority.Name}'? This cannot be undone.")
            .ShowDialog<bool>(this);

        if (!confirmed) return;

        _catalog.DeleteAuthority(_selectedAuthority.Id);
        _selectedAuthority = null;
        EditorPanel.IsVisible = false;
        BuildAuthorityList();
    }

    // ── Add panel ─────────────────────────────────────────────────────────────

    private static readonly Regex SafeId = new(@"^[a-z][a-z0-9]*$", RegexOptions.Compiled);

    private void OnShowAddPanel(object? sender, RoutedEventArgs e)
    {
        _selectedAuthority    = null;
        EditorPanel.IsVisible = false;

        foreach (var child in AuthorityListPanel.Children)
            if (child is Border b) b.Classes.Remove("selected");

        AddIdInput.Text   = "";
        AddNameInput.Text = "";
        AddIdErrorText.IsVisible   = false;
        AddAuthorityButton.IsEnabled = false;
        AddPanel.IsVisible = true;
    }

    private void OnAddFieldChanged(object? sender, TextChangedEventArgs e) => ValidateAdd();

    private void ValidateAdd()
    {
        var id   = AddIdInput.Text?.Trim() ?? "";
        var name = AddNameInput.Text?.Trim() ?? "";

        string? error = id.Length == 0         ? null
            : !SafeId.IsMatch(id)              ? "ID must start with a letter, then lowercase letters or digits only."
            : _catalog.LoadAuthorities().Any(a => a.Id == id) ? "An authority with this ID already exists."
            : null;

        AddIdErrorText.Text      = error ?? "";
        AddIdErrorText.IsVisible = error is not null;

        AddAuthorityButton.IsEnabled = id.Length > 0 && name.Length > 0 && error is null;
    }

    private void OnAddAuthority(object? sender, RoutedEventArgs e)
    {
        var id   = AddIdInput.Text?.Trim() ?? "";
        var name = AddNameInput.Text?.Trim() ?? "";
        if (id.Length == 0 || name.Length == 0) return;

        var record = new AuthorityRecord { Id = id, Name = name, IsSeeded = false };
        _catalog.SaveAuthority(record);

        _selectedAuthority  = record;
        AddPanel.IsVisible  = false;
        BuildAuthorityList();

        // Auto-open editor for the newly added authority
        foreach (var child in AuthorityListPanel.Children)
        {
            if (child is Border b && b.Child is StackPanel sp)
            {
                var header = (sp.Children[0] as StackPanel)?.Children[0] as TextBlock;
                if (header?.Text == name)
                {
                    OnRowClicked(record, b);
                    break;
                }
            }
        }
    }

    private void OnCancelAdd(object? sender, RoutedEventArgs e)
    {
        AddPanel.IsVisible = false;
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    private string LogoPath(string id) =>
        Path.Combine(_dataDir, "authorityimages", $"{id}.png");

    private void OnClose(object? sender, RoutedEventArgs e) => Close(true);
}
