using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows.Input;

namespace ModManager.App.ViewModels;

/// <summary>
/// Minimal MVVM base: change notification plus a command implementation.
/// </summary>
/// <remarks>
/// Hand-written rather than taken from a toolkit, consistent with the no-package-dependency constraint.
/// Deliberately small: a view-model here is a presentation adapter over engine services, and the less
/// machinery it carries the easier it is to see that it holds no business rules.
/// </remarks>
public abstract class ViewModelBase : INotifyPropertyChanged
{
    /// <inheritdoc />
    public event PropertyChangedEventHandler? PropertyChanged;

    /// <summary>Raises change notification for one property.</summary>
    /// <param name="propertyName">Property name, supplied automatically by the compiler.</param>
    protected void OnPropertyChanged([CallerMemberName] string? propertyName = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));

    /// <summary>Assigns a field and raises notification when the value actually changed.</summary>
    /// <typeparam name="T">Value type.</typeparam>
    /// <param name="field">Field to assign.</param>
    /// <param name="value">New value.</param>
    /// <param name="propertyName">Property name.</param>
    /// <returns>True when the value changed.</returns>
    protected bool SetProperty<T>(ref T field, T value, [CallerMemberName] string? propertyName = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value))
        {
            return false;
        }

        field = value;
        OnPropertyChanged(propertyName);
        return true;
    }
}

/// <summary>A command that delegates to a method, with an optional availability test.</summary>
public sealed class RelayCommand : ICommand
{
    private readonly Func<Task> _execute;
    private readonly Func<bool>? _canExecute;
    private bool _running;

    /// <summary>Creates a command over an synchronous action.</summary>
    /// <param name="execute">Action to run.</param>
    /// <param name="canExecute">Optional availability test.</param>
    public RelayCommand(Action execute, Func<bool>? canExecute = null)
        : this(
            () =>
            {
                execute();
                return Task.CompletedTask;
            },
            canExecute)
    {
    }

    /// <summary>Creates a command over an asynchronous action.</summary>
    /// <param name="execute">Action to run.</param>
    /// <param name="canExecute">Optional availability test.</param>
    public RelayCommand(Func<Task> execute, Func<bool>? canExecute = null)
    {
        _execute = execute;
        _canExecute = canExecute;
    }

    /// <inheritdoc />
    public event EventHandler? CanExecuteChanged;

    /// <inheritdoc />
    public bool CanExecute(object? parameter) => !_running && (_canExecute?.Invoke() ?? true);

    /// <inheritdoc />
    public async void Execute(object? parameter)
    {
        if (!CanExecute(parameter))
        {
            return;
        }

        // Guarded so a double-click cannot start the same long operation twice. Without this a user who
        // clicks Deploy twice queues two transactions over the same files.
        _running = true;
        RaiseCanExecuteChanged();

        try
        {
            await _execute().ConfigureAwait(true);
        }
        finally
        {
            _running = false;
            RaiseCanExecuteChanged();
        }
    }

    /// <summary>Re-evaluates availability.</summary>
    public void RaiseCanExecuteChanged() => CanExecuteChanged?.Invoke(this, EventArgs.Empty);
}

/// <summary>A typed variant used by lists, where the item is the parameter.</summary>
/// <typeparam name="T">Parameter type.</typeparam>
public sealed class RelayCommand<T> : ICommand
{
    private readonly Func<T, Task> _execute;
    private readonly Func<T, bool>? _canExecute;

    /// <summary>Creates a typed command.</summary>
    /// <param name="execute">Action to run.</param>
    /// <param name="canExecute">Optional availability test.</param>
    public RelayCommand(Func<T, Task> execute, Func<T, bool>? canExecute = null)
    {
        _execute = execute;
        _canExecute = canExecute;
    }

    /// <inheritdoc />
    public event EventHandler? CanExecuteChanged;

    /// <inheritdoc />
    public bool CanExecute(object? parameter) =>
        parameter is T typed && (_canExecute?.Invoke(typed) ?? true);

    /// <inheritdoc />
    public async void Execute(object? parameter)
    {
        if (parameter is T typed && CanExecute(parameter))
        {
            await _execute(typed).ConfigureAwait(true);
        }
    }

    /// <summary>Re-evaluates availability.</summary>
    public void RaiseCanExecuteChanged() => CanExecuteChanged?.Invoke(this, EventArgs.Empty);
}

/// <summary>
/// A list that supports <c>AddRange</c> semantics for a batch of rows.
/// </summary>
/// <typeparam name="T">Row type.</typeparam>
/// <remarks>
/// Rebuilding a page means clearing and refilling a collection. Using <c>ObservableCollection</c> directly
/// raises one notification per item, which for a mod list of several hundred entries produces a visible
/// stutter. This raises a single reset instead.
/// </remarks>
public sealed class BulkObservableCollection<T> : ObservableCollection<T>
{
    /// <summary>Replaces the entire contents, raising one reset notification.</summary>
    /// <param name="items">Items to install.</param>
    public void ReplaceAll(IEnumerable<T> items)
    {
        Items.Clear();
        foreach (var item in items)
        {
            Items.Add(item);
        }

        OnPropertyChanged(new PropertyChangedEventArgs(nameof(Count)));
        OnPropertyChanged(new PropertyChangedEventArgs("Item[]"));
        OnCollectionChanged(new System.Collections.Specialized.NotifyCollectionChangedEventArgs(
            System.Collections.Specialized.NotifyCollectionChangedAction.Reset));
    }
}
