ARG DOTNET_SDK_IMAGE=mcr.microsoft.com/dotnet/sdk:10.0.400-noble@sha256:e1ffd2a92ae84c1291bc1b6887501f8af98e6331e7af6d4c8d37168c5e87a64c
ARG DOTNET_RUNTIME_IMAGE=mcr.microsoft.com/dotnet/runtime:10.0.11-noble@sha256:a365ce6a50b09176855d085c69da3fc1204a48432e36087e9a208f6e5860e235

FROM ${DOTNET_SDK_IMAGE} AS build
WORKDIR /src

COPY global.json Directory.Build.props Directory.Packages.props Vistara.slnx ./
COPY src/ src/

RUN dotnet tool install dotnet-ef \
    --tool-path /tools \
    --version 10.0.11
RUN dotnet restore src/Vistara.Migrations.Sqlite/Vistara.Migrations.Sqlite.csproj && \
    dotnet restore src/Vistara.Migrations.Postgres/Vistara.Migrations.Postgres.csproj
RUN dotnet build src/Vistara.Migrations.Sqlite/Vistara.Migrations.Sqlite.csproj \
    --configuration Release \
    --no-restore && \
    dotnet build src/Vistara.Migrations.Postgres/Vistara.Migrations.Postgres.csproj \
    --configuration Release \
    --no-restore
RUN /tools/dotnet-ef migrations bundle \
    --project src/Vistara.Migrations.Sqlite/Vistara.Migrations.Sqlite.csproj \
    --configuration Release \
    --no-build \
    --verbose \
    --output /out/vistara-migrate-sqlite && \
    /tools/dotnet-ef migrations bundle \
    --project src/Vistara.Migrations.Postgres/Vistara.Migrations.Postgres.csproj \
    --configuration Release \
    --no-build \
    --verbose \
    --output /out/vistara-migrate-postgres

FROM ${DOTNET_RUNTIME_IMAGE} AS final
WORKDIR /app

ARG IMAGE_VERSION=unreleased

LABEL org.opencontainers.image.title="Vistara migrations" \
      org.opencontainers.image.description="Vistara SQLite and PostgreSQL migration bundles" \
      org.opencontainers.image.source="https://github.com/Cody-Sims/Vistara" \
      org.opencontainers.image.licenses="Apache-2.0" \
      org.opencontainers.image.version="${IMAGE_VERSION}" \
      org.opencontainers.image.base.name="mcr.microsoft.com/dotnet/runtime:10.0.11-noble" \
      org.opencontainers.image.base.digest="sha256:a365ce6a50b09176855d085c69da3fc1204a48432e36087e9a208f6e5860e235"

ENV DOTNET_BUNDLE_EXTRACT_BASE_DIR=/tmp/.net

RUN mkdir --parents /var/lib/vistara/data && \
    chown "$APP_UID:$APP_UID" /var/lib/vistara/data

COPY --from=build --chmod=0555 /out/vistara-migrate-sqlite /app/
COPY --from=build --chmod=0555 /out/vistara-migrate-postgres /app/
COPY --chmod=0555 deploy/containers/migration-entrypoint.sh /usr/local/bin/vistara-migrate

USER $APP_UID

ENTRYPOINT ["/usr/local/bin/vistara-migrate"]
