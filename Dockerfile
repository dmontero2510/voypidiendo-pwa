FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src
COPY src/VoyPidiendo.Api/VoyPidiendo.Api.csproj src/VoyPidiendo.Api/
RUN dotnet restore src/VoyPidiendo.Api/VoyPidiendo.Api.csproj
COPY src/VoyPidiendo.Api/ src/VoyPidiendo.Api/
RUN dotnet publish src/VoyPidiendo.Api/VoyPidiendo.Api.csproj -c Release -o /app --no-restore

FROM mcr.microsoft.com/dotnet/aspnet:10.0
WORKDIR /app
COPY --from=build /app .
COPY openssl-legacy.cnf /app/openssl-legacy.cnf
ENV ASPNETCORE_ENVIRONMENT=Production
ENV ASPNETCORE_FORWARDEDHEADERS_ENABLED=true
ENV OPENSSL_CONF=/app/openssl-legacy.cnf
EXPOSE 10000
ENTRYPOINT ["sh", "-c", "ASPNETCORE_URLS=http://0.0.0.0:${PORT:-10000} exec dotnet VoyPidiendo.Api.dll"]
