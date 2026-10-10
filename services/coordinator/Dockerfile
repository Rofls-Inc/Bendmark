FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
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
USER app
ENV ASPNETCORE_HTTP_PORTS=5080
EXPOSE 5080
ENTRYPOINT ["dotnet", "Bendmark.Coordinator.dll"]
