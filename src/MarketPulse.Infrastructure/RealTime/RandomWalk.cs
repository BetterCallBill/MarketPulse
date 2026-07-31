namespace MarketPulse.Infrastructure.RealTime;

public static class RandomWalk
{
    private const decimal MaxMovePercent = 0.01m;
    private const decimal Floor = 0.01m;

    /// <summary>Moves a price by at most ±1%, rounded to cents, never below one cent.</summary>
    public static decimal Next(decimal current, Random rng)
    {
        var drift = ((decimal)rng.NextDouble() * 2m - 1m) * MaxMovePercent;
        var candidate = Math.Round(current * (1m + drift), 2, MidpointRounding.AwayFromZero);

        var upperBound = current * (1m + MaxMovePercent);
        var lowerBound = current * (1m - MaxMovePercent);

        if (candidate > upperBound)
        {
            // Truncate (not round-to-nearest) so the clamp can never land back above the bound.
            candidate = Math.Truncate(upperBound * 100m) / 100m;
        }
        else if (candidate < lowerBound)
        {
            // Ceiling (not round-to-nearest) so the clamp can never land back below the bound.
            candidate = Math.Ceiling(lowerBound * 100m) / 100m;
        }

        return candidate < Floor ? Floor : candidate;
    }
}
