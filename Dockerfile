# Build stage. The SDK image is large and stays out of the final image entirely.
FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
ARG BUILD_CONFIGURATION=Release
WORKDIR /source

# Restore before copying the rest of the source, so a change to a source file does not
# invalidate the layer holding the downloaded packages.
COPY global.json Directory.Build.props Directory.Packages.props ./
COPY src/Spoolr.Api/Spoolr.Api.csproj src/Spoolr.Api/
COPY src/Spoolr.Core/Spoolr.Core.csproj src/Spoolr.Core/
COPY src/Spoolr.Infrastructure/Spoolr.Infrastructure.csproj src/Spoolr.Infrastructure/
RUN dotnet restore src/Spoolr.Api/Spoolr.Api.csproj

COPY src/ src/
RUN dotnet publish src/Spoolr.Api/Spoolr.Api.csproj \
    --configuration $BUILD_CONFIGURATION \
    --no-restore \
    --output /app

# Runtime stage. The chiselled image carries no shell and no package manager, which removes
# most of what an attacker who reaches the container would otherwise have to work with.
FROM mcr.microsoft.com/dotnet/aspnet:10.0-noble-chiseled AS runtime
WORKDIR /app

# Present in the base image already. Named here so it is obvious the process is not root.
USER $APP_UID

COPY --from=build /app .

EXPOSE 8080
ENV ASPNETCORE_HTTP_PORTS=8080

ENTRYPOINT ["dotnet", "Spoolr.Api.dll"]
