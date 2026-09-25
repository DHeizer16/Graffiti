# Stage 1: Build & Publish
FROM mcr.microsoft.com/dotnet/sdk:9.0 AS build
WORKDIR /src

# Copy project file and restore dependencies
COPY ["GlobalGraffitiWall.API.csproj", "./"]
RUN dotnet restore "GlobalGraffitiWall.API.csproj"

# Copy remaining source code and publish
COPY . .
RUN dotnet publish "GlobalGraffitiWall.API.csproj" -c Release -o /app/publish /p:UseAppHost=false

# Stage 2: Runtime
FROM mcr.microsoft.com/dotnet/aspnet:9.0 AS final
WORKDIR /app

# In .NET 8 and 9, default container HTTP port is 8080
ENV ASPNETCORE_HTTP_PORTS=8080
EXPOSE 8080

COPY --from=build /app/publish .

ENTRYPOINT ["dotnet", "GlobalGraffitiWall.API.dll"]
