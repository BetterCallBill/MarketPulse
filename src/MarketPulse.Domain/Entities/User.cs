namespace MarketPulse.Domain.Entities;

public sealed class User
{
    public Guid Id { get; private set; }
    public string Email { get; private set; } = string.Empty;

    private User() { }

    public User(Guid id, string email)
    {
        Id = id;
        Email = email;
    }
}
