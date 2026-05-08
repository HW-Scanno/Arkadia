using System;
using System.Collections.Generic;
using System.Text.RegularExpressions;
using Arkadia.Data;
using Avalonia.Controls;
using Avalonia.Interactivity;

namespace Arkadia;

public partial class ToolDialog : Window
{
    private readonly HashSet<string> _existingIds;
    private readonly bool            _isEditMode;
    private readonly bool            _originalIsBundled;

    public ToolRecord? Result { get; private set; }

    public ToolDialog() : this([], null) { }

    public ToolDialog(IEnumerable<string> existingIds, ToolRecord? prefill)
    {
        InitializeComponent();

        _existingIds       = new HashSet<string>(existingIds, StringComparer.OrdinalIgnoreCase);
        _isEditMode        = prefill is not null;
        _originalIsBundled = prefill?.IsBundled ?? false;

        if (_isEditMode && prefill is not null)
        {
            Title              = "Edit Tool";
            TitleLabel.Text    = "Edit Tool";
            SubtitleLabel.Text = "Update the folder or executable name for this tool.";
            SaveButton.Content = "Save Changes";

            IdInput.Text     = prefill.Id;
            IdInput.Classes.Add("id-readonly");
            FolderInput.Text = prefill.FolderName;
            ExeInput.Text    = prefill.ExecutableName;
        }
        else
        {
            Title              = "Add Tool";
            TitleLabel.Text    = "Add Tool";
            SubtitleLabel.Text = "Register a new external tool for use in transforms.";
            SaveButton.Content = "Add Tool";
        }

        ValidateForm();
    }

    private static readonly Regex SafeId =
        new(@"^[a-z0-9][a-z0-9\-_]*$", RegexOptions.Compiled);

    private void OnFieldChanged(object? sender, TextChangedEventArgs e) => ValidateForm();

    private void ValidateForm()
    {
        var id     = IdInput.Text?.Trim()     ?? "";
        var folder = FolderInput.Text?.Trim() ?? "";
        var exe    = ExeInput.Text?.Trim()    ?? "";

        string? error = _isEditMode ? null
            : id.Length == 0            ? null
            : !SafeId.IsMatch(id)       ? "ID must be lowercase alphanumeric, hyphens, or underscores."
            : _existingIds.Contains(id) ? "A tool with this ID already exists."
            : null;

        IdErrorText.Text      = error ?? "";
        IdErrorText.IsVisible = error is not null;

        SaveButton.IsEnabled =
            id.Length > 0 && error is null &&
            folder.Length > 0 &&
            exe.Length > 0;
    }

    private void OnSave(object? sender, RoutedEventArgs e)
    {
        Result = new ToolRecord
        {
            Id             = IdInput.Text!.Trim(),
            FolderName     = FolderInput.Text!.Trim(),
            ExecutableName = ExeInput.Text!.Trim(),
            IsBundled      = _isEditMode ? _originalIsBundled : false,
        };
        Close(true);
    }

    private void OnCancel(object? sender, RoutedEventArgs e) => Close(false);
}
