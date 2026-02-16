FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src
COPY . .
RUN dotnet publish src/McpAggregator.HttpServer/McpAggregator.HttpServer.csproj -c Release -o /app

FROM mcr.microsoft.com/dotnet/aspnet:10.0
WORKDIR /app
COPY --from=build /app .
VOLUME /data
ENV MCPAGGREGATOR__DATADIRECTORY=/data
EXPOSE 8080
ENTRYPOINT ["dotnet", "McpAggregator.HttpServer.dll"]
