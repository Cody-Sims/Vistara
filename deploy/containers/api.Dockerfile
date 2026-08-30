ARG DOTNET_SDK_IMAGE=mcr.microsoft.com/dotnet/sdk:10.0.400-noble@sha256:e1ffd2a92ae84c1291bc1b6887501f8af98e6331e7af6d4c8d37168c5e87a64c
ARG DOTNET_RUNTIME_IMAGE=mcr.microsoft.com/dotnet/aspnet:10.0.11-noble@sha256:a4556ed033fa96f984bb7a8d348851cb2d36b1281dd2420070045f664fbb5f94
ARG NODE_IMAGE=docker.io/library/node:24-bookworm-slim@sha256:ba849c60be29959425b8734d57b8b4b7d56f98edd9504c9af091d5281095a71e

FROM ${DOTNET_SDK_IMAGE} AS libvips-build
ARG LIBVIPS_VERSION=8.18.6
ARG LIBVIPS_SOURCE_SHA256=3c41e1d5458081bfa4a5bc54e116c46259c75c6760a18027764555632b9dda3e
ARG LIBVIPS_SOURCE_URL=https://github.com/libvips/libvips/releases/download/v8.18.6/vips-8.18.6.tar.xz
COPY ./deploy/containers/build-libvips-runtime.sh /usr/local/bin/build-libvips-runtime
RUN chmod 0555 /usr/local/bin/build-libvips-runtime && \
    LIBVIPS_VERSION="${LIBVIPS_VERSION}" \
    LIBVIPS_SOURCE_SHA256="${LIBVIPS_SOURCE_SHA256}" \
    LIBVIPS_SOURCE_URL="${LIBVIPS_SOURCE_URL}" \
    /usr/local/bin/build-libvips-runtime

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
      org.opencontainers.image.licenses="Apache-2.0 AND MIT AND LGPL-2.1-or-later" \
      org.opencontainers.image.version="${IMAGE_VERSION}" \
      org.opencontainers.image.base.name="mcr.microsoft.com/dotnet/aspnet:10.0.11-noble" \
      org.opencontainers.image.base.digest="sha256:a4556ed033fa96f984bb7a8d348851cb2d36b1281dd2420070045f664fbb5f94" \
      io.vistara.libvips.version="8.18.6" \
      io.vistara.libvips.source="https://github.com/libvips/libvips/releases/download/v8.18.6/vips-8.18.6.tar.xz" \
      io.vistara.libvips.source.sha256="3c41e1d5458081bfa4a5bc54e116c46259c75c6760a18027764555632b9dda3e" \
      io.vistara.netvips.version="3.2.0"

ENV ASPNETCORE_ENVIRONMENT=Production \
    DOTNET_ENVIRONMENT=Production \
    ASPNETCORE_HTTP_PORTS=8080

COPY --from=libvips-build /out/vistara-libvips-runtime.deb /var/cache/vistara-libvips-runtime.deb
COPY ./deploy/licenses/NetVips-MIT.txt /usr/share/licenses/netvips/LICENSE
COPY ./deploy/licenses/THIRD-PARTY-NOTICES.md /usr/share/doc/vistara/THIRD-PARTY-NOTICES.md
RUN apt-get update && \
    apt-get install --yes --no-install-recommends /var/cache/vistara-libvips-runtime.deb && \
    rm --recursive --force /var/lib/apt/lists/* /var/cache/vistara-libvips-runtime.deb && \
    mkdir --parents /var/lib/vistara/data /var/lib/vistara/media && \
    chown --recursive "$APP_UID:$APP_UID" /var/lib/vistara

COPY --from=build /app/publish ./
COPY --from=web-build /web/dist ./wwwroot

USER $APP_UID
EXPOSE 8080

ENTRYPOINT ["dotnet", "Vistara.Api.dll"]
