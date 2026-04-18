using System;
using System.Windows.Input;
using Avalonia.Controls;

namespace Arkadia;

public static class TextBoxCommands
{
    // Captured on GotFocus (before the ContextMenu opens and steals focus).
    // Commands read this instead of relying on PlacementTarget, which is
    // unreachable by the time the ContextMenu has closed and the command fires.
    internal static TextBox? LastFocused { get; set; }

    public static readonly ICommand Cut   = new LambdaCommand(_ => LastFocused?.Cut());
    public static readonly ICommand Copy  = new LambdaCommand(_ => LastFocused?.Copy());
    public static readonly ICommand Paste = new LambdaCommand(_ => LastFocused?.Paste());

    private sealed class LambdaCommand(Action<object?> execute) : ICommand
    {
        public event EventHandler? CanExecuteChanged { add { } remove { } }
        public bool CanExecute(object? parameter) => true;
        public void Execute(object? parameter)    => execute(parameter);
    }
}
