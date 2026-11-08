using System.Windows;
using System.Windows.Controls;
using ModManager.App.ViewModels;

namespace ModManager.App.Views;

/// <summary>
/// Code-behind for every page.
/// </summary>
/// <remarks>
/// These classes exist only because the XAML compiler requires a partial class per <c>x:Class</c>. With one
/// forced exception they contain no logic: all page behaviour is binding to a view-model, and every action
/// goes through a command. Keeping them in one file makes that easy to verify at a glance — a page with
/// behaviour has to grow a method here, which is a visible change rather than a quiet one.
/// <para>
/// THE EXCEPTION — <see cref="SettingsView.OnApiKeyChanged"/>. <see cref="PasswordBox.Password"/> is
/// deliberately not a dependency property (a bindable password would leave the secret sitting in a plain
/// string reachable by anything that can walk the visual tree), so it cannot participate in a binding and
/// the value has to be forwarded in code. The handler does exactly that and nothing else: no validation,
/// no transformation, no storage. Every decision about the key stays in the view-model.
/// </para>
/// </remarks>
public partial class DashboardView : UserControl
{
    /// <summary>Creates the view.</summary>
    public DashboardView() => InitializeComponent();
}

/// <summary>Games page.</summary>
public partial class GamesView : UserControl
{
    /// <summary>Creates the view.</summary>
    public GamesView() => InitializeComponent();
}

/// <summary>Mods page.</summary>
public partial class ModsView : UserControl
{
    /// <summary>Creates the view.</summary>
    public ModsView() => InitializeComponent();
}

/// <summary>Downloads page.</summary>
public partial class DownloadsView : UserControl
{
    /// <summary>Creates the view.</summary>
    public DownloadsView() => InitializeComponent();
}

/// <summary>Profiles page.</summary>
public partial class ProfilesView : UserControl
{
    /// <summary>Creates the view.</summary>
    public ProfilesView() => InitializeComponent();
}

/// <summary>Conflicts page.</summary>
public partial class ConflictsView : UserControl
{
    /// <summary>Creates the view.</summary>
    public ConflictsView() => InitializeComponent();
}

/// <summary>Settings page.</summary>
public partial class SettingsView : UserControl
{
    /// <summary>Creates the view.</summary>
    public SettingsView() => InitializeComponent();

    /// <summary>Forwards the password box's value to the view-model.</summary>
    /// <param name="sender">The <see cref="PasswordBox"/> that raised the event.</param>
    /// <param name="e">Event data.</param>
    /// <remarks>
    /// See the class remarks: this is the one place in the app where code-behind does work, and it is forced
    /// by WPF rather than chosen. Nothing is inspected or transformed here — the characters go straight to
    /// the view-model, which owns every decision about the credential.
    /// </remarks>
    private void OnApiKeyChanged(object sender, RoutedEventArgs e)
    {
        if (sender is PasswordBox box && DataContext is SettingsViewModel viewModel)
        {
            viewModel.ApiKeyInput = box.Password;
        }
    }
}

/// <summary>Logs page.</summary>
public partial class LogsView : UserControl
{
    /// <summary>Creates the view.</summary>
    public LogsView() => InitializeComponent();
}

/// <summary>Online Mods page.</summary>
public partial class OnlineModsView : UserControl
{
    /// <summary>Creates the view.</summary>
    public OnlineModsView() => InitializeComponent();
}
