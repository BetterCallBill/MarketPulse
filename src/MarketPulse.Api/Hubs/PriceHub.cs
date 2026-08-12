using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.SignalR;

namespace MarketPulse.Api.Hubs;

/// <summary>
/// Prices are public data, so every authenticated client gets the same broadcast —
/// but an anonymous connection is refused. The cookie rides the negotiate request and
/// the websocket handshake, so no query-string access token is needed.
/// </summary>
[Authorize]
public sealed class PriceHub : Hub;
