# The SDK stage runs natively on the build machine and cross-publishes for the target platform:
# the .NET SDK crashes under QEMU emulation (e.g. building linux/amd64 on an arm64 machine).
FROM --platform=$BUILDPLATFORM mcr.microsoft.com/dotnet/sdk:10.0 AS build
ARG TARGETARCH
WORKDIR /src
COPY . .
RUN dotnet publish src/McpAggregator.HttpServer/McpAggregator.HttpServer.csproj -c Release -a $TARGETARCH --no-self-contained -o /app

FROM mcr.microsoft.com/dotnet/aspnet:10.0
WORKDIR /app
COPY --from=build /app .
RUN mkdir -p /app/logs && chmod 777 /app/logs
VOLUME /data
ENV MCPAGGREGATOR__DATADIRECTORY=/data
EXPOSE 8080 8081
ENTRYPOINT ["dotnet", "McpAggregator.HttpServer.dll"]
