FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /source
COPY Directory.Build.props .
COPY src/NotificationHub.Api/NotificationHub.Api.csproj src/NotificationHub.Api/
RUN dotnet restore src/NotificationHub.Api/NotificationHub.Api.csproj
COPY src/ src/
RUN dotnet publish src/NotificationHub.Api/NotificationHub.Api.csproj -c Release --no-restore -o /publish

FROM mcr.microsoft.com/dotnet/aspnet:10.0
WORKDIR /app
COPY --from=build /publish .
RUN mkdir -p /keys && chown $APP_UID:$APP_UID /keys
USER $APP_UID
ENV ASPNETCORE_HTTP_PORTS=8080 DataProtection__KeyPath=/keys
EXPOSE 8080
ENTRYPOINT ["dotnet", "NotificationHub.Api.dll"]
