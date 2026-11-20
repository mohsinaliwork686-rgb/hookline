# One build for both images: hookline itself (the default target) and the demo receiver.

FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src
COPY global.json Directory.Build.props Directory.Packages.props .editorconfig ./
COPY src/Hookline.Core/Hookline.Core.csproj src/Hookline.Core/
COPY src/Hookline.Api/Hookline.Api.csproj src/Hookline.Api/
COPY src/Hookline.DemoReceiver/Hookline.DemoReceiver.csproj src/Hookline.DemoReceiver/
RUN dotnet restore src/Hookline.Api/Hookline.Api.csproj \
 && dotnet restore src/Hookline.DemoReceiver/Hookline.DemoReceiver.csproj
COPY src/ src/
RUN dotnet publish src/Hookline.Api/Hookline.Api.csproj --configuration Release --no-restore --output /app/hookline \
 && dotnet publish src/Hookline.DemoReceiver/Hookline.DemoReceiver.csproj --configuration Release --no-restore --output /app/demo

FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS demo
WORKDIR /app
COPY --from=build /app/demo ./
ENV ASPNETCORE_HTTP_PORTS=8080
EXPOSE 8080
USER $APP_UID
ENTRYPOINT ["dotnet", "Hookline.DemoReceiver.dll"]

FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS hookline
WORKDIR /app
COPY --from=build /app/hookline ./
ENV ASPNETCORE_HTTP_PORTS=8080
EXPOSE 8080
USER $APP_UID
ENTRYPOINT ["dotnet", "Hookline.Api.dll"]
