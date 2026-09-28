FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src
COPY src/Notif.Core/Notif.Core.csproj src/Notif.Core/
COPY src/Notif.Infrastructure/Notif.Infrastructure.csproj src/Notif.Infrastructure/
COPY src/Notif.Api/Notif.Api.csproj src/Notif.Api/
COPY nuget.config ./
COPY local-feed/ local-feed/
RUN dotnet restore src/Notif.Api/Notif.Api.csproj
COPY . .
RUN dotnet publish src/Notif.Api/Notif.Api.csproj -c Release -o /app/publish

FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS runtime
WORKDIR /app
COPY --from=build /app/publish .
EXPOSE 5006
ENV ASPNETCORE_URLS=http://+:5006
RUN apt-get update && apt-get install -y --no-install-recommends curl && rm -rf /var/lib/apt/lists/*
USER app
HEALTHCHECK CMD curl -f http://localhost:5006/health || exit 1
ENTRYPOINT ["dotnet", "Notif.Api.dll"]
