using System.ComponentModel.DataAnnotations;

namespace MarketPulse.Application.Configuration;

public sealed class OtelOptions
{
    public const string SectionName = "Otel";

    /// <summary>OTLP/gRPC endpoint. The Aspire dashboard listens here in docker-compose.</summary>
    [Required]
    public string OtlpEndpoint { get; init; } = "http://localhost:4317";
}
