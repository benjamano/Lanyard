FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src

COPY src/Lanyard.Shared/                          Lanyard.Shared/
COPY src/Lanyard.Infrastructure/                  Lanyard.Infrastructure/
COPY src/Lanyard.Server/LanyardServices/          Lanyard.Server/LanyardServices/
COPY src/Lanyard.Server/LanyardAPI/               Lanyard.Server/LanyardAPI/
COPY src/Lanyard.Server/LanyardApp/               Lanyard.Server/LanyardApp/

WORKDIR /src/Lanyard.Server/LanyardApp
RUN dotnet publish Lanyard.App.csproj -c Release -o /app/publish --no-self-contained

FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS final
WORKDIR /app
COPY --from=build /app/publish .

ENV ASPNETCORE_HTTP_PORTS=8080
EXPOSE 8080

# Run as the image's built-in unprivileged user rather than root (Security Issue 9.13). Port 8080
# needs no privileges; uploads go to the storage bucket outside Development, and the local-disk
# fallback writes under $HOME (/home/app), which this user owns.
USER app

ENTRYPOINT ["dotnet", "Lanyard.App.dll"]