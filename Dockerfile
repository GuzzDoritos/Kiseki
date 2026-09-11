# Build stage
FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src

# Copy csproj files and restore dependencies
COPY ["Kiseki.Core/Kiseki.Core.csproj", "Kiseki.Core/"]
COPY ["Kiseki.Web/Kiseki.Web.csproj", "Kiseki.Web/"]
RUN dotnet restore "Kiseki.Web/Kiseki.Web.csproj"

# Copy remaining source code and publish
COPY . .
RUN dotnet publish "Kiseki.Web/Kiseki.Web.csproj" -c Release -o /app/publish /p:UseAppHost=false

# Runtime stage
FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS final
WORKDIR /app
COPY --from=build /app/publish .

# Set default port (Render will also provide $PORT if needed)
ENV ASPNETCORE_HTTP_PORTS=8080

EXPOSE 8080

ENTRYPOINT ["dotnet", "Kiseki.Web.dll"]
