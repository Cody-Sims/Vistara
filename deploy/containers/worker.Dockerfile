ARG DOTNET_SDK_IMAGE=mcr.microsoft.com/dotnet/sdk:10.0.400-noble@sha256:e1ffd2a92ae84c1291bc1b6887501f8af98e6331e7af6d4c8d37168c5e87a64c
ARG DOTNET_RUNTIME_IMAGE=mcr.microsoft.com/dotnet/runtime:10.0.11-noble@sha256:a365ce6a50b09176855d085c69da3fc1204a48432e36087e9a208f6e5860e235

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
      org.opencontainers.image.licenses="Apache-2.0" \
      org.opencontainers.image.version="${IMAGE_VERSION}" \
      org.opencontainers.image.base.name="mcr.microsoft.com/dotnet/runtime:10.0.11-noble" \
      org.opencontainers.image.base.digest="sha256:a365ce6a50b09176855d085c69da3fc1204a48432e36087e9a208f6e5860e235"

ENV DOTNET_ENVIRONMENT=Production

COPY --from=build /app/publish ./

USER $APP_UID

ENTRYPOINT ["dotnet", "Vistara.Worker.dll"]
