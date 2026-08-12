namespace MarketPulse.Application.Authentication;

/// <summary>Security events carry evidence, not credentials: first char + domain only.</summary>
public static class EmailMasking
{
    public static string Mask(string email)
    {
        var at = email.IndexOf('@');
        var first = email.Length > 0 ? email[..1] : "?";
        return at > 0 ? $"{first}***{email[at..]}" : $"{first}***";
    }
}
