namespace HamProgramAutoUpdate.Models;

/// <summary>Outcome of a single run block within a log file.</summary>
public enum RunStatus
{
    Unknown,
    Success,
    Failed,
    Running,
    Empty
}

/// <summary>One run block parsed out of an updater's log.</summary>
public sealed class RunInfo
{
    public DateTime? Timestamp { get; init; }
    public RunStatus Status { get; set; } = RunStatus.Unknown;
    public DateTime? UpdateTime { get; init; }
    public string? Error { get; set; }
    public int LineCount { get; init; }
}

/// <summary>Everything the dashboard knows about one tracked program.</summary>
public sealed class ProgramStatus
{
    public string Key { get; init; } = "";
    public string Name { get; init; } = "";
    public string LogPath { get; init; } = "";

    /// <summary>Where the actual ham radio program (not this updater) was
    /// found installed, if it was.</summary>
    public string? TargetInstallPath { get; init; }
    public bool TargetInstalled { get; init; }

    public List<RunInfo> Runs { get; init; } = new();

    public RunStatus LatestStatus { get; set; } = RunStatus.Unknown;
    public DateTime? LatestRunTime { get; set; }

    /// <summary>Last real update, from the log or from stored history.</summary>
    public DateTime? LastUpdate { get; set; }

    /// <summary>
    /// True when the displayed update date came from stored history because
    /// the log no longer contains it (rotated, cleared or truncated).
    /// </summary>
    public bool LastUpdateRemembered { get; set; }

    public string? ErrorMessage { get; set; }

    /// <summary>True when this dashboard currently has the updater running.</summary>
    public bool IsRunning { get; set; }
}
