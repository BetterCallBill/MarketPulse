#!/bin/sh
set -e

# In AWS the task definition injects DB_HOST plus DB_USERNAME/DB_PASSWORD
# from Secrets Manager; the full connection string is composed here so the
# password never sits in a task definition. Locally DB_HOST is unset and
# configuration comes from appsettings/environment as always.
if [ -n "$DB_HOST" ]; then
  export ConnectionStrings__MarketPulse="Server=${DB_HOST},1433;Database=MarketPulse;User Id=${DB_USERNAME};Password=${DB_PASSWORD};TrustServerCertificate=True"
fi

exec dotnet MarketPulse.Alerts.dll
