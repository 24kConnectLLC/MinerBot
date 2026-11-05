# The web dashboard. Built from the repository root: docker build -f MinerBot.Dashboard/Dockerfile -t minerbot-dashboard .
# Settings come from the environment when the container starts, and no settings file is ever copied in.

FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS base
# The image would otherwise listen on 8080 on every address, which with host networking is the server's public address.
# The dashboard sets its own in code: 127.0.0.1:5080, unless Dashboard__ListenAddress and Dashboard__Port say otherwise.
ENV ASPNETCORE_HTTP_PORTS=
ENV Dashboard__KeysFolder=/app/keys
# No diagnostics socket in /tmp and no debugger or profiler attach: nothing in the container needs them
ENV DOTNET_EnableDiagnostics=0
WORKDIR /app
# /app stays root's, so the app can't change its own files even in a container that isn't read-only. Only the keys folder
# is the app user's, made here so a new volume takes that owner. The sign-in keys live on it, so a restart signs nobody
# out, and with a read-only filesystem it and /tmp are the only places the app writes.
RUN mkdir /app/keys && chown "$APP_UID:$APP_UID" /app/keys
USER $APP_UID
VOLUME /app/keys

FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
ARG BUILD_CONFIGURATION=Release
WORKDIR /src
COPY ["MinerBot.Dashboard/MinerBot.Dashboard.csproj", "MinerBot.Dashboard/"]
COPY ["DataAccessLibrary/DataAccessLibrary.csproj", "DataAccessLibrary/"]
COPY ["MinerBot.Shared/MinerBot.Shared.csproj", "MinerBot.Shared/"]
RUN dotnet restore "./MinerBot.Dashboard/MinerBot.Dashboard.csproj"
COPY . .
WORKDIR "/src/MinerBot.Dashboard"
RUN dotnet build "./MinerBot.Dashboard.csproj" -c $BUILD_CONFIGURATION -o /app/build

FROM build AS publish
ARG BUILD_CONFIGURATION=Release
RUN dotnet publish "./MinerBot.Dashboard.csproj" -c $BUILD_CONFIGURATION -o /app/publish /p:UseAppHost=false

FROM base AS final
WORKDIR /app
COPY --from=publish /app/publish .
# The runtime image has no curl, so the dashboard checks itself: it asks /health/live on its own address. Every 2 seconds
# while it starts, so Docker reports it healthy within seconds, and every 30 after.
HEALTHCHECK --interval=30s --timeout=10s --start-period=30s --start-interval=2s --retries=3 CMD ["dotnet", "MinerBot.Dashboard.dll", "healthcheck"]
ENTRYPOINT ["dotnet", "MinerBot.Dashboard.dll"]
