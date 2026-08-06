using MarketPulse.Application.Authentication;

namespace MarketPulse.UnitTests.Application;

public class SecurityEventTests
{
    [Theory]
    [InlineData("alice@example.com", "a***@example.com")]
    [InlineData("x@y.z", "x***@y.z")]
    [InlineData("weird", "w***")]
    public void Mask_never_reveals_the_local_part(string email, string expected) =>
        Assert.Equal(expected, EmailMasking.Mask(email));
}
