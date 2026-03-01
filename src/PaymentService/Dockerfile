FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src

COPY ["src/PaymentService/PaymentService.csproj", "src/PaymentService/"]
COPY ["src/Shared.Contracts/Shared.Contracts.csproj", "src/Shared.Contracts/"]
RUN dotnet restore "src/PaymentService/PaymentService.csproj"

COPY . .
RUN dotnet publish "src/PaymentService/PaymentService.csproj" -c Release -o /app/publish /p:UseAppHost=false

FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS final
WORKDIR /app
EXPOSE 8080
ENV ASPNETCORE_URLS=http://+:8080

COPY --from=build /app/publish .
ENTRYPOINT ["dotnet", "PaymentService.dll"]
