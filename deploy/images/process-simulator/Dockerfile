# syntax=docker/dockerfile:1
# The demo's process-value simulator (D-101, tools/Pulaski.ProcessSimulator): a Sparkplug B node publishing an extruder's melt temperature
# and profile width. Build from the repository root: docker build -f deploy/images/process-simulator/Dockerfile .
FROM mcr.microsoft.com/dotnet/sdk:10.0.404 AS build
WORKDIR /src
COPY global.json Directory.Build.props Directory.Packages.props nuget.config .editorconfig ./
COPY src/ src/
COPY tools/Pulaski.ProcessSimulator/ tools/Pulaski.ProcessSimulator/
RUN --mount=type=cache,id=nuget,target=/root/.nuget/packages \
    dotnet publish tools/Pulaski.ProcessSimulator -c Release -o /app

FROM mcr.microsoft.com/dotnet/runtime:10.0.15
WORKDIR /app
COPY --from=build /app .
USER $APP_UID
ENTRYPOINT ["dotnet", "Pulaski.ProcessSimulator.dll"]
