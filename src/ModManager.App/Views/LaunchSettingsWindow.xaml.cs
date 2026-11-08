using System.Windows;

namespace ModManager.App.Views;

/// <summary>
/// Small editor for a game's launch configuration: executable, arguments, working directory.
/// </summary>
public partial class LaunchSettingsWindow : Window
{
    /// <summary>Creates the dialog.</summary>
    /// <param name="gameName">Game display name, shown in the header.</param>
    /// <param name="executable">Current executable override.</param>
    /// <param name="arguments">Current launch arguments.</param>
    /// <param name="workingDirectory">Current working directory.</param>
    public LaunchSettingsWindow(string gameName, string? executable, string? arguments, string? workingDirectory)
    {
        InitializeComponent();

        GameName = gameName;
        ExeBox.Text = executable ?? string.Empty;
        ArgsBox.Text = arguments ?? string.Empty;
        WorkDirBox.Text = workingDirectory ?? string.Empty;
    }

    /// <summary>Game display name.</summary>
    public string GameName { get; }

    /// <summary>The executable the user entered.</summary>
    public string? Executable => string.IsNullOrWhiteSpace(ExeBox.Text) ? null : ExeBox.Text.Trim();

    /// <summary>The launch arguments the user entered.</summary>
    public string? Arguments => string.IsNullOrWhiteSpace(ArgsBox.Text) ? null : ArgsBox.Text;

    /// <summary>The working directory the user entered.</summary>
    public string? WorkingDirectory => string.IsNullOrWhiteSpace(WorkDirBox.Text) ? null : WorkDirBox.Text.Trim();

    private void OnSave(object sender, RoutedEventArgs e)
    {
        DialogResult = true;
    }

    private void OnCancel(object sender, RoutedEventArgs e)
    {
        DialogResult = false;
    }
}
