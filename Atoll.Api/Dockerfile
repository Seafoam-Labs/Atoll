FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS base
WORKDIR /app
# .NET on Linux validates TLS against the OS trust store; tlsCAFile alone
# leaves the DocumentDB chain untrusted (PartialChain), so the RDS CA bundle
# is also installed system-wide.
RUN apt-get update && apt-get install -y --no-install-recommends git curl ca-certificates \
    && curl -fsSL https://truststore.pki.rds.amazonaws.com/global/global-bundle.pem -o /app/rds-combined-ca-bundle.pem \
    && cp /app/rds-combined-ca-bundle.pem /usr/local/share/ca-certificates/rds-ca-bundle.crt \
    && update-ca-certificates \
    && rm -rf /var/lib/apt/lists/*
RUN mkdir -p /app/data && chown -R $APP_UID /app/data
USER $APP_UID
EXPOSE 8080
EXPOSE 8081

FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
ARG BUILD_CONFIGURATION=Release
ARG TAILWIND_VERSION=4.3.3
# TARGETARCH is provided by BuildKit: amd64 for x86 runners, arm64 for the
# Graviton CI runners that build the production image.
ARG TARGETARCH
RUN apt-get update \
    && apt-get install -y --no-install-recommends curl ca-certificates \
    && tailwind_arch="$(case "$TARGETARCH" in amd64) echo x64 ;; arm64) echo arm64 ;; *) echo "unsupported architecture: $TARGETARCH" >&2; exit 1 ;; esac)" \
    && curl -fsSL "https://github.com/tailwindlabs/tailwindcss/releases/download/v${TAILWIND_VERSION}/tailwindcss-linux-${tailwind_arch}" \
        -o /usr/local/bin/tailwindcss \
    && chmod +x /usr/local/bin/tailwindcss \
    && rm -rf /var/lib/apt/lists/*
WORKDIR /src
COPY ["Atoll.Api/Atoll.Api.csproj", "Atoll.Api/"]
RUN dotnet restore "Atoll.Api/Atoll.Api.csproj"
COPY . .
WORKDIR "/src/Atoll.Api"
RUN dotnet build "./Atoll.Api.csproj" -c $BUILD_CONFIGURATION -o /app/build

FROM build AS publish
ARG BUILD_CONFIGURATION=Release
RUN dotnet publish "./Atoll.Api.csproj" -c $BUILD_CONFIGURATION -o /app/publish /p:UseAppHost=false

FROM base AS final
WORKDIR /app
COPY --from=publish /app/publish .
ENTRYPOINT ["dotnet", "Atoll.Api.dll"]
