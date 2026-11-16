# syntax=docker/dockerfile:1
# Pulaski Edge (D-050). Build from the repository root: docker build -f deploy/images/edge/Dockerfile .
# Its buffer and OPC UA certificates live in /data: mount a volume there so they survive the container. Settings come from the
# environment (Edge__ApiUrl, Edge__EdgeNodeId, Edge__Username, Edge__Password, ...); see deploy/edge/README.md.
FROM mcr.microsoft.com/dotnet/sdk:10.0.404 AS build
WORKDIR /src
COPY global.json Directory.Build.props Directory.Packages.props nuget.config .editorconfig ./
COPY src/ src/
RUN --mount=type=cache,id=nuget,target=/root/.nuget/packages \
    dotnet publish src/Pulaski.Edge -c Release -o /app

FROM mcr.microsoft.com/dotnet/runtime:10.0.15
WORKDIR /app
COPY --from=build /app .
RUN mkdir -p /data && chown "$APP_UID" /data
ENV Edge__BufferPath=/data/pulaski-edge.db \
    Edge__PkiPath=/data/pki
VOLUME /data
USER $APP_UID
ENTRYPOINT ["dotnet", "Pulaski.Edge.dll"]
