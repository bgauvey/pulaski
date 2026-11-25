# syntax=docker/dockerfile:1
# The demo's one-shot setup (D-101, tools/Pulaski.DemoSetup). Build from the repository root:
# docker build -f deploy/images/demo-setup/Dockerfile .  It writes the edge's sign-in to /shared, a volume the edge container reads.
FROM mcr.microsoft.com/dotnet/sdk:10.0.404 AS build
WORKDIR /src
COPY global.json Directory.Build.props Directory.Packages.props nuget.config .editorconfig ./
COPY src/ src/
COPY tools/Pulaski.DemoSetup/ tools/Pulaski.DemoSetup/
COPY deploy/simulator/packml-simulator-profile.csv deploy/simulator/
# Each image keeps its own package cache: docker compose builds them in parallel, and two restores unpacking the same package into a
# shared cache race (NuGet's temporary file vanishes under the other).
RUN --mount=type=cache,id=nuget-demo-setup,target=/root/.nuget/packages \
    dotnet publish tools/Pulaski.DemoSetup -c Release -o /app

FROM mcr.microsoft.com/dotnet/runtime:10.0.15
WORKDIR /app
COPY --from=build /app .
RUN mkdir -p /shared && chown "$APP_UID" /shared
USER $APP_UID
ENTRYPOINT ["dotnet", "Pulaski.DemoSetup.dll"]
