namespace MarketPulse.Application.Abstractions;

public interface IPasswordHasher
{
    string Hash(string password);

    /// <summary>Constant-time verification. Returns false rather than throwing on a malformed hash.</summary>
    bool Verify(string hash, string password);
}
