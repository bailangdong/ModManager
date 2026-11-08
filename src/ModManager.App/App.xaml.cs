using System.IO;
using System.Windows;
using ModManager.App.ViewModels;

namespace ModManager.App;

/// <summary>
/// Application entry point and composition root.
/// </summary>
/// <remarks>
/// <para>
/// Everything is wired here and nowhere else. The engine projects know nothing about WPF and take their
/// dependencies as interfaces, so this file is the single place where the object graph is decided. That
/// is what makes the engine testable without a message pump, and it is the same separation Modrix draws
/// when it says "core depends on no UI crate; frontends are thin".
/// </para>
/// <para>
/// Startup order matters and is deliberate: paths, then the logger, then the recovered journals, then the
/// window. Journal recovery runs before any view-model exists, because a half-applied transaction from a
/// previous run must be repaired before the UI can show the user a state that is not real.
/// </para>
/// </remarks>
public partial class App : Application
{
    private ShellViewModel? _shell;

    /// <summary>Creates the application.</summary>
    /// <remarks>
    /// The process-wide handlers are attached here, in the constructor, because a failure while
    /// <c>App.xaml</c>'s own resource dictionaries load happens before <c>OnStartup</c> and would
    /// otherwise exit without a trace.
    /// </remarks>
    public App()
    {
        TraceStartup("App ctor entered");

        // WPF's default element language is the literal tag "en-US". On a machine without the en-US
        // culture installed, the first culture-aware binding throws
        // "Cannot find non-neutral culture related to 'en-us'" and the window never opens. Pointing
        // the default at the system's actual culture (which by definition is installed) fixes this
        // for every machine, regardless of language packs.
        FrameworkElement.LanguageProperty.OverrideMetadata(
            typeof(FrameworkElement),
            new FrameworkPropertyMetadata(
                System.Windows.Markup.XmlLanguage.GetLanguage(
                    System.Globalization.CultureInfo.CurrentCulture.IetfLanguageTag)));

        AppDomain.CurrentDomain.UnhandledException += (_, args) =>
        {
            if (args.ExceptionObject is Exception exception)
            {
                WriteStartupError(exception);
                TraceStartup("AppDomain unhandled: " + exception.GetType().Name + ": " + exception.Message);
            }
        };

        DispatcherUnhandledException += (_, args) =>
        {
            WriteStartupError(args.Exception);
            TraceStartup("Dispatcher unhandled: " + args.Exception);
            args.Handled = true;
            Shutdown(1);
        };

        TraceStartup("App ctor hooks attached");
    }

    /// <inheritdoc />
    protected override async void OnStartup(StartupEventArgs e)
    {
        TraceStartup("OnStartup entered");
        base.OnStartup(e);
        TraceStartup("base.OnStartup returned (StartupUri window should exist)");

        try
        {
            var host = await ApplicationHost.CreateAsync().ConfigureAwait(true);
            TraceStartup("ApplicationHost created");

            // Applied before the window is constructed, so the very first frame is already in the saved
            // theme. Applying it after Show() would render a light window and then repaint it dark.
            var savedTheme = host.Settings.Settings.Theme;
            TraceStartup($"Theme setting='{savedTheme}' dark={IsDarkTheme(savedTheme)}");

            Theme.AppearanceService.Apply(IsDarkTheme(savedTheme));
            TraceStartup(
                $"Theme applied: IsDark={Theme.AppearanceService.IsDark} "
                + $"background={Theme.AppearanceService.DescribeBackground()}");

            _shell = new ShellViewModel(host);

            var window = new Views.MainWindow { DataContext = _shell };
            TraceStartup("MainWindow constructed");
            MainWindow = window;
            window.Show();
            TraceStartup("MainWindow shown");

            await _shell.InitializeAsync().ConfigureAwait(true);
            TraceStartup("Shell initialized");
        }
        catch (Exception ex)
        {
            // A failure to start is reported rather than swallowed, because a silent exit with no window
            // is indistinguishable from the application being broken. It is also written to disk, so a
            // crash during XAML load — which happens before the logger exists — is still diagnosable.
            TraceStartup("OnStartup exception: " + ex);
            WriteStartupError(ex);
            MessageBox.Show(
                "Mod Manager could not start.\n\n" + ex.Message,
                "Mod Manager",
                MessageBoxButton.OK,
                MessageBoxImage.Error);

            Shutdown(1);
        }
    }

    /// <summary>True when the saved theme name selects the dark palette.</summary>
    /// <param name="theme">Stored theme name; anything other than "Dark" means light.</param>
    /// <remarks>
    /// Read from <c>AppSettings.Theme</c>, which already existed for exactly this purpose. An older settings
    /// file that predates the switch stores no theme name and therefore keeps the light palette, which is
    /// the behaviour it was written against.
    /// </remarks>
    private static bool IsDarkTheme(string? theme) =>
        string.Equals(theme, "Dark", StringComparison.OrdinalIgnoreCase);

    /// <summary>Appends one startup trace line beside the executable.</summary>
    /// <param name="message">Line to record.</param>
    /// <remarks>
    /// Tracing must work before the regular data directory exists, so it uses a path relative to the
    /// application folder rather than a machine-specific absolute path. The folder is ignored by Git.
    /// </remarks>
    private static void TraceStartup(string message)
    {
        try
        {
            var traceDirectory = Path.Combine(AppContext.BaseDirectory, "artifacts");
            Directory.CreateDirectory(traceDirectory);
            File.AppendAllText(
                Path.Combine(traceDirectory, "app-trace.log"),
                DateTimeOffset.Now.ToString("HH:mm:ss.fff") + "  " + message + Environment.NewLine);
        }
        catch
        {
            // Tracing must never take the app down.
        }
    }

    /// <summary>Writes a startup failure to a crash log, creating the directory if needed.</summary>
    /// <param name="exception">The failure.</param>
    /// <remarks>
    /// The regular logger does not exist yet when XAML loading fails, so this writes directly to the
    /// application's own portable data directory — the same rule as <see cref="AppPaths"/>: nothing
    /// is ever written outside the application's own folder.
    /// </remarks>
    private static void WriteStartupError(Exception exception)
    {
        try
        {
            var logDir = Path.Combine(AppContext.BaseDirectory, "Data", "Logs");
            Directory.CreateDirectory(logDir);
            File.WriteAllText(
                Path.Combine(logDir, "startup-error.log"),
                DateTimeOffset.Now + Environment.NewLine + exception);
        }
        catch
        {
            // Reporting a startup failure must never itself throw.
        }
    }

    /// <inheritdoc />
    protected override async void OnExit(ExitEventArgs e)
    {
        if (_shell is not null)
        {
            await _shell.ShutdownAsync().ConfigureAwait(true);
        }

        base.OnExit(e);
    }
}
