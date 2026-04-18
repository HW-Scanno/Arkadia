using System.Linq;
using System.Text.RegularExpressions;
using Arkadia.Data;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Media;

namespace Arkadia;

public partial class PlatformTypeManagerDialog : Window
{
    private readonly CatalogService _catalog;

    // Parameterless ctor required by Avalonia XAML compiler
    public PlatformTypeManagerDialog() : this(null!) { }

    public PlatformTypeManagerDialog(CatalogService catalog)
    {
        InitializeComponent();
        _catalog = catalog;
        BuildTypeList();
    }

    // ── List ─────────────────────────────────────────────────────────────────

    private void BuildTypeList()
    {
        TypeListPanel.Children.Clear();

        var types = _catalog.LoadHardwareTypes();

        foreach (var t in types)
        {
            var captured = t;

            var nameLabel = new TextBlock
            {
                Text       = t.Name,
                FontSize   = 13,
                FontWeight = FontWeight.SemiBold,
                Foreground = new SolidColorBrush(Color.FromRgb(0xF0, 0xF0, 0xF0)),
            };
            var idLabel = new TextBlock
            {
                Text       = t.Id,
                FontSize   = 11,
                Foreground = Brushes.Gray,
                FontFamily = new FontFamily("Consolas,Courier New,monospace"),
            };

            var headerRow = new StackPanel { Orientation = Avalonia.Layout.Orientation.Horizontal, Spacing = 0 };
            headerRow.Children.Add(nameLabel);
            if (t.IsSeeded)
            {
                headerRow.Children.Add(new TextBlock
                {
                    Text                  = "seeded",
                    FontSize              = 10,
                    Foreground            = new SolidColorBrush(Color.FromRgb(0x88, 0x88, 0x99)),
                    VerticalAlignment     = Avalonia.Layout.VerticalAlignment.Center,
                    Margin                = new Avalonia.Thickness(8, 0, 0, 0),
                });
            }

            var inner = new StackPanel { Spacing = 2 };
            inner.Children.Add(headerRow);
            inner.Children.Add(idLabel);

            var deleteButton = new Button
            {
                Content         = "Delete",
                Background      = new SolidColorBrush(Color.FromRgb(0x2A, 0x12, 0x18)),
                Foreground      = new SolidColorBrush(Color.FromRgb(0xEF, 0x53, 0x50)),
                BorderBrush     = new SolidColorBrush(Color.FromRgb(0x3A, 0x1C, 0x22)),
                BorderThickness = new Avalonia.Thickness(1),
                CornerRadius    = new Avalonia.CornerRadius(4),
                Padding         = new Avalonia.Thickness(10, 5),
                FontSize        = 11,
                IsEnabled       = !t.IsSeeded,
                VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center,
            };
            deleteButton.Click += async (_, _) => await DeleteTypeAsync(captured);

            var row = new Border();
            row.Classes.Add("type-row");
            row.Child = new Grid
            {
                ColumnDefinitions = new ColumnDefinitions("*,Auto"),
                Children =
                {
                    new Panel { Children = { inner }, [Grid.ColumnProperty] = 0 },
                    new Panel { Children = { deleteButton }, [Grid.ColumnProperty] = 1 },
                },
            };

            TypeListPanel.Children.Add(row);
        }

        if (types.Count == 0)
        {
            TypeListPanel.Children.Add(new TextBlock
            {
                Text       = "No platform types defined.",
                FontSize   = 12,
                Foreground = Brushes.Gray,
                Margin     = new Avalonia.Thickness(0, 4),
            });
        }
    }

    // ── Delete ────────────────────────────────────────────────────────────────

    private async System.Threading.Tasks.Task DeleteTypeAsync(HardwareTypeRecord type)
    {
        if (type.IsSeeded)
            return;

        if (_catalog.HardwareTypeHasDependencies(type.Id))
        {
            await new InfoDialog(
                "Cannot Delete Platform Type",
                "This platform type is used by one or more platforms and cannot be deleted.\n" +
                "Please update those platforms before deleting this platform type.")
                .ShowDialog(this);
            return;
        }

        var confirmed = await new ConfirmDialog(
            "Delete Platform Type",
            $"Delete '{type.Name}'? This cannot be undone.")
            .ShowDialog<bool>(this);

        if (!confirmed) return;

        _catalog.DeleteHardwareType(type.Id);
        BuildTypeList();
    }

    // ── Add panel ─────────────────────────────────────────────────────────────

    private static readonly Regex SafeId = new(@"^[a-z][a-z0-9]*$", RegexOptions.Compiled);

    private void OnShowAddPanel(object? sender, RoutedEventArgs e)
    {
        AddIdInput.Text   = "";
        AddNameInput.Text = "";
        AddIdErrorText.IsVisible = false;
        AddTypeButton.IsEnabled  = false;
        AddPanel.IsVisible       = true;
    }

    private void OnAddFieldChanged(object? sender, TextChangedEventArgs e) => ValidateAdd();

    private void ValidateAdd()
    {
        var id   = AddIdInput.Text?.Trim() ?? "";
        var name = AddNameInput.Text?.Trim() ?? "";

        string? error = id.Length == 0         ? null
            : !SafeId.IsMatch(id)              ? "ID must start with a letter, then lowercase letters or digits only."
            : _catalog.LoadHardwareTypes().Any(h => h.Id == id) ? "A platform type with this ID already exists."
            : null;

        AddIdErrorText.Text      = error ?? "";
        AddIdErrorText.IsVisible = error is not null;

        AddTypeButton.IsEnabled = id.Length > 0 && name.Length > 0 && error is null;
    }

    private void OnAddType(object? sender, RoutedEventArgs e)
    {
        var id   = AddIdInput.Text?.Trim() ?? "";
        var name = AddNameInput.Text?.Trim() ?? "";
        if (id.Length == 0 || name.Length == 0) return;

        var existing = _catalog.LoadHardwareTypes();
        var maxSort  = existing.Count > 0 ? existing.Max(h => h.SortOrder) : 0;

        _catalog.SaveHardwareType(new HardwareTypeRecord
        {
            Id        = id,
            Name      = name,
            SortOrder = maxSort + 10,
            IsSeeded  = false,
        });

        AddPanel.IsVisible = false;
        BuildTypeList();
    }

    private void OnCancelAdd(object? sender, RoutedEventArgs e)
    {
        AddPanel.IsVisible = false;
    }

    private void OnClose(object? sender, RoutedEventArgs e) => Close(true);
}
