namespace MarketPulse.Domain.Entities;

/// <summary>
/// One claimed idempotency key. Claimed-first: the row is inserted before the action
/// executes (the unique index is the arbiter under concurrency), completed with the
/// response after success, and removed on failure — a failed request must be safe to
/// retry under the same key. Null response fields mean "claimed, still executing".
/// </summary>
public sealed class IdempotencyKey
{
    public Guid Id { get; private set; }
    public Guid UserId { get; private set; }
    public string Endpoint { get; private set; } = string.Empty;
    public string Key { get; private set; } = string.Empty;
    public string RequestHash { get; private set; } = string.Empty;
    public int? ResponseStatusCode { get; private set; }
    public string? ResponseBody { get; private set; }
    public DateTimeOffset CreatedUtc { get; private set; }

    private IdempotencyKey() { }

    public IdempotencyKey(Guid userId, string endpoint, string key, string requestHash, DateTimeOffset now)
    {
        Id = Guid.NewGuid();
        UserId = userId;
        Endpoint = endpoint;
        Key = key;
        RequestHash = requestHash;
        CreatedUtc = now;
    }

    public bool IsCompleted => ResponseStatusCode is not null;

    public void Complete(int statusCode, string body)
    {
        ResponseStatusCode = statusCode;
        ResponseBody = body;
    }
}
