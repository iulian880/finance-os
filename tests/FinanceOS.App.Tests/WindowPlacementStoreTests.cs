using System.IO;
using System.Runtime.ExceptionServices;
using System.Windows.Interop;
using System.Windows.Threading;
using FinanceOS.Platform.Windows.Windowing;
using Xunit;

namespace FinanceOS.App.Tests;

public sealed class WindowPlacementStoreTests : IDisposable
{
    private readonly string _folder = Path.Combine(Path.GetTempPath(), "FinanceOS.Tests", Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_folder))
        {
            Directory.Delete(_folder, recursive: true);
        }
    }

    [Fact]
    public void Missing_file_means_default_placement()
    {
        Assert.Null(Store().Load());
    }

    [Fact]
    public void Saved_placement_round_trips_and_creates_the_folder()
    {
        var store = Store(Path.Combine("nested", "deeper", "window.json"));
        var placement = new SavedWindowPlacement(-1800, 40, -200, 1000, IsMaximized: true);

        store.Save(placement);

        Assert.Equal(placement, store.Load());
        Assert.False(File.Exists(store.FilePath + ".tmp"), "temporary file left behind");
    }

    [Fact]
    public void Saving_again_replaces_the_previous_placement()
    {
        var store = Store();
        store.Save(new SavedWindowPlacement(0, 0, 800, 600, false));
        store.Save(new SavedWindowPlacement(10, 20, 1210, 920, false));

        Assert.Equal(new SavedWindowPlacement(10, 20, 1210, 920, false), store.Load());
    }

    [Theory]
    [InlineData("")]
    [InlineData("{ not json")]
    [InlineData("null")]
    [InlineData("[1, 2, 3]")]
    [InlineData("""{"Left":"wide"}""")]
    public void Corrupt_file_means_default_placement(string content)
    {
        var store = Store();
        Directory.CreateDirectory(_folder);
        File.WriteAllText(store.FilePath, content);

        Assert.Null(store.Load());
    }

    [Theory]
    [InlineData(100, 100, 100, 500)] // zero width
    [InlineData(100, 100, 900, 100)] // zero height
    [InlineData(900, 100, 100, 700)] // inverted
    public void Empty_or_inverted_bounds_are_ignored(int left, int top, int right, int bottom)
    {
        var store = Store();
        store.Save(new SavedWindowPlacement(left, top, right, bottom, false));

        Assert.Null(store.Load());
    }

    [Fact]
    public void Unwritable_location_never_throws()
    {
        // The "folder" is an existing file, so creating the directory fails with IOException.
        Directory.CreateDirectory(_folder);
        var blocker = Path.Combine(_folder, "blocker");
        File.WriteAllText(blocker, "");
        var store = new WindowPlacementStore(Path.Combine(blocker, "window.json"));

        store.Save(new SavedWindowPlacement(0, 0, 800, 600, false));

        Assert.Null(store.Load());
    }

    [Fact]
    public void Default_store_lives_in_the_user_data_folder()
    {
        Assert.Equal(Path.Combine(Platform.Windows.AppPaths.DataRoot, "window.json"), new WindowPlacementStore().FilePath);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Apply_then_capture_returns_the_same_restored_bounds(bool maximized)
    {
        // Real HWND on the primary monitor: verifies the P/Invoke structs, not just the JSON.
        var wanted = new SavedWindowPlacement(120, 140, 920, 740, maximized);
        SavedWindowPlacement? captured = null;

        RunOnSta(() =>
        {
            using var window = new HwndSource(new HwndSourceParameters("FinanceOS placement test", 300, 200)
            {
                WindowStyle = unchecked((int)0x00CF0000), // WS_OVERLAPPEDWINDOW, not yet visible
            });

            WindowPlacementStore.Apply(window.Handle, wanted);
            captured = WindowPlacementStore.Capture(window.Handle);
        });

        Assert.Equal(wanted, captured);
    }

    [Fact]
    public void Capture_of_an_invalid_handle_returns_null()
    {
        Assert.Null(WindowPlacementStore.Capture(IntPtr.Zero));
    }

    private WindowPlacementStore Store(string relative = "window.json") => new(Path.Combine(_folder, relative));

    private static void RunOnSta(Action action)
    {
        ExceptionDispatchInfo? failure = null;
        var thread = new Thread(() =>
        {
            try
            {
                action();
            }
            catch (Exception ex)
            {
                failure = ExceptionDispatchInfo.Capture(ex);
            }
            finally
            {
                Dispatcher.CurrentDispatcher.InvokeShutdown();
            }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join();
        failure?.Throw();
    }
}
