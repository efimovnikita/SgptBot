FROM mcr.microsoft.com/dotnet/sdk:8.0-jammy AS build-env
WORKDIR /source
COPY . .
RUN dotnet publish "VectorStoreWebApi.csproj" -c Release -o /app/publish

FROM mcr.microsoft.com/dotnet/aspnet:8.0-jammy AS runtime
WORKDIR /app
COPY --from=build-env /app/publish .

EXPOSE 80

ENTRYPOINT ["dotnet", "VectorStoreWebApi.dll"]