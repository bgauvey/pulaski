# syntax=docker/dockerfile:1
# Pulaski API. Build from the repository root: docker build -f deploy/images/api/Dockerfile .
# Configuration comes from the environment (ConnectionStrings__Pulaski, Security__SigningKey, Security__BootstrapAdmin__*, ...);
# see deploy/images/README.md.
FROM mcr.microsoft.com/dotnet/sdk:10.0.404 AS build
WORKDIR /src
COPY global.json Directory.Build.props Directory.Packages.props nuget.config .editorconfig ./
COPY src/ src/
RUN --mount=type=cache,id=nuget,target=/root/.nuget/packages \
    dotnet publish src/Pulaski.Api -c Release -o /app

FROM mcr.microsoft.com/dotnet/aspnet:10.0.15
# Npgsql looks for GSSAPI (Kerberos) when it connects; without the library it logs an error at start, and Kerberos sign-in to
# PostgreSQL couldn't work.
RUN apt-get update \
    && apt-get install -y --no-install-recommends libgssapi-krb5-2 \
    && rm -rf /var/lib/apt/lists/*
WORKDIR /app
COPY --from=build /app .
ENV ASPNETCORE_HTTP_PORTS=8080
EXPOSE 8080
USER $APP_UID
ENTRYPOINT ["dotnet", "Pulaski.Api.dll"]
