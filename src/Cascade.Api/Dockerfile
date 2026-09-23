# Build context is the repo root (see docker-compose.yml).
FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /repo
COPY global.json ./
COPY src/Cascade.Api/Cascade.Api.csproj src/Cascade.Api/
RUN dotnet restore src/Cascade.Api/Cascade.Api.csproj
COPY src/Cascade.Api/ src/Cascade.Api/
RUN dotnet publish src/Cascade.Api/Cascade.Api.csproj -c Release -o /app --no-restore

FROM mcr.microsoft.com/dotnet/aspnet:10.0
WORKDIR /app
COPY --from=build /app .
ENV ASPNETCORE_URLS=http://+:8080
EXPOSE 8080
USER $APP_UID
ENTRYPOINT ["dotnet", "Cascade.Api.dll"]
