using System.Security.Claims;
using MarketPulse.Application.Abstractions;

namespace MarketPulse.Api;

public sealed class CurrentUser(IHttpContextAccessor accessor) : ICurrentUser
{
    public Guid UserId
    {
        get
        {
            var raw = accessor.HttpContext?.User.FindFirstValue(ClaimTypes.NameIdentifier);
            return Guid.TryParse(raw, out var id)
                ? id
                : throw new UnauthorizedAccessException("No authenticated user on the request.");
        }
    }
}
