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
# Pure HTTP scraping — no Chromium/Playwright needed. Use slim ASP.NET runtime.
FROM mcr.microsoft.com/dotnet/aspnet:9.0-noble-chiseled AS runtime

ENV ASPNETCORE_ENVIRONMENT=Production
ENV DOTNET_RUNNING_IN_CONTAINER=true

WORKDIR /app
COPY --from=build /app/publish .

ENTRYPOINT ["dotnet", "AnimeIndex.Scraper.dll"]
