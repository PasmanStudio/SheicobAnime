# ─── Build stage ──────────────────────────────────────────────────────────────
FROM mcr.microsoft.com/dotnet/sdk:9.0 AS build
WORKDIR /src

# Restore layer — project files first for cache
COPY AnimeIndex.Api/AnimeIndex.Api.csproj AnimeIndex.Api/
RUN dotnet restore AnimeIndex.Api/AnimeIndex.Api.csproj

# Build + publish
COPY AnimeIndex.Api/ AnimeIndex.Api/
RUN dotnet publish AnimeIndex.Api/AnimeIndex.Api.csproj \
    -c Release -o /app/publish --no-restore

# ─── Runtime stage ────────────────────────────────────────────────────────────
FROM mcr.microsoft.com/dotnet/aspnet:9.0 AS runtime

ENV ASPNETCORE_ENVIRONMENT=Production
ENV DOTNET_RUNNING_IN_CONTAINER=true

WORKDIR /app
COPY --from=build /app/publish .

EXPOSE 8080
ENV ASPNETCORE_URLS=http://+:8080

ENTRYPOINT ["dotnet", "AnimeIndex.Api.dll"]
