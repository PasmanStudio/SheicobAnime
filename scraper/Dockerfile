# ─── Build stage ──────────────────────────────────────────────────────────────
# Use the SDK image for building (not the Playwright image — smaller layer cache)
FROM mcr.microsoft.com/dotnet/sdk:9.0 AS build
WORKDIR /src

# Restore layer — copy project files first for better cache utilisation
COPY api/AnimeIndex.Api/AnimeIndex.Api.csproj api/AnimeIndex.Api/
COPY scraper/AnimeIndex.Scraper/AnimeIndex.Scraper.csproj scraper/AnimeIndex.Scraper/
RUN dotnet restore scraper/AnimeIndex.Scraper/AnimeIndex.Scraper.csproj

# Build + publish
COPY api/AnimeIndex.Api/ api/AnimeIndex.Api/
COPY scraper/AnimeIndex.Scraper/ scraper/AnimeIndex.Scraper/
RUN dotnet publish scraper/AnimeIndex.Scraper/AnimeIndex.Scraper.csproj \
    -c Release -o /app/publish --no-restore

# ─── Runtime stage ────────────────────────────────────────────────────────────
# Official Playwright image ships .NET runtime + Chromium + all system deps.
# Tag aligns with the Microsoft.Playwright NuGet version in the csproj.
FROM mcr.microsoft.com/playwright/dotnet:v1.51.0-noble AS runtime

ENV ASPNETCORE_ENVIRONMENT=Production
ENV DOTNET_RUNNING_IN_CONTAINER=true
# Playwright uses this env var to locate the browser binaries it pre-installed
ENV PLAYWRIGHT_BROWSERS_PATH=/ms-playwright

WORKDIR /app
COPY --from=build /app/publish .

ENTRYPOINT ["dotnet", "AnimeIndex.Scraper.dll"]
