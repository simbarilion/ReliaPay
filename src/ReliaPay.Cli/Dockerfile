# syntax=docker/dockerfile:1

FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src
COPY Directory.Build.props Directory.Build.targets global.json nuget.config ./
COPY contracts/ ./contracts/
COPY src/ ./src/
RUN dotnet restore src/ReliaPay.Cli/ReliaPay.Cli.csproj
RUN dotnet publish src/ReliaPay.Cli/ReliaPay.Cli.csproj -c Release -o /app/publish --no-restore

FROM mcr.microsoft.com/dotnet/runtime:10.0 AS runtime
WORKDIR /app
COPY --from=build /app/publish .
COPY db/migrations /migrations
USER $APP_UID
ENTRYPOINT ["dotnet", "ReliaPay.Cli.dll"]
