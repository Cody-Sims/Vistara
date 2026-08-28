ARG DOTNET_SDK_IMAGE=mcr.microsoft.com/dotnet/sdk:10.0.400-noble@sha256:e1ffd2a92ae84c1291bc1b6887501f8af98e6331e7af6d4c8d37168c5e87a64c
ARG DOTNET_RUNTIME_IMAGE=mcr.microsoft.com/dotnet/aspnet:10.0.11-noble@sha256:a4556ed033fa96f984bb7a8d348851cb2d36b1281dd2420070045f664fbb5f94
ARG NODE_IMAGE=docker.io/library/node:24-bookworm-slim@sha256:ba849c60be29959425b8734d57b8b4b7d56f98edd9504c9af091d5281095a71e

FROM ${DOTNET_SDK_IMAGE} AS build
WORKDIR /src

COPY global.json Directory.Build.props Directory.Packages.props Vistara.slnx ./
COPY src/ src/

RUN dotnet restore src/Vistara.Api/Vistara.Api.csproj
RUN dotnet build src/Vistara.Api/Vistara.Api.csproj \
    --configuration Release \
    --no-restore
RUN dotnet publish src/Vistara.Api/Vistara.Api.csproj \
    --configuration Release \
    --no-build \
    --no-restore \
    --output /app/publish \
    /p:UseAppHost=false

FROM ${NODE_IMAGE} AS web-build
WORKDIR /web

COPY src/Vistara.Web/package.json src/Vistara.Web/package-lock.json ./
RUN npm ci

COPY src/Vistara.Web/ ./
RUN npm run build

FROM ${DOTNET_RUNTIME_IMAGE} AS final
WORKDIR /app

ARG IMAGE_VERSION=unreleased

LABEL org.opencontainers.image.title="Vistara API" \
      org.opencontainers.image.description="Vistara HTTP API" \
      org.opencontainers.image.source="https://github.com/Cody-Sims/Vistara" \
      org.opencontainers.image.licenses="Apache-2.0" \
      org.opencontainers.image.version="${IMAGE_VERSION}" \
      org.opencontainers.image.base.name="mcr.microsoft.com/dotnet/aspnet:10.0.11-noble" \
      org.opencontainers.image.base.digest="sha256:a4556ed033fa96f984bb7a8d348851cb2d36b1281dd2420070045f664fbb5f94"

ENV ASPNETCORE_ENVIRONMENT=Production \
    DOTNET_ENVIRONMENT=Production \
    ASPNETCORE_HTTP_PORTS=8080

COPY --from=build /app/publish ./
COPY --from=web-build /web/dist ./wwwroot

USER $APP_UID
EXPOSE 8080

ENTRYPOINT ["dotnet", "Vistara.Api.dll"]
