using Dapper;
using MarketPulse.Application.Abstractions;
using Microsoft.Extensions.DependencyInjection;

namespace MarketPulse.IntegrationTests;

[Collection(nameof(SqlServerCollection))]
public class SqlConnectionFactoryTests(SqlServerFixture fixture)
{
    [Fact]
    public async Task Opens_a_connection_to_the_test_database()
    {
        using var factory = TestFactory.Create(fixture);
        var connections = factory.Services.GetRequiredService<ISqlConnectionFactory>();

        await using var connection = await connections.OpenAsync(CancellationToken.None);
        var one = await connection.QuerySingleAsync<int>("SELECT 1");

        Assert.Equal(1, one);
    }
}
