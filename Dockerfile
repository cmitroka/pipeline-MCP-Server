# Build the app in a full SDK image, then copy only the published output
# into a smaller runtime image. That keeps the image you deploy smaller.

FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src

# Copy project file first so NuGet restore is cached when only code changes.
COPY JobPipelineMcp.csproj .
RUN dotnet restore JobPipelineMcp.csproj

# Copy the rest of the source and publish a Release build.
COPY . .
RUN dotnet publish JobPipelineMcp.csproj -c Release -o /app --no-restore

FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS final
WORKDIR /app

# Container Apps (and our earlier setup) expect the app on port 8080.
ENV ASPNETCORE_URLS=http://+:8080
EXPOSE 8080

COPY --from=build /app .
ENTRYPOINT ["dotnet", "JobPipelineMcp.dll"]
