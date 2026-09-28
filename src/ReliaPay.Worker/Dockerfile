# syntax=docker/dockerfile:1

FROM mcr.microsoft.com/dotnet/sdk:10.0@sha256:e1ffd2a92ae84c1291bc1b6887501f8af98e6331e7af6d4c8d37168c5e87a64c AS build
WORKDIR /src
COPY Directory.Build.props Directory.Build.targets global.json nuget.config ./
COPY contracts/ ./contracts/
COPY src/ ./src/
RUN dotnet restore src/ReliaPay.Worker/ReliaPay.Worker.csproj
RUN dotnet publish src/ReliaPay.Worker/ReliaPay.Worker.csproj -c Release -o /app/publish --no-restore

FROM mcr.microsoft.com/dotnet/runtime:10.0@sha256:a365ce6a50b09176855d085c69da3fc1204a48432e36087e9a208f6e5860e235 AS runtime
WORKDIR /app
COPY --from=build /app/publish .
EXPOSE 8080
USER $APP_UID
ENTRYPOINT ["dotnet", "ReliaPay.Worker.dll"]
