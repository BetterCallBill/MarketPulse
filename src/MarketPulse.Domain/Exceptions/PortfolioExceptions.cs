namespace MarketPulse.Domain.Exceptions;

public sealed class InsufficientHoldingsException(decimal held, decimal requested)
    : DomainException(
        $"Cannot sell {requested} units: only {held} held.")
{
    public override string ErrorCode => "insufficient-holdings";
    public override int StatusCode => 422;
}

public sealed class InvalidTradeException(string reason)
    : DomainException(reason)
{
    public override string ErrorCode => "invalid-trade";
    public override int StatusCode => 400;
}
