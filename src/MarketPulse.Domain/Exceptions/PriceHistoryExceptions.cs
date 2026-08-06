namespace MarketPulse.Domain.Exceptions;

/// <summary>
/// 404, not the alerts path's 400: there the unknown ticker arrives in a request body and
/// is a validation failure; here it names the resource in the path, and a resource that
/// does not exist is a 404. Same slug both ways — clients branch on the code, and the code
/// means the same thing.
/// </summary>
public sealed class TickerNotFoundException(string ticker)
    : DomainException($"Ticker '{ticker}' is not a known instrument.")
{
    public override string ErrorCode => "unknown-ticker";
    public override int StatusCode => 404;
}
