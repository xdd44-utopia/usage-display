namespace UsageDisplay;

sealed record LimitWindow(double UsedPercent, int? WindowMinutes, double? ResetsAt)
{
    public DateTimeOffset? ResetDate => ResetsAt is double seconds ? DateTimeOffset.UnixEpoch.AddSeconds(seconds) : null;

    public double RemainingPercent(DateTimeOffset? now = null)
    {
        if (ResetDate is { } reset && reset <= (now ?? DateTimeOffset.Now)) return 100;
        return Math.Clamp(100 - UsedPercent, 0, 100);
    }

    public string Label => WindowMinutes switch
    {
        null => "Limit",
        10080 => "Weekly",
        int m when m % 1440 == 0 => $"{m / 1440}-day",
        int m when m % 60 == 0 => $"{m / 60}-hour",
        int m => $"{m}-minute",
    };
}

sealed record ClaudeUsage(LimitWindow? FiveHour, LimitWindow? Weekly, string? PlanType, DateTimeOffset? FetchedAt)
{
    public IReadOnlyList<LimitWindow> Windows => new[] { FiveHour, Weekly }.OfType<LimitWindow>().ToList();
}

sealed record CodexUsage(IReadOnlyList<LimitWindow> Windows, string? PlanType, DateTimeOffset? FetchedAt,
                         BankedResets? BankedResets = null)
{
    public LimitWindow? Tightest => Windows.MinBy(w => w.RemainingPercent());
}

/// Full limit resets OpenAI grants, which the user can spend later.
/// Expirations are soonest first; null for a reset with no expiry. May list fewer than Count.
sealed record BankedResets(int Count, IReadOnlyList<DateTimeOffset?> Expirations);
