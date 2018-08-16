FROM microsoft/dotnet:2.1-sdk-alpine AS build
WORKDIR /app

# copy csproj and restore as distinct layers
COPY nuget.config .
COPY *.sln .
COPY HomeAutio.Mqtt.Harmony/*.csproj ./HomeAutio.Mqtt.Harmony/
RUN dotnet restore

# copy and build everything else
COPY HomeAutio.Mqtt.Harmony/. ./HomeAutio.Mqtt.Harmony/
RUN dotnet build -c Release

# publish
FROM build AS publish
WORKDIR /app/HomeAutio.Mqtt.Harmony
RUN dotnet publish -o out

# build runtime image
FROM microsoft/dotnet:2.1-aspnetcore-runtime-stretch-slim-arm32v7 AS runtime
WORKDIR /app
COPY --from=publish /app/HomeAutio.Mqtt.Harmony/out ./

ENV ENVIRONMENT Production

VOLUME ["/app/appsettings.Production.json", "/app/logs"]
ENTRYPOINT ["dotnet", "HomeAutio.Mqtt.Harmony.dll"]