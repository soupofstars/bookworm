# ---- Build stage ----
FROM mcr.microsoft.com/dotnet/sdk:8.0 AS build
WORKDIR /src

# copy project file and restore dependencies
COPY ["Bookworm.csproj", "./"]
RUN dotnet restore "Bookworm.csproj"

# copy everything and build
COPY . .

# Map Docker's TARGETARCH to a .NET runtime identifier so native deps (e_sqlite3) match the target platform.
ARG TARGETARCH
RUN set -eux; \
    if [ "$TARGETARCH" = "amd64" ]; then RID=linux-x64; \
    elif [ "$TARGETARCH" = "arm64" ]; then RID=linux-arm64; \
    else RID="linux-$TARGETARCH"; fi; \
    dotnet publish "Bookworm.csproj" -c Release -o /app/publish /p:UseAppHost=false -r "$RID" --self-contained false

# ---- Runtime stage ----
FROM mcr.microsoft.com/dotnet/aspnet:8.0 AS final
WORKDIR /app

COPY --from=build /app/publish .
ENV ASPNETCORE_URLS=http://+:8080
EXPOSE 8080

ENTRYPOINT ["dotnet", "Bookworm.dll"]
