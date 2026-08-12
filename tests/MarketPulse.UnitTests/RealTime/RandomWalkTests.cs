using MarketPulse.Infrastructure.RealTime;

namespace MarketPulse.UnitTests.RealTime;

public class RandomWalkTests
{
    [Fact]
    public void Next_stays_within_one_percent_of_the_current_price()
    {
        var rng = new Random(Seed: 42);
        var current = 100m;

        for (var i = 0; i < 1000; i++)
        {
            var next = RandomWalk.Next(current, rng);

            Assert.InRange(next, current * 0.99m, current * 1.01m);
            current = next;
        }
    }

    [Fact]
    public void Next_never_returns_a_non_positive_price()
    {
        var rng = new Random(Seed: 7);
        var current = 0.02m;

        for (var i = 0; i < 500; i++)
        {
            current = RandomWalk.Next(current, rng);
            Assert.True(current > 0m);
        }
    }

    [Fact]
    public void Next_rounds_to_two_decimal_places()
    {
        var rng = new Random(Seed: 1);

        var next = RandomWalk.Next(62.10m, rng);

        Assert.Equal(next, Math.Round(next, 2));
    }
}
