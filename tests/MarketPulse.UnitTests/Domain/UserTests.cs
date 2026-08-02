using MarketPulse.Domain.Entities;

namespace MarketPulse.UnitTests.Domain;

public class UserTests
{
    private static readonly DateTimeOffset Now = new(2026, 8, 3, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public void Register_lowercases_the_email()
    {
        var user = User.Register("Dev@MarketPulse.Local", "hash");

        Assert.Equal("dev@marketpulse.local", user.Email);
    }

    [Fact]
    public void A_new_user_is_not_locked_out()
    {
        var user = User.Register("a@b.com", "hash");

        Assert.False(user.IsLockedOut(Now));
        Assert.Equal(0, user.FailedLoginCount);
    }

    [Fact]
    public void Failures_below_the_threshold_do_not_lock_the_account()
    {
        var user = User.Register("a@b.com", "hash");

        for (var i = 0; i < 4; i++)
        {
            user.RecordFailedLogin(Now, maxAttempts: 5, lockoutDuration: TimeSpan.FromMinutes(15));
        }

        Assert.Equal(4, user.FailedLoginCount);
        Assert.False(user.IsLockedOut(Now));
    }

    [Fact]
    public void The_fifth_consecutive_failure_locks_the_account()
    {
        var user = User.Register("a@b.com", "hash");

        for (var i = 0; i < 5; i++)
        {
            user.RecordFailedLogin(Now, maxAttempts: 5, lockoutDuration: TimeSpan.FromMinutes(15));
        }

        Assert.True(user.IsLockedOut(Now));
        Assert.Equal(Now.AddMinutes(15), user.LockoutEndUtc);
    }

    [Fact]
    public void The_lockout_expires()
    {
        var user = User.Register("a@b.com", "hash");
        for (var i = 0; i < 5; i++)
        {
            user.RecordFailedLogin(Now, maxAttempts: 5, lockoutDuration: TimeSpan.FromMinutes(15));
        }

        Assert.False(user.IsLockedOut(Now.AddMinutes(16)));
    }

    [Fact]
    public void A_successful_login_resets_the_counter_and_the_lockout()
    {
        var user = User.Register("a@b.com", "hash");
        for (var i = 0; i < 5; i++)
        {
            user.RecordFailedLogin(Now, maxAttempts: 5, lockoutDuration: TimeSpan.FromMinutes(15));
        }

        user.RecordSuccessfulLogin();

        Assert.Equal(0, user.FailedLoginCount);
        Assert.Null(user.LockoutEndUtc);
        Assert.False(user.IsLockedOut(Now));
    }
}
