# See https://aka.ms/customizecontainer to learn how to customize your debug container and how Visual Studio uses this Dockerfile to build your images for faster debugging.

# This stage is used when running from VS in fast mode (Default for Debug configuration)
FROM mcr.microsoft.com/dotnet/runtime:10.0 AS base
# /app stays root's, so the bot can't change its own files. Only its data folder is the bot user's, made here so a new
# volume takes that owner. The Steam sign-in the bot saves lives there, in data/Auth.
WORKDIR /app
RUN mkdir /app/data && chown "$APP_UID:$APP_UID" /app/data
USER $APP_UID
VOLUME /app/data

# This stage is used to build the service project
FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
ARG BUILD_CONFIGURATION=Release
WORKDIR /src
COPY ["MinerBot 2.0/MinerBot 2.0.csproj", "MinerBot 2.0/"]
COPY ["DataAccessLibrary/DataAccessLibrary.csproj", "DataAccessLibrary/"]
COPY ["MinerBot.Shared/MinerBot.Shared.csproj", "MinerBot.Shared/"]
COPY ["SteamWebAPIAccess/SteamWebAPIAccess.csproj", "SteamWebAPIAccess/"]
RUN dotnet restore "./MinerBot 2.0/MinerBot 2.0.csproj"
COPY . .
WORKDIR "/src/MinerBot 2.0"
RUN dotnet build "./MinerBot 2.0.csproj" -c $BUILD_CONFIGURATION -o /app/build

# This stage is used to publish the service project to be copied to the final stage
FROM build AS publish
ARG BUILD_CONFIGURATION=Release
# Readable by all and writable only by root, whatever modes the build's files had, as MinerBot.Dashboard/Dockerfile says
RUN dotnet publish "./MinerBot 2.0.csproj" -c $BUILD_CONFIGURATION -o /app/publish /p:UseAppHost=false \
    && chmod -R a+rX,go-w /app/publish

# This stage is used in production or when running from VS in regular mode (Default when not using the Debug configuration)
FROM base AS final
WORKDIR /app
COPY --from=publish /app/publish .
ENTRYPOINT ["dotnet", "MinerBot 2.0.dll"]