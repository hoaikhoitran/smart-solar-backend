# =========================
# BUILD
# =========================
FROM mcr.microsoft.com/dotnet/sdk:8.0 AS build

WORKDIR /src

COPY ["src/SmartSolar.Api/SmartSolar.Api.csproj", "src/SmartSolar.Api/"]
COPY ["src/SmartSolar.Infrastructure/SmartSolar.Infrastructure.csproj", "src/SmartSolar.Infrastructure/"]
COPY ["src/SmartSolar.Modules/SmartSolar.Modules.csproj", "src/SmartSolar.Modules/"]

RUN dotnet restore "src/SmartSolar.Api/SmartSolar.Api.csproj"

COPY . .

RUN dotnet publish \
    "src/SmartSolar.Api/SmartSolar.Api.csproj" \
    -c Release \
    -o /app/publish \
    --no-restore

# =========================
# RUNTIME
# =========================
FROM mcr.microsoft.com/dotnet/aspnet:8.0 AS runtime

WORKDIR /app

COPY --from=build /app/publish .

EXPOSE 8080

ENV ASPNETCORE_URLS=http://+:8080

ENTRYPOINT ["dotnet", "SmartSolar.Api.dll"]