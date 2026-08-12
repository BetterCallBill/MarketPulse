using MarketPulse.Application.Authentication;

namespace MarketPulse.UnitTests.Application;

public class PasswordPolicyTests
{
    [Fact]
    public void The_minimum_length_is_twelve()
    {
        Assert.Equal(12, PasswordPolicy.MinimumLength);
    }

    [Theory]
    [InlineData("short")]
    [InlineData("elevenchars")]
    public void Passwords_below_the_minimum_are_rejected(string password)
    {
        Assert.False(PasswordPolicy.IsAcceptable(password));
    }

    [Fact]
    public void A_long_passphrase_with_no_symbols_is_accepted()
    {
        // NIST SP 800-63B: length is the control that matters; composition rules are
        // explicitly discouraged. This must pass, or we have re-implemented the thing
        // the spec rejected.
        Assert.True(PasswordPolicy.IsAcceptable("correct horse battery staple"));
    }

    [Theory]
    [InlineData("unbelievable")]
    [InlineData("UNBELIEVABLE")]
    public void Blocklisted_passwords_are_rejected_case_insensitively(string password)
    {
        Assert.False(PasswordPolicy.IsAcceptable(password));
    }

    [Fact]
    public void The_blocklist_actually_loaded()
    {
        Assert.True(PasswordPolicy.BlocklistSize > 5000,
            $"Blocklist only has {PasswordPolicy.BlocklistSize} entries — the embedded " +
            "resource probably did not load.");
    }

    [Fact]
    public void A_short_blocklisted_password_is_rejected_on_length_before_the_blocklist_matters()
    {
        // Documents a real limitation: of the 10,000 most common passwords, only 10 are
        // long enough to reach the blocklist at all. The 12-character minimum is doing
        // nearly all the work here — the blocklist earns its place only if that floor
        // ever drops. See the slice 2 spec's password-policy section.
        Assert.False(PasswordPolicy.IsAcceptable("password"));
        Assert.True("password".Length < PasswordPolicy.MinimumLength);
    }
}
