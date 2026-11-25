# syntax=docker/dockerfile:1
# The demo's history (D-101, tools/Pulaski.DemoHistory): the previous shift and the current one so far, recorded through the application's
# command handlers straight into the database. Build from the repository root: docker build -f deploy/images/demo-history/Dockerfile .
FROM mcr.microsoft.com/dotnet/sdk:10.0.404 AS build
WORKDIR /src
COPY global.json Directory.Build.props Directory.Packages.props nuget.config .editorconfig ./
COPY src/ src/
COPY tools/Pulaski.DemoHistory/ tools/Pulaski.DemoHistory/
# Each image keeps its own package cache: docker compose builds them in parallel, and two restores unpacking the same package into a
# shared cache race (NuGet's temporary file vanishes under the other).
RUN --mount=type=cache,id=nuget-demo-history,target=/root/.nuget/packages \
    dotnet publish tools/Pulaski.DemoHistory -c Release -o /app

FROM mcr.microsoft.com/dotnet/runtime:10.0.15
# Npgsql looks for GSSAPI (Kerberos) when it connects; without the library it logs an error, as in the API's image.
RUN apt-get update \
    && apt-get install -y --no-install-recommends libgssapi-krb5-2 \
    && rm -rf /var/lib/apt/lists/*
WORKDIR /app
COPY --from=build /app .
USER $APP_UID
ENTRYPOINT ["dotnet", "Pulaski.DemoHistory.dll"]
