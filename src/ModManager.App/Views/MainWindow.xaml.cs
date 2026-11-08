using System.IO;
using System.Windows;
using System.Windows.Controls;
using ModManager.App.ViewModels;

namespace ModManager.App.Views;

/// <summary>
/// The shell window.
/// </summary>
/// <remarks>
/// The code-behind is deliberately almost empty: game selection and drag-drop are the only things
/// that need a real event handler, because both arrive as window events rather than bindings.
/// </remarks>
public partial class MainWindow : System.Windows.Window
{
    /// <summary>Creates the window.</summary>
    public MainWindow() => InitializeComponent();

    /// <summary>Archive extensions accepted by drag-drop import.</summary>
    private static readonly HashSet<string> ArchiveExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".zip", ".7z", ".rar", ".tar", ".gz", ".tgz", ".tar.gz", ".fomod", ".omod",
    };

    /// <summary>Switches the active game when the header selector changes.</summary>
    /// <param name="sender">Selector.</param>
    /// <param name="e">Change arguments.</param>
    private void OnGameSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (DataContext is not ShellViewModel shell || sender is not ComboBox combo)
        {
            return;
        }

        if (combo.SelectedItem is GameChoice choice)
        {
            shell.SelectGameCommand.Execute(choice);
        }
    }

    /// <summary>
    /// Navigates when a rail item becomes the checked one.
    /// </summary>
    /// <param name="sender">The rail item.</param>
    /// <param name="e">Event arguments.</param>
    /// <remarks>
    /// Driven by <c>Checked</c> instead of a <c>Command</c> on the button. A command attached to a
    /// RadioButton only runs on a real click, so anything else that selects the item — UI Automation,
    /// a keyboard mnemonic, or assistive technology, all of which set <c>IsChecked</c> through the
    /// automation peer — moved the highlight without changing the page. That is an accessibility defect
    /// first, and it is also why a screenshot run once produced eight images of the same page with eight
    /// different navigation items highlighted.
    /// </remarks>
    private void OnNavigationChecked(object sender, RoutedEventArgs e)
    {
        if (sender is RadioButton { DataContext: PageDescriptor page } && DataContext is ShellViewModel shell)
        {
            shell.NavigateTo(page.Name);
        }
    }

    /// <summary>Accepts the drop when it contains at least one archive file.</summary>
    /// <param name="sender">Window.</param>
    /// <param name="e">Drag arguments.</param>
    private void OnDragOver(object sender, DragEventArgs e)
    {
        e.Effects = GetDroppedArchives(e) is { Count: > 0 }
            ? DragDropEffects.Copy
            : DragDropEffects.None;
        e.Handled = true;
    }

    /// <summary>Hands dropped archive files to the Mods page for batch import.</summary>
    /// <param name="sender">Window.</param>
    /// <param name="e">Drop arguments.</param>
    private async void OnDrop(object sender, DragEventArgs e)
    {
        var archives = GetDroppedArchives(e);
        e.Handled = true;

        if (archives.Count == 0 || DataContext is not ShellViewModel shell)
        {
            return;
        }

        await shell.ImportArchivesAsync(archives).ConfigureAwait(true);
    }

    /// <summary>Extracts the archive files from a drop payload.</summary>
    /// <param name="e">Drop arguments.</param>
    private static List<string> GetDroppedArchives(DragEventArgs e)
    {
        if (!e.Data.GetDataPresent(DataFormats.FileDrop) || e.Data.GetData(DataFormats.FileDrop) is not string[] files)
        {
            return [];
        }

        var results = new List<string>();
        foreach (var file in files)
        {
            // *.tar.gz needs a two-part check before the plain extension test.
            if (file.EndsWith(".tar.gz", StringComparison.OrdinalIgnoreCase))
            {
                results.Add(file);
                continue;
            }

            if (File.Exists(file) && ArchiveExtensions.Contains(Path.GetExtension(file)))
            {
                results.Add(file);
            }
        }

        return results;
    }
}
