# syntax=docker/dockerfile:1
# ---------------------------------------------------------------------------
# Comments.Api — multi-stage image (Clean Architecture backend, stage 2).
#
# Build context = REPOSITORY ROOT (so paths below start with src/Backend/...):
#   docker build -f src/Backend/Comments.Api/Dockerfile -t comments-spa-api .
#
#   stage 1 (sdk)    : restore + publish Comments.Api (net10.0)
#   stage 2 (aspnet) : minimal, non-root, curl healthcheck on /api/health
#
# The Angular SPA is served by nginx (src/Frontend), NOT by this image.
# ---------------------------------------------------------------------------

# ------------------------------- build -------------------------------------
FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src

# 1) Restore first (layer cache): copy every csproj of the dependency chain.
COPY src/Backend/Comments.Domain/Comments.Domain.csproj src/Backend/Comments.Domain/
COPY src/Backend/Comments.Application/Comments.Application.csproj src/Backend/Comments.Application/
COPY src/Backend/Comments.Infrastructure/Comments.Infrastructure.csproj src/Backend/Comments.Infrastructure/
COPY src/Backend/Comments.Api/Comments.Api.csproj src/Backend/Comments.Api/
RUN dotnet restore src/Backend/Comments.Api/Comments.Api.csproj

# 2) Sources. Frontend is deliberately not copied here.
COPY src/Backend/ src/Backend/

# 3) Publish into a clean folder.
RUN dotnet publish src/Backend/Comments.Api/Comments.Api.csproj \
      -c Release \
      -o /app/publish \
      /p:UseAppHost=false \
      --no-restore

# ------------------------------ runtime ------------------------------------
FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS runtime

# curl is used by HEALTHCHECK; the base image does not ship it.
RUN apt-get update \
 && apt-get install -y --no-install-recommends curl \
 && rm -rf /var/lib/apt/lists/*

WORKDIR /app
COPY --chown=app:app --from=build /app/publish .

ENV ASPNETCORE_URLS=http://+:8080 \
    ASPNETCORE_ENVIRONMENT=Production \
    DOTNET_EnableDiagnostics=0 \
    Storage__Root=/app/storage \
    Features__DevCaptchaPeek=false \
    Features__Seed=false

# Writable state for uploads, owned by the non-root `app` user that ships with
# the aspnet image. The app needs write access to the storage root.
RUN mkdir -p /app/storage \
 && chown -R app:app /app \
 && chmod -R u+rwX,go+rX /app

USER app
EXPOSE 8080

HEALTHCHECK --interval=30s --timeout=5s --start-period=40s --retries=5 \
    CMD curl -fsS http://localhost:8080/api/health || exit 1

ENTRYPOINT ["dotnet", "Comments.Api.dll"]
