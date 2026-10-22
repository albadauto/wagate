# syntax=docker/dockerfile:1
FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src
COPY Directory.Build.props ./
COPY src/WaGate.Domain/WaGate.Domain.csproj src/WaGate.Domain/
COPY src/WaGate.Shared/WaGate.Shared.csproj src/WaGate.Shared/
COPY src/WaGate.Infrastructure/WaGate.Infrastructure.csproj src/WaGate.Infrastructure/
COPY src/WaGate.Api/WaGate.Api.csproj src/WaGate.Api/
RUN dotnet restore src/WaGate.Api/WaGate.Api.csproj
COPY src/ src/
RUN dotnet publish src/WaGate.Api/WaGate.Api.csproj -c Release -o /app --no-restore

FROM mcr.microsoft.com/dotnet/aspnet:10.0
WORKDIR /app
COPY --from=build /app .
ENV ASPNETCORE_URLS=http://+:8080
EXPOSE 8080
ENTRYPOINT ["dotnet", "WaGate.Api.dll"]
