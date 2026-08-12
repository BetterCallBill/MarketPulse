using System.ComponentModel.DataAnnotations;

namespace MarketPulse.Application.Configuration;

public sealed class HistoryOptions
{
    public const string SectionName = "History";

    [Range(1, 365)]
    public int RetentionDays { get; init; } = 7;

    [Range(1, 3600)]
    public int FlushIntervalSeconds { get; init; } = 5;

    /// <summary>≤ 600: each row costs 3 parameters and SQL Server caps a command at 2,100.</summary>
    [Range(1, 600)]
    public int FlushBatchSize { get; init; } = 500;

    [Range(100, 100_000)]
    public int BufferCapacity { get; init; } = 5000;

    [Range(10, 10_000)]
    public int MaxCandleBuckets { get; init; } = 1000;

    [Range(1, 1440)]
    public int SparklineWindowMinutes { get; init; } = 60;

    [Range(1, 86_400)]
    public int RetentionSweepIntervalSeconds { get; init; } = 3600;

    [Range(1000, 100_000)]
    public int RetentionDeleteChunk { get; init; } = 10_000;
}
