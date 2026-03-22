FROM mcr.microsoft.com/dotnet/sdk:8.0 AS build
WORKDIR /src

COPY VoxMind.sln .
COPY src/ ./src/
COPY python_services/protos/ ./python_services/protos/

RUN dotnet restore VoxMind.sln
RUN dotnet publish src/VoxMind.CLI/VoxMind.CLI.csproj -c Release -o /app/publish

FROM mcr.microsoft.com/dotnet/runtime:8.0 AS runtime
WORKDIR /app

# Prérequis audio Linux
RUN apt-get update && apt-get install -y \
    libportaudio2 \
    portaudio19-dev \
    && rm -rf /var/lib/apt/lists/*

COPY --from=build /app/publish .

ENTRYPOINT ["./voxmind"]
