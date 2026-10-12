using System.Windows.Input;
namespace COT_VCF.Presentation;

public sealed class Command(Action<object?> execute, Func<bool>? canExecute = null) : ICommand
{
    internal Func<bool>? Availability { get; set; }
    public bool CanExecute(object? parameter) => (Availability?.Invoke() ?? true) && (canExecute?.Invoke() ?? true);
    public void Execute(object? parameter)
    {
        if (Availability?.Invoke() == false) return;
        execute(parameter);
    }
    public event EventHandler? CanExecuteChanged { add => CommandManager.RequerySuggested += value; remove => CommandManager.RequerySuggested -= value; }
}
public interface ISelectionService
{
    IReadOnlyList<string>? Select(string title, IReadOnlyList<string> available, IReadOnlyList<string> selected);
}
