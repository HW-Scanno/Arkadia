using System;
using System.Windows.Input;
using Avalonia.Controls;

namespace Arkadia;

public static class TextBoxCommands
{
    public static readonly ICommand Cut   = new LambdaCommand(p => (p as TextBox)?.Cut());
    public static readonly ICommand Copy  = new LambdaCommand(p => (p as TextBox)?.Copy());
    public static readonly ICommand Paste = new LambdaCommand(p => (p as TextBox)?.Paste());

    private sealed class LambdaCommand(Action<object?> execute) : ICommand
    {
        public event EventHandler? CanExecuteChanged { add { } remove { } }
        public bool CanExecute(object? parameter) => true;
        public void Execute(object? parameter)    => execute(parameter);
    }
}
