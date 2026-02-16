FROM mcr.microsoft.com/dotnet/sdk:10.0-preview AS build
WORKDIR /src
COPY . .
RUN dotnet publish src/McpAggregator.HttpServer/McpAggregator.HttpServer.csproj -c Release -o /app

FROM mcr.microsoft.com/dotnet/aspnet:10.0-preview
WORKDIR /app
COPY --from=build /app .
VOLUME /data
ENV MCPAGGREGATOR__DATADIRECTORY=/data
EXPOSE 8080
HEALTHCHECK --interval=30s --timeout=5s --retries=3 CMD curl -f http://localhost:8080/health || exit 1
ENTRYPOINT ["dotnet", "McpAggregator.HttpServer.dll"]
