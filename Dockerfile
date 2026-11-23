# syntax=docker/dockerfile:1

FROM --platform=$BUILDPLATFORM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src
COPY ChatHub.Contracts/ChatHub.Contracts.csproj ChatHub.Contracts/
COPY ChatHub.Domain/ChatHub.Domain.csproj ChatHub.Domain/
COPY ChatHub.Application/ChatHub.Application.csproj ChatHub.Application/
COPY ChatHub.Infrastructure/ChatHub.Infrastructure.csproj ChatHub.Infrastructure/
COPY ChatHub.WebAPI/ChatHub.WebAPI.csproj ChatHub.WebAPI/
RUN dotnet restore ChatHub.WebAPI/ChatHub.WebAPI.csproj
COPY ChatHub.Contracts/ ChatHub.Contracts/
COPY ChatHub.Domain/ ChatHub.Domain/
COPY ChatHub.Application/ ChatHub.Application/
COPY ChatHub.Infrastructure/ ChatHub.Infrastructure/
COPY ChatHub.WebAPI/ ChatHub.WebAPI/
RUN dotnet publish ChatHub.WebAPI/ChatHub.WebAPI.csproj -c Release --no-restore -o /app/publish /p:UseAppHost=false

FROM --platform=$BUILDPLATFORM mcr.microsoft.com/dotnet/sdk:10.0 AS ef-tools
ARG EF_TOOLS_VERSION=10.0.4
RUN dotnet tool install dotnet-ef --version ${EF_TOOLS_VERSION} --tool-path /tools

FROM build AS bundle
ARG TARGETARCH
COPY --from=ef-tools /tools /tools
RUN case "$TARGETARCH" in \
        amd64) rid=linux-x64 ;; \
        arm64) rid=linux-arm64 ;; \
        *) echo "Unsupported TARGETARCH '$TARGETARCH'" >&2; exit 1 ;; \
    esac \
    && /tools/dotnet-ef migrations bundle \
        --project ChatHub.Infrastructure \
        --startup-project ChatHub.WebAPI \
        --configuration Release \
        --no-build \
        --target-runtime "$rid" \
        --output /app/efbundle

FROM --platform=$BUILDPLATFORM mcr.microsoft.com/dotnet/sdk:10.0 AS notifications-build
WORKDIR /src
COPY ChatHub.Contracts/ChatHub.Contracts.csproj ChatHub.Contracts/
COPY ChatHub.Notifications/ChatHub.Notifications.csproj ChatHub.Notifications/
RUN dotnet restore ChatHub.Notifications/ChatHub.Notifications.csproj
COPY ChatHub.Contracts/ ChatHub.Contracts/
COPY ChatHub.Notifications/ ChatHub.Notifications/
RUN dotnet publish ChatHub.Notifications/ChatHub.Notifications.csproj -c Release --no-restore -o /app/publish /p:UseAppHost=false

FROM notifications-build AS notifications-bundle
ARG TARGETARCH
COPY --from=ef-tools /tools /tools
RUN case "$TARGETARCH" in \
        amd64) rid=linux-x64 ;; \
        arm64) rid=linux-arm64 ;; \
        *) echo "Unsupported TARGETARCH '$TARGETARCH'" >&2; exit 1 ;; \
    esac \
    && /tools/dotnet-ef migrations bundle \
        --project ChatHub.Notifications \
        --startup-project ChatHub.Notifications \
        --configuration Release \
        --no-build \
        --target-runtime "$rid" \
        --output /app/efbundle

FROM build AS seeder-build
COPY tools/ChatHub.Seeder/ChatHub.Seeder.csproj tools/ChatHub.Seeder/
RUN dotnet restore tools/ChatHub.Seeder/ChatHub.Seeder.csproj
COPY tools/ChatHub.Seeder/ tools/ChatHub.Seeder/
RUN dotnet publish tools/ChatHub.Seeder/ChatHub.Seeder.csproj -c Release --no-restore -o /app/seeder /p:UseAppHost=false

FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS seeder
WORKDIR /app
COPY --from=seeder-build /app/seeder .
USER $APP_UID
ENTRYPOINT ["dotnet", "ChatHub.Seeder.dll"]

FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS migrator
WORKDIR /app
COPY --from=bundle /app/efbundle .
USER $APP_UID
ENTRYPOINT ["./efbundle"]

FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS api
WORKDIR /app
RUN mkdir -p /app/storage && chown "$APP_UID:$APP_UID" /app/storage && chmod 700 /app/storage
COPY --from=build /app/publish .
ENV ASPNETCORE_HTTP_PORTS=8080
EXPOSE 8080
USER $APP_UID
ENTRYPOINT ["dotnet", "ChatHub.WebAPI.dll"]

FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS notifications-migrator
WORKDIR /app
COPY --from=notifications-bundle /app/efbundle .
USER $APP_UID
ENTRYPOINT ["./efbundle"]

FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS notifications
WORKDIR /app
COPY --from=notifications-build /app/publish .
ENV ASPNETCORE_HTTP_PORTS=8080
EXPOSE 8080
USER $APP_UID
ENTRYPOINT ["dotnet", "ChatHub.Notifications.dll"]
