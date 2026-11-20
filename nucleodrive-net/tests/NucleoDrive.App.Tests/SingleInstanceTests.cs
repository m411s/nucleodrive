using NucleoDrive.App.Services;
using NucleoDrive.Tests;

namespace NucleoDrive.App.Tests;

public sealed class SingleInstanceTests
{
    [Fact]
    public async Task Second_launch_shows_the_window_but_a_second_tray_launch_does_not()
    {
        using var tmp = new TempDir();
        var socket = tmp["app.sock"];
        Assert.False(SingleInstance.TrySignalExisting(socket)); // nobody running yet

        var shown = 0;
        using var listener = SingleInstance.Listen(socket, () => Interlocked.Increment(ref shown));
        Assert.NotNull(listener);

        Assert.True(SingleInstance.TrySignalExisting(socket, show: false)); // `nucleodrive --tray` again
        await Task.Delay(300, TestContext.Current.CancellationToken);
        Assert.Equal(0, shown);

        Assert.True(SingleInstance.TrySignalExisting(socket)); // `nucleodrive` from the app menu
        var end = DateTime.UtcNow.AddSeconds(5);
        while (Volatile.Read(ref shown) == 0 && DateTime.UtcNow < end)
            await Task.Delay(50, TestContext.Current.CancellationToken);
        Assert.Equal(1, shown);
    }
}
