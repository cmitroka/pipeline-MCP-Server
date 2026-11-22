# Build the app in a full SDK image, then copy only the published output
# into a smaller runtime image. That keeps the image you deploy smaller.

FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src

# Copy project file first so NuGet restore is cached when only code changes.
COPY JobPipelineMcp.csproj .
RUN dotnet restore JobPipelineMcp.csproj

# Copy the rest of the source and publish a Release build.
# Stamp BuildTimestamp so server_build_time /health report when this image was built.
COPY . .
ARG BUILD_TIMESTAMP
RUN stamp="${BUILD_TIMESTAMP:-$(date -u +%Y-%m-%dT%H:%M:%SZ)}" \
 && dotnet publish JobPipelineMcp.csproj -c Release -o /app --no-restore \
      -p:BuildTimestamp="$stamp"

FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS final
WORKDIR /app

# Container Apps (and our earlier setup) expect the app on port 8080.
ENV ASPNETCORE_URLS=http://+:8080
EXPOSE 8080

COPY --from=build /app .
ENTRYPOINT ["dotnet", "JobPipelineMcp.dll"]
