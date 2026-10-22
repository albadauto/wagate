# syntax=docker/dockerfile:1
FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src
COPY Directory.Build.props ./
COPY src/WaGate.Shared/WaGate.Shared.csproj src/WaGate.Shared/
COPY src/WaGate.Web/WaGate.Web.csproj src/WaGate.Web/
RUN dotnet restore src/WaGate.Web/WaGate.Web.csproj
COPY src/WaGate.Shared/ src/WaGate.Shared/
COPY src/WaGate.Web/ src/WaGate.Web/
# o CSS do Tailwind já vem compilado em wwwroot/app.css (npm run css:build)
RUN dotnet publish src/WaGate.Web/WaGate.Web.csproj -c Release -o /app --no-restore

FROM mcr.microsoft.com/dotnet/aspnet:10.0
WORKDIR /app
COPY --from=build /app .
ENV ASPNETCORE_URLS=http://+:8080
EXPOSE 8080
ENTRYPOINT ["dotnet", "WaGate.Web.dll"]
