FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
# protoc из пакета Grpc.Tools падает на linux/arm64 (Docker на Apple Silicon, код 139),
# поэтому берём системный, как в образе агента. Плагин grpc_csharp_plugin остаётся из Grpc.Tools
RUN apt-get update \
    && apt-get install -y --no-install-recommends protobuf-compiler \
    && rm -rf /var/lib/apt/lists/*
ENV PROTOBUF_PROTOC=/usr/bin/protoc
WORKDIR /src
# Сначала только csproj: пока зависимости не меняются, restore берётся из кэша
COPY services/coordinator/Bendmark.Coordinator.csproj services/coordinator/
RUN dotnet restore services/coordinator/Bendmark.Coordinator.csproj
COPY proto/ proto/
COPY services/coordinator/ services/coordinator/
RUN dotnet publish services/coordinator/Bendmark.Coordinator.csproj \
    --configuration Release --no-restore --output /app/publish \
    /p:UseAppHost=false

FROM mcr.microsoft.com/dotnet/aspnet:10.0
WORKDIR /app
COPY --from=build /app/publish .
# Каталог базы (Storage:Path = data/runs.db). Новый том Docker берёт владельца из образа,
# поэтому пользователь app может писать в него
RUN mkdir -p /app/data && chown app:app /app/data
USER app
ENV ASPNETCORE_HTTP_PORTS=5080
EXPOSE 5080
ENTRYPOINT ["dotnet", "Bendmark.Coordinator.dll"]
