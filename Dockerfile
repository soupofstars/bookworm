# ---- Build stage ----
FROM mcr.microsoft.com/dotnet/sdk:8.0 AS build
WORKDIR /src

# copy project file and restore dependencies
COPY ["Bookworm.Api.csproj", "./"]
RUN dotnet restore "Bookworm.Api.csproj"

# copy everything and build
COPY . .
RUN dotnet publish "Bookworm.Api.csproj" -c Release -o /app/publish /p:UseAppHost=false

# ---- Runtime stage ----
FROM mcr.microsoft.com/dotnet/aspnet:8.0 AS final
WORKDIR /app

COPY --from=build /app/publish .
ENV ASPNETCORE_URLS=http://+:8080
EXPOSE 8080

ENTRYPOINT ["dotnet", "Bookworm.Api.dll"]
