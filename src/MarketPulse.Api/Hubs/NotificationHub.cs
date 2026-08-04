using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.SignalR;

namespace MarketPulse.Api.Hubs;

/// <summary>
/// Private, per-user delivery — the counterpart to <see cref="PriceHub"/>, which broadcasts
/// public data to everyone. Two data classifications, two hubs, so the distinction is
/// structural rather than a convention someone has to remember.
///
/// No group management is needed: SignalR's default IUserIdProvider reads
/// ClaimTypes.NameIdentifier, which is the same claim CurrentUser reads and the same claim
/// the JWT's `sub` is mapped to. Clients.User(userId) therefore already means "this user's
/// connections, wherever they are".
/// </summary>
[Authorize]
public sealed class NotificationHub : Hub;
