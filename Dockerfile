# Build stage
FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src

# Copy project files for layer caching
COPY ["Directory.Build.props", "./"]
COPY ["Networking/TurboTick.Networking.csproj", "Networking/"]
COPY ["Server/TurboTick.Server.csproj", "Server/"]
RUN dotnet restore "Server/TurboTick.Server.csproj"

# Copy source and publish
COPY ["Networking/", "Networking/"]
COPY ["Server/", "Server/"]
WORKDIR /src/Server
RUN dotnet publish "TurboTick.Server.csproj" -c Release -o /app/publish /p:UseAppHost=false

# Runtime stage
FROM mcr.microsoft.com/dotnet/runtime:10.0 AS runtime
WORKDIR /app

# Configure default container environment
ENV TURBOTICK_HOST=0.0.0.0 \
    TURBOTICK_PORT=30572 \
    TURBOTICK_DATA=/data

# Create data directory and assign ownership to non-root app user
RUN mkdir -p /data && chown -R app:app /data /app

COPY --from=build --chown=app:app /app/publish .

USER app
VOLUME /data
EXPOSE 30572

ENTRYPOINT ["dotnet", "TurboTick.Server.dll"]
