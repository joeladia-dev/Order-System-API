FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src

COPY ["src/ShippingService/ShippingService.csproj", "src/ShippingService/"]
COPY ["src/Shared.Contracts/Shared.Contracts.csproj", "src/Shared.Contracts/"]
RUN dotnet restore "src/ShippingService/ShippingService.csproj"

COPY . .
RUN dotnet publish "src/ShippingService/ShippingService.csproj" -c Release -o /app/publish /p:UseAppHost=false

FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS final
WORKDIR /app
EXPOSE 8080
ENV ASPNETCORE_URLS=http://+:8080

COPY --from=build /app/publish .
ENTRYPOINT ["dotnet", "ShippingService.dll"]
