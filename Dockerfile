# The Piper voices for spoken announcements (Services/Speech), pinned by SHA-256.
FROM debian:bookworm-slim AS voices
RUN apt-get update && apt-get install -y --no-install-recommends ca-certificates curl bzip2 && rm -rf /var/lib/apt/lists/*
COPY scripts/download-voices.sh /download-voices.sh
RUN bash /download-voices.sh /voices

FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src

COPY src/Lanyard.Shared/                          Lanyard.Shared/
COPY src/Lanyard.Infrastructure/                  Lanyard.Infrastructure/
COPY src/Lanyard.Server/LanyardServices/          Lanyard.Server/LanyardServices/
COPY src/Lanyard.Server/LanyardAPI/               Lanyard.Server/LanyardAPI/
COPY src/Lanyard.Server/LanyardApp/               Lanyard.Server/LanyardApp/

WORKDIR /src/Lanyard.Server/LanyardApp
RUN dotnet publish Lanyard.App.csproj -c Release -o /app/publish --no-self-contained
# The speech engine's NuGet package carries native libraries for every platform (about 340 MB);
# this image only ever runs on linux-x64.
RUN find /app/publish/runtimes -mindepth 1 -maxdepth 1 -type d ! -name linux-x64 ! -name linux ! -name unix -exec rm -rf {} +

FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS final
WORKDIR /app
COPY --from=build /app/publish .
COPY --from=voices /voices ./voices

ENV ASPNETCORE_HTTP_PORTS=8080
EXPOSE 8080

ENTRYPOINT ["dotnet", "Lanyard.App.dll"]