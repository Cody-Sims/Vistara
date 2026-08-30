ARG DOTNET_SDK_IMAGE=mcr.microsoft.com/dotnet/sdk:10.0.400-noble@sha256:e1ffd2a92ae84c1291bc1b6887501f8af98e6331e7af6d4c8d37168c5e87a64c
ARG DOTNET_RUNTIME_IMAGE=mcr.microsoft.com/dotnet/runtime:10.0.11-noble@sha256:a365ce6a50b09176855d085c69da3fc1204a48432e36087e9a208f6e5860e235

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

RUN dotnet restore src/Vistara.Worker/Vistara.Worker.csproj
RUN dotnet build src/Vistara.Worker/Vistara.Worker.csproj \
    --configuration Release \
    --no-restore
RUN dotnet publish src/Vistara.Worker/Vistara.Worker.csproj \
    --configuration Release \
    --no-build \
    --no-restore \
    --output /app/publish \
    /p:UseAppHost=false

FROM ${DOTNET_RUNTIME_IMAGE} AS final
WORKDIR /app

ARG IMAGE_VERSION=unreleased

LABEL org.opencontainers.image.title="Vistara Worker" \
      org.opencontainers.image.description="Vistara background worker" \
      org.opencontainers.image.source="https://github.com/Cody-Sims/Vistara" \
      org.opencontainers.image.licenses="Apache-2.0 AND MIT AND LGPL-2.1-or-later" \
      org.opencontainers.image.version="${IMAGE_VERSION}" \
      org.opencontainers.image.base.name="mcr.microsoft.com/dotnet/runtime:10.0.11-noble" \
      org.opencontainers.image.base.digest="sha256:a365ce6a50b09176855d085c69da3fc1204a48432e36087e9a208f6e5860e235" \
      io.vistara.libvips.version="8.18.6" \
      io.vistara.libvips.source="https://github.com/libvips/libvips/releases/download/v8.18.6/vips-8.18.6.tar.xz" \
      io.vistara.libvips.source.sha256="3c41e1d5458081bfa4a5bc54e116c46259c75c6760a18027764555632b9dda3e" \
      io.vistara.netvips.version="3.2.0"

ENV DOTNET_ENVIRONMENT=Production

COPY --from=libvips-build /out/vistara-libvips-runtime.deb /var/cache/vistara-libvips-runtime.deb
COPY ./deploy/licenses/NetVips-MIT.txt /usr/share/licenses/netvips/LICENSE
COPY ./deploy/licenses/THIRD-PARTY-NOTICES.md /usr/share/doc/vistara/THIRD-PARTY-NOTICES.md
RUN apt-get update && \
    apt-get install --yes --no-install-recommends /var/cache/vistara-libvips-runtime.deb && \
    rm --recursive --force /var/lib/apt/lists/* /var/cache/vistara-libvips-runtime.deb && \
    mkdir --parents /var/lib/vistara/data /var/lib/vistara/media && \
    chown --recursive "$APP_UID:$APP_UID" /var/lib/vistara

COPY --from=build /app/publish ./

USER $APP_UID

ENTRYPOINT ["dotnet", "Vistara.Worker.dll"]
