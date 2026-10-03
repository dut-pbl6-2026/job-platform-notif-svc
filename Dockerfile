# Notification Service — multi-stage build (.NET 10, port 5007).
# Local SharedKernel feed is copied in for restore (long-term: GH Packages).
FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /app
COPY local-feed/ ./local-feed/
COPY nuget.config ./
COPY NotifService.sln ./
COPY src/ ./src/
RUN dotnet restore NotifService.sln
RUN dotnet publish src/Notif.Api/Notif.Api.csproj -c Release -o /out --no-restore

FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS runtime
WORKDIR /app
COPY --from=build /out ./
EXPOSE 5007
ENV ASPNETCORE_HTTP_PORTS=5007
ENTRYPOINT ["dotnet", "Notif.Api.dll"]
