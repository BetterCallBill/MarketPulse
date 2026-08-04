namespace MarketPulse.Domain.Exceptions;

public sealed class InvalidThresholdException()
    : DomainException("An alert threshold must be greater than zero.")
{
    public override string ErrorCode => "invalid-threshold";
    public override int StatusCode => 400;
}

public sealed class UnknownTickerException(string ticker)
    : DomainException($"'{ticker}' is not a known ticker.")
{
    public override string ErrorCode => "unknown-ticker";
    public override int StatusCode => 400;
}

public sealed class AlertRuleLimitException(int max)
    : DomainException($"A user may hold at most {max} alert rules.")
{
    public override string ErrorCode => "alert-limit-reached";
}

public sealed class DuplicateAlertRuleException(string ticker)
    : DomainException($"An identical active alert already exists for '{ticker}'.")
{
    public override string ErrorCode => "duplicate-alert-rule";
}

/// <summary>
/// Also raised when the rule belongs to another user. Answering 404 rather than 403 keeps
/// the endpoint from confirming that someone else's rule id exists.
/// </summary>
public sealed class AlertRuleNotFoundException()
    : DomainException("No such alert rule.")
{
    public override string ErrorCode => "alert-rule-not-found";
    public override int StatusCode => 404;
}

public sealed class AlertNotTriggeredException()
    : DomainException("Only a triggered alert can be re-armed.")
{
    public override string ErrorCode => "alert-not-triggered";
}

public sealed class AlertNotActiveException()
    : DomainException("Only an active alert can be triggered.")
{
    public override string ErrorCode => "alert-not-active";
}

public sealed class NotificationNotFoundException()
    : DomainException("No such notification.")
{
    public override string ErrorCode => "notification-not-found";
    public override int StatusCode => 404;
}
