using HamProgramAutoUpdate.Services;

namespace HamProgramAutoUpdate.Tests;

public class UpdateSnoozeTests
{
    private static readonly DateTime Now = new(2026, 10, 16, 9, 0, 0);

    private static string TempFile() =>
        Path.Combine(Path.GetTempPath(), $"snoozetest_{Guid.NewGuid():N}", "snooze.json");

    [Fact]
    public void NoFile_NothingIsSnoozed()
    {
        Assert.Null(UpdateSnooze.SnoozedUntil("n1mm", Now, TempFile()));
    }

    [Fact]
    public void Set_ThenSnoozedUntil_ReturnsTheTimeUntilItPasses()
    {
        var path = TempFile();
        var until = new DateTime(2026, 10, 18, 12, 0, 0);

        Assert.Null(UpdateSnooze.Set("n1mm", until, Now, path));

        Assert.Equal(until, UpdateSnooze.SnoozedUntil("n1mm", Now, path));
        Assert.Equal(until, UpdateSnooze.SnoozedUntil("N1MM", Now, path)); // key is case-insensitive
        Assert.Null(UpdateSnooze.SnoozedUntil("n1mm", until, path));       // expired exactly at the time
        Assert.Null(UpdateSnooze.SnoozedUntil("n1mm", until.AddMinutes(1), path));
    }

    [Fact]
    public void Set_KeepsOtherProgramsAndDropsExpiredOnes()
    {
        var path = TempFile();
        UpdateSnooze.Set("tqsl", Now.AddDays(1), Now, path);
        UpdateSnooze.Set("old", Now.AddHours(1), Now, path);

        // Later, "old" has expired by the time another snooze is saved.
        var later = Now.AddHours(2);
        UpdateSnooze.Set("n1mm", later.AddDays(2), later, path);

        Assert.NotNull(UpdateSnooze.SnoozedUntil("tqsl", later, path));
        Assert.NotNull(UpdateSnooze.SnoozedUntil("n1mm", later, path));
        Assert.DoesNotContain("old", File.ReadAllText(path));
    }

    [Fact]
    public void Clear_RemovesOnlyThatProgram()
    {
        var path = TempFile();
        UpdateSnooze.Set("n1mm", Now.AddDays(2), Now, path);
        UpdateSnooze.Set("tqsl", Now.AddDays(2), Now, path);

        Assert.Null(UpdateSnooze.Clear("n1mm", Now, path));

        Assert.Null(UpdateSnooze.SnoozedUntil("n1mm", Now, path));
        Assert.NotNull(UpdateSnooze.SnoozedUntil("tqsl", Now, path));
    }

    [Fact]
    public void HandWrittenFile_IsUnderstood()
    {
        var path = TempFile();
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, "{\"n1mm\": \"2026-10-18T12:00:00\"}");

        Assert.Equal(new DateTime(2026, 10, 18, 12, 0, 0), UpdateSnooze.SnoozedUntil("n1mm", Now, path));
    }

    [Theory]
    [InlineData("not json at all")]
    [InlineData("[]")]
    [InlineData("{\"n1mm\": \"not a date\"}")]
    [InlineData("")]
    public void CorruptFile_MeansNothingIsSnoozed(string content)
    {
        var path = TempFile();
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, content);

        Assert.Null(UpdateSnooze.SnoozedUntil("n1mm", Now, path));
    }
}
