# syntax=docker/dockerfile:1
# Pulaski UI (Blazor, interactive server). Build from the repository root: docker build -f deploy/images/web/Dockerfile .
# It needs only the API's address: PulaskiApi__BaseUrl.
FROM mcr.microsoft.com/dotnet/sdk:10.0.404 AS build
WORKDIR /src
COPY global.json Directory.Build.props Directory.Packages.props nuget.config .editorconfig ./
COPY src/ src/
RUN --mount=type=cache,id=nuget,target=/root/.nuget/packages \
    dotnet publish src/Pulaski.Web -c Release -o /app

FROM mcr.microsoft.com/dotnet/aspnet:10.0.15
WORKDIR /app
COPY --from=build /app .
# Data Protection keys (sign-in cookies, sealed sign-in state) live here: owned by the app user, so a volume mounted on it is writable.
RUN mkdir -p /home/app/.aspnet/DataProtection-Keys && chown -R "$APP_UID" /home/app/.aspnet
ENV ASPNETCORE_HTTP_PORTS=8080
EXPOSE 8080
USER $APP_UID
ENTRYPOINT ["dotnet", "Pulaski.Web.dll"]
