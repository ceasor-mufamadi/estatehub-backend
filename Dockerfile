# Build stage
FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src

COPY . .

RUN dotnet restore src/EstateHub.Api/EstateHub.Api.csproj
RUN dotnet publish src/EstateHub.Api/EstateHub.Api.csproj -c Release -o /app/publish --no-restore
# Mount point for the Data Protection key ring volume. Created here (the runtime image has no shell)
# and owned by the image's non-root user (uid 1654), so a fresh named volume is writable.
RUN mkdir -p /keys

# Runtime stage: Microsoft's "chiseled" image (SECURITY.md layer 10).
# No shell, no package manager, runs as a non-root user by default; the "-extra" variant
# carries tzdata + ICU so Africa/Johannesburg works.
FROM mcr.microsoft.com/dotnet/aspnet:10.0-noble-chiseled-extra
WORKDIR /app

ENV TZ=Africa/Johannesburg \
    ASPNETCORE_ENVIRONMENT=Production \
    DOTNET_CLI_TELEMETRY_OPTOUT=1

COPY --from=build /app/publish .
COPY --from=build --chown=1654:1654 /keys /keys

# 5268 = gRPC-Web (HTTP/1.1), 5269 = native gRPC (HTTP/2). Never published to the host in production:
# cloudflared/Caddy reach them over the Docker network.
EXPOSE 5268 5269

ENTRYPOINT ["dotnet", "EstateHub.Api.dll"]
