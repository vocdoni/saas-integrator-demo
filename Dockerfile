# Multi-stage build for the Homeowners Voting Platform API. No local .NET SDK required.
# (The React SPA is built and served by the separate `web` compose service.)
FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src
COPY src/HoaVoting.Api/HoaVoting.Api.csproj src/HoaVoting.Api/
RUN dotnet restore src/HoaVoting.Api/HoaVoting.Api.csproj
COPY src/ src/
RUN dotnet publish src/HoaVoting.Api/HoaVoting.Api.csproj -c Release -o /app --no-restore

FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS runtime
WORKDIR /app
# aspnet image listens on 8080 by default (ASPNETCORE_HTTP_PORTS=8080).
EXPOSE 8080
# SQLite db lives here; mount a volume to persist it.
RUN mkdir -p /data
COPY --from=build /app ./
ENTRYPOINT ["dotnet", "HoaVoting.Api.dll"]
