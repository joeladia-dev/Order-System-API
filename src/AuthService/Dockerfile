FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src

COPY ["src/AuthService/AuthService.csproj", "src/AuthService/"]
RUN dotnet restore "src/AuthService/AuthService.csproj"

COPY . .
RUN dotnet publish "src/AuthService/AuthService.csproj" -c Release -o /app/publish /p:UseAppHost=false

FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS final
WORKDIR /app
EXPOSE 8080
ENV ASPNETCORE_URLS=http://+:8080

COPY --from=build /app/publish .
ENTRYPOINT ["dotnet", "AuthService.dll"]
