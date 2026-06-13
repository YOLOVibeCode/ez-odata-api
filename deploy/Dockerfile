# Multi-arch image for the ez-odata-api standalone server (spec 12 OPS-1).
# Builds the React admin UI and the .NET host, runs on a chiseled non-root base.

# ---- UI build ----
FROM node:22-alpine AS ui
WORKDIR /ui
COPY ui/package*.json ./
RUN npm ci
COPY ui/ ./
# Emit into a staging dir the .NET build will pick up.
RUN npm run build -- --outDir /ui-dist --emptyOutDir

# ---- .NET build ----
FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src
COPY *.sln* Directory.Build.props BannedSymbols.txt ./
COPY src/ ./src/
RUN dotnet restore src/EzOdata.Host/EzOdata.Host.csproj
COPY --from=ui /ui-dist ./src/EzOdata.Host/wwwroot
RUN dotnet publish src/EzOdata.Host/EzOdata.Host.csproj -c Release -o /app --no-restore

# ---- runtime ----
FROM mcr.microsoft.com/dotnet/aspnet:10.0-noble-chiseled AS runtime
WORKDIR /app
COPY --from=build /app ./
EXPOSE 8080
ENV ASPNETCORE_URLS=http://+:8080 \
    DOTNET_gcServer=1
USER $APP_UID
ENTRYPOINT ["dotnet", "EzOdata.Host.dll"]
