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
# Official Playwright image ships Chromium + all system deps, but only .NET 8.
# Install .NET 9 runtime on top since our scraper targets net9.0.
FROM mcr.microsoft.com/playwright/dotnet:v1.51.0-noble AS runtime

# Install .NET 9 runtime (Playwright image only has .NET 8)
RUN apt-get update && apt-get install -y --no-install-recommends wget \
    && wget https://dot.net/v1/dotnet-install.sh -O /tmp/dotnet-install.sh \
    && chmod +x /tmp/dotnet-install.sh \
    && /tmp/dotnet-install.sh --runtime dotnet --channel 9.0 --install-dir /usr/share/dotnet \
    && /tmp/dotnet-install.sh --runtime aspnetcore --channel 9.0 --install-dir /usr/share/dotnet \
    && rm /tmp/dotnet-install.sh \
    && apt-get clean && rm -rf /var/lib/apt/lists/*

ENV ASPNETCORE_ENVIRONMENT=Production
ENV DOTNET_RUNNING_IN_CONTAINER=true
# Playwright uses this env var to locate the browser binaries it pre-installed
ENV PLAYWRIGHT_BROWSERS_PATH=/ms-playwright

WORKDIR /app
COPY --from=build /app/publish .

ENTRYPOINT ["dotnet", "AnimeIndex.Scraper.dll"]
