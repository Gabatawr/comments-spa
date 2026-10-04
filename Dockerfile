# syntax=docker/dockerfile:1
# ---------------------------------------------------------------------------
# SPA «Комментарии» — multi-stage build
#   stage 1 (sdk)     : restore + build + publish .NET 10 API
#   stage 2 (aspnet)  : minimal runtime image, non-root, healthcheck
#
# The SPA is build-free (plain ES modules in src/Frontend) and is copied into
# the API's wwwroot so a single origin/port serves both API and UI.
# ---------------------------------------------------------------------------

# ------------------------------- build -------------------------------------
FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src

# 1) Restore first (layer cache): only the project file is needed.
COPY src/Backend/CommentsApi/CommentsApi.csproj src/Backend/CommentsApi/
RUN dotnet restore src/Backend/CommentsApi/CommentsApi.csproj

# 2) Sources: backend + frontend.
COPY src/Backend/ src/Backend/
COPY src/Frontend/ src/Frontend/

# 3) Deterministically place the SPA into wwwroot before publish, so the
#    published output contains the static assets regardless of glob timing.
RUN rm -rf src/Backend/CommentsApi/wwwroot \
 && mkdir -p src/Backend/CommentsApi/wwwroot \
 && cp -R src/Frontend/. src/Backend/CommentsApi/wwwroot/

RUN dotnet publish src/Backend/CommentsApi/CommentsApi.csproj \
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
    ConnectionStrings__Default="Data Source=/app/data/comments.db" \
    Storage__Root=/app/storage \
    Features__DevCaptchaPeek=false

# Writable state for SQLite + uploads, owned by the non-root `app` user.
# `chmod` also normalises source files that may be mode 600, so the published
# app is always readable by the unprivileged user.
RUN mkdir -p /app/data /app/storage \
 && chown -R app:app /app \
 && chmod -R u+rwX,go+rX /app

USER app
EXPOSE 8080

HEALTHCHECK --interval=30s --timeout=5s --start-period=25s --retries=5 \
    CMD curl -fsS http://localhost:8080/api/health || exit 1

ENTRYPOINT ["dotnet", "CommentsApi.dll"]
