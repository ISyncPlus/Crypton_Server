# Crypton API — build and runtime image.
# Build from the repository root:  docker build -t crypton-api .
FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src

# Restore first so that a code-only change reuses the cached package layer.
COPY global.json Directory.Build.props Directory.Packages.props Crypton.slnx ./
COPY src/Crypton.Core/Crypton.Core.csproj src/Crypton.Core/
COPY src/Crypton.Integrations/Crypton.Integrations.csproj src/Crypton.Integrations/
COPY src/Crypton.Api/Crypton.Api.csproj src/Crypton.Api/
RUN dotnet restore src/Crypton.Api/Crypton.Api.csproj

COPY src/ src/
RUN dotnet publish src/Crypton.Api/Crypton.Api.csproj -c Release -o /app --no-restore /p:UseAppHost=false

FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS runtime
# The reporting time zone (Africa/Lagos) is resolved through the OS time zone database.
RUN apt-get update \
    && apt-get install -y --no-install-recommends tzdata curl \
    && rm -rf /var/lib/apt/lists/*

WORKDIR /app
COPY --from=build /app ./

# Uploaded KYC documents and the data-protection keys live here; mount a volume so they survive a redeploy.
RUN mkdir -p /app/data/keys /app/data/uploads && chown -R $APP_UID:$APP_UID /app/data
VOLUME ["/app/data"]

ENV ASPNETCORE_HTTP_PORTS=8080 \
    DOTNET_gcServer=1
EXPOSE 8080
USER $APP_UID

HEALTHCHECK --interval=30s --timeout=5s --start-period=30s --retries=5 \
    CMD curl -fsS http://localhost:8080/health/ready || exit 1

ENTRYPOINT ["dotnet", "Crypton.Api.dll"]
