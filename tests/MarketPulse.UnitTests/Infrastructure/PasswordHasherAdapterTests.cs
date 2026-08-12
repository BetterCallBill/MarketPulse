using MarketPulse.Infrastructure.Authentication;

namespace MarketPulse.UnitTests.Infrastructure;

public class PasswordHasherAdapterTests
{
    private readonly PasswordHasherAdapter _hasher = new();

    [Fact]
    public void A_hash_verifies_against_its_own_password()
    {
        var hash = _hasher.Hash("correct horse battery staple");

        Assert.True(_hasher.Verify(hash, "correct horse battery staple"));
    }

    [Fact]
    public void A_hash_does_not_verify_against_a_different_password()
    {
        var hash = _hasher.Hash("correct horse battery staple");

        Assert.False(_hasher.Verify(hash, "incorrect horse battery staple"));
    }

    [Fact]
    public void The_same_password_hashes_differently_each_time()
    {
        // Per-hash random salt. Two identical passwords must not produce identical hashes,
        // or the database leaks which accounts share a password.
        Assert.NotEqual(_hasher.Hash("correct horse battery staple"),
                        _hasher.Hash("correct horse battery staple"));
    }

    [Fact]
    public void A_malformed_hash_returns_false_rather_than_throwing()
    {
        Assert.False(_hasher.Verify("not-a-real-hash", "anything at all"));
    }
}
