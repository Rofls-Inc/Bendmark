FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src
# Сначала только csproj: пока зависимости не меняются, restore берётся из кэша
COPY Bendmark.Coordinator.csproj .
RUN dotnet restore Bendmark.Coordinator.csproj
COPY . .
RUN dotnet publish Bendmark.Coordinator.csproj \
    --configuration Release --no-restore --output /app/publish \
    /p:UseAppHost=false

FROM mcr.microsoft.com/dotnet/aspnet:10.0
WORKDIR /app
COPY --from=build /app/publish .
USER app
ENV ASPNETCORE_HTTP_PORTS=5080
EXPOSE 5080
ENTRYPOINT ["dotnet", "Bendmark.Coordinator.dll"]
