FROM microsoft/dotnet:2.1-sdk-alpine AS build
WORKDIR /app

# copy csproj and restore as distinct layers
COPY nuget.config .
COPY *.sln .
COPY HomeAutio.Mqtt.Neato/*.csproj ./HomeAutio.Mqtt.Neato/
RUN dotnet restore

# copy and build everything else
COPY HomeAutio.Mqtt.Neato/. ./HomeAutio.Mqtt.Neato/
RUN dotnet build -c Release

# publish
FROM build AS publish
WORKDIR /app/HomeAutio.Mqtt.Neato
RUN dotnet publish -o out

# build runtime image
FROM microsoft/dotnet:2.1-runtime-alpine AS runtime
WORKDIR /app
COPY --from=publish /app/HomeAutio.Mqtt.Neato/out ./

ENV ENVIRONMENT Production

VOLUME ["/app/appsettings.Production.json", "/app/logs"]
ENTRYPOINT ["dotnet", "HomeAutio.Mqtt.Neato.dll"]