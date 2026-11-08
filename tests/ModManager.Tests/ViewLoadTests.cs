using System.Threading;
using System.Windows;
using ModManager.App;
using ModManager.App.Theme;
using ModManager.App.ViewModels;
using ModManager.App.Views;
using Xunit;

namespace ModManager.Tests;

/// <summary>
/// Loads every XAML view with the application's real resource dictionaries.
/// </summary>
/// <remarks>
/// A view whose XAML references a missing resource or a mistyped binding target only fails when the
/// page is actually opened — which no headless test would otherwise cover, and which the smoke run
/// cannot reach without a UI driver. Constructing each view on an STA thread is the cheapest honest
/// substitute: it exercises the same BAML load and StaticResource resolution the app performs.
/// </remarks>
public sealed class ViewLoadTests
{
    /// <summary>Every view instantiates cleanly against the shipped theme.</summary>
    [Fact]
    public void EveryViewLoadsWithTheApplicationResources()
    {
        Exception? failure = null;
        using var done = new ManualResetEventSlim(false);

        var thread = new Thread(() =>
        {
            try
            {
                if (Application.Current is null)
                {
                    var app = new ModManager.App.App();
                    app.InitializeComponent();
                }

                _ = new DashboardView();
                _ = new GamesView();
                _ = new OnlineModsView();
                _ = new ModsView();
                _ = new LoadOrderView();
                _ = new DownloadsView();
                _ = new ProfilesView();
                _ = new ConflictsView();
                _ = new SettingsView();
                _ = new LogsView();
                _ = new LaunchSettingsWindow("Test Game", null, null, null);
            }
            catch (Exception ex)
            {
                failure = ex;
            }
            finally
            {
                done.Set();
            }
        });

        thread.SetApartmentState(ApartmentState.STA);
        thread.IsBackground = true;
        thread.Start();

        Assert.True(done.Wait(TimeSpan.FromSeconds(60)), "Loading the views timed out.");

        if (failure is not null)
        {
            Assert.Fail("A view failed to load: " + failure);
        }
    }

    /// <summary>
    /// Attaches real view models and forces a layout pass, so every binding is actually evaluated.
    /// </summary>
    /// <remarks>
    /// Loading a view only proves its XAML parses; not one binding is evaluated, and a binding that throws is
    /// invisible. A launch smoke run caught exactly that class of defect — a <c>ProgressBar.Value</c> bound to
    /// a read-only property, which WPF treats as TwoWay by default and which crashed the application the
    /// moment the Settings page was rendered. This test exists so the next one fails here instead.
    /// </remarks>
    [Fact]
    public async Task PagesEvaluateTheirBindingsAgainstARealViewModel()
    {
        Exception? failure = null;
        using var done = new ManualResetEventSlim(false);

        // Built off the UI thread: the host awaits internally, and awaiting on an STA thread with a
        // dispatcher installed is a deadlock waiting to happen.
        var host = await Task.Run(ApplicationHost.CreateAsync);

        var thread = new Thread(() =>
        {
            try
            {
                if (Application.Current is null)
                {
                    var app = new ModManager.App.App();
                    app.InitializeComponent();
                }

                LayOut(new SettingsView { DataContext = new SettingsViewModel(host) });
                LayOut(new OnlineModsView { DataContext = new OnlineModsViewModel(host) });
                LayOut(new LogsView { DataContext = new LogsViewModel(host) });
            }
            catch (Exception ex)
            {
                failure = ex;
            }
            finally
            {
                done.Set();
            }
        });

        thread.SetApartmentState(ApartmentState.STA);
        thread.IsBackground = true;
        thread.Start();

        Assert.True(done.Wait(TimeSpan.FromSeconds(120)), "Evaluating the page bindings timed out.");

        if (failure is not null)
        {
            Assert.Fail("A page failed to bind against its view model: " + failure);
        }
    }

    /// <summary>Forces a full layout pass, which is what evaluates the bindings.</summary>
    /// <param name="element">Element to lay out.</param>
    private static void LayOut(FrameworkElement element)
    {
        element.Measure(new Size(1400, 900));
        element.Arrange(new Rect(0, 0, 1400, 900));
        element.UpdateLayout();
    }

    /// <summary>
    /// Switching theme actually republishes the palette.
    /// </summary>
    /// <remarks>
    /// This is the exact defect the dark mode shipped with: the switch ran without error and changed nothing
    /// on screen, because WPF freezes a brush that a sealed <c>Style</c> references, so mutating the original
    /// instance was a silent no-op that no "did it throw?" check could catch. Asserting on the live resource
    /// is what turns that into a failing test instead of a screenshot nobody looked at closely enough.
    /// </remarks>
    [Fact]
    public void SwitchingThemesRepublishesThePalette()
    {
        Exception? failure = null;
        using var done = new ManualResetEventSlim(false);

        var thread = new Thread(() =>
        {
            try
            {
                if (Application.Current is null)
                {
                    var app = new ModManager.App.App();
                    app.InitializeComponent();
                }

                var light = ReadBackground();

                AppearanceService.Apply(true);
                var dark = ReadBackground();

                Assert.True(AppearanceService.IsDark, "The service did not record the dark palette.");
                Assert.NotEqual(light, dark);

                AppearanceService.Apply(false);
                var restored = ReadBackground();

                Assert.False(AppearanceService.IsDark, "The service did not record the light palette.");
                Assert.Equal(light, restored);
            }
            catch (Exception ex)
            {
                failure = ex;
            }
            finally
            {
                done.Set();
            }
        });

        thread.SetApartmentState(ApartmentState.STA);
        thread.IsBackground = true;
        thread.Start();

        Assert.True(done.Wait(TimeSpan.FromSeconds(60)), "Switching the theme timed out.");

        if (failure is not null)
        {
            Assert.Fail("Switching the theme did not republish the palette: " + failure);
        }
    }

    /// <summary>Reads the live background colour from the application resources.</summary>
    private static System.Windows.Media.Color ReadBackground() =>
        Application.Current?.TryFindResource("BackgroundBrush") is System.Windows.Media.SolidColorBrush brush
            ? brush.Color
            : throw new InvalidOperationException("BackgroundBrush is not available in application resources.");
}
