using System.Windows.Input;
namespace COT_VCF.Presentation;

public sealed class Command(Action<object?> execute, Func<bool>? canExecute = null) : ICommand
{
    public bool CanExecute(object? parameter) => canExecute?.Invoke() ?? true;
    public void Execute(object? parameter) => execute(parameter);
    public event EventHandler? CanExecuteChanged { add => CommandManager.RequerySuggested += value; remove => CommandManager.RequerySuggested -= value; }
}
public interface ISelectionService
{
    IReadOnlyList<string>? Select(string title, IReadOnlyList<string> available, IReadOnlyList<string> selected);
}
