namespace GlobalGraffitiWall.API;

public class CanvasSeason
{
    public int SeasonId { get; set; }
    public int SeasonNumber { get; set; }
    public string Name { get; set; } = string.Empty;
    public DateTimeOffset StartedAt { get; set; }
    public DateTimeOffset? EndedAt { get; set; }
    public string? ResetBy { get; set; }
    public string? ResetReason { get; set; }
    public long TotalPixelsPlaced { get; set; }
    public string? ArchiveExportUrl { get; set; }
}

public class CanvasScheduledReset
{
    public int Id { get; set; }
    public DateTimeOffset ScheduledResetUtc { get; set; }
    public string ScheduledBy { get; set; } = string.Empty;
    public string? AnnouncementMessage { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public bool IsCancelled { get; set; }
    public DateTimeOffset? CancelledAt { get; set; }
    public string? CancelledBy { get; set; }
    public bool IsExecuted { get; set; }
    public DateTimeOffset? ExecutedAt { get; set; }
}

public class ScheduleResetRequest
{
    public DateTimeOffset ScheduledResetUtc { get; set; }
    public string? AnnouncementMessage { get; set; }
}

public class ExecuteResetNowRequest
{
    public string? Reason { get; set; }
}

public class CanvasResetStatusDto
{
    public bool IsResetScheduled { get; set; }
    public DateTimeOffset? ScheduledResetUtc { get; set; }
    public int RemainingSeconds { get; set; }
    public string? Message { get; set; }
    public string? ScheduledBy { get; set; }
    public int CurrentSeasonNumber { get; set; } = 1;
    public string CurrentSeasonName { get; set; } = "Season 1: Genesis";
    public DateTimeOffset CurrentSeasonStartedAt { get; set; }
}
