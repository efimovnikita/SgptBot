FROM mcr.microsoft.com/dotnet/sdk:6.0-alpine AS build-env
WORKDIR /app
COPY *.csproj ./
RUN dotnet restore /p:PublishReadyToRun=true
COPY . ./
RUN dotnet publish /p:PublishReadyToRun=true /p:PublishSingleFile=true --self-contained true -c Release -o out

FROM mcr.microsoft.com/dotnet/runtime:6.0-alpine as final

RUN apk add --no-cache openssl=3.1.4-r0

WORKDIR /app
COPY --from=build-env /app/out .
ENTRYPOINT ["dotnet", "SgptBot.dll"]