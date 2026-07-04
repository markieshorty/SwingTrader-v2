FROM mcr.microsoft.com/dotnet/aspnet:9.0 AS base
WORKDIR /app
EXPOSE 5001

FROM mcr.microsoft.com/dotnet/sdk:9.0 AS build
WORKDIR /src

COPY ["SwingTrader.Api/SwingTrader.Api.csproj", "SwingTrader.Api/"]
COPY ["SwingTrader.Core/SwingTrader.Core.csproj", "SwingTrader.Core/"]
COPY ["SwingTrader.Data/SwingTrader.Data.csproj", "SwingTrader.Data/"]
COPY ["SwingTrader.Agents/SwingTrader.Agents.csproj", "SwingTrader.Agents/"]
COPY ["SwingTrader.Infrastructure/SwingTrader.Infrastructure.csproj", "SwingTrader.Infrastructure/"]

RUN dotnet restore "SwingTrader.Api/SwingTrader.Api.csproj"
COPY . .

WORKDIR "/src/SwingTrader.Api"
RUN dotnet build -c Release -o /app/build

FROM build AS publish
RUN dotnet publish -c Release -o /app/publish --no-restore

FROM base AS final
WORKDIR /app
COPY --from=publish /app/publish .
ENV ASPNETCORE_ENVIRONMENT=Production
ENV ASPNETCORE_URLS=http://+:5001
ENTRYPOINT ["dotnet", "SwingTrader.Api.dll"]
