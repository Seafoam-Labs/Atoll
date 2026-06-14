# https://github.com/dotnet/dotnet-docker/blob/main/samples/releasesapi/Dockerfile
FROM mcr.microsoft.com/dotnet/runtime-deps:10.0-noble-chiseled AS base
WORKDIR /app
EXPOSE 8080

FROM mcr.microsoft.com/dotnet/sdk:10.0-noble-aot AS build
ARG BUILD_CONFIGURATION=Release
WORKDIR /src
COPY ["Atoll.Api/Atoll.Api.csproj", "Atoll.Api/"]
RUN dotnet restore "Atoll.Api/Atoll.Api.csproj"
COPY . .
WORKDIR "/src/Atoll.Api"
RUN dotnet publish "./Atoll.Api.csproj" -c $BUILD_CONFIGURATION -o /app/publish \
    && rm /app/publish/*.dbg

FROM base AS final
WORKDIR /app
COPY --from=build /app/publish .
USER $APP_UID
ENTRYPOINT ["./Atoll.Api"]
