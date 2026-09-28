FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src
COPY src/ChamXanh.Api/ChamXanh.Api.csproj src/ChamXanh.Api/
RUN dotnet restore src/ChamXanh.Api/ChamXanh.Api.csproj
COPY src/ src/
RUN dotnet publish src/ChamXanh.Api/ChamXanh.Api.csproj -c Release -o /app --no-restore

FROM mcr.microsoft.com/dotnet/aspnet:10.0
WORKDIR /app
COPY --from=build /app .
ENV ASPNETCORE_ENVIRONMENT=Production
ENTRYPOINT ["dotnet", "ChamXanh.Api.dll"]
