using HamProgramAutoUpdate.Services.Updaters.Shared;
using Xunit;

namespace HamProgramAutoUpdate.Tests;

public class DesktopShortcutCleanerTests
{
    [Fact]
    public async Task RemoveAllNew_KeepsShortcutsThatExistedAndRemovesNewOnes()
    {
        var desktop = Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory);
        var id = Guid.NewGuid().ToString("N");
        var existing = Path.Combine(desktop, $"zz-test-existing-{id}.lnk");
        var added = Path.Combine(desktop, $"zz-test-added-{id}.lnk");
        File.WriteAllText(existing, "x");
        try
        {
            var before = DesktopShortcutCleaner.SnapshotAll();
            File.WriteAllText(added, "x");

            var removed = await DesktopShortcutCleaner.RemoveAllNewWithDelayAsync(before, TimeSpan.Zero);

            Assert.True(File.Exists(existing));
            Assert.False(File.Exists(added));
            Assert.Contains(Path.GetFileName(added), removed);
        }
        finally
        {
            try { File.Delete(existing); } catch (Exception) { }
            try { File.Delete(added); } catch (Exception) { }
        }
    }
}
