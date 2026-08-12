FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src

COPY Directory.Build.props Directory.Packages.props ./
COPY src/MarketPulse.Domain/*.csproj src/MarketPulse.Domain/
COPY src/MarketPulse.Application/*.csproj src/MarketPulse.Application/
COPY src/MarketPulse.Infrastructure/*.csproj src/MarketPulse.Infrastructure/
COPY src/MarketPulse.Api/*.csproj src/MarketPulse.Api/
RUN dotnet restore src/MarketPulse.Api/MarketPulse.Api.csproj

COPY src/ src/
RUN dotnet publish src/MarketPulse.Api/MarketPulse.Api.csproj -c Release -o /app

FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS runtime
WORKDIR /app
COPY --from=build /app .
COPY src/MarketPulse.Api/docker-entrypoint.sh .
RUN chmod +x docker-entrypoint.sh
EXPOSE 8080
ENTRYPOINT ["./docker-entrypoint.sh"]
