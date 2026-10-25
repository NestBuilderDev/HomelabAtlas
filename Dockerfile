# ---------- Build ----------
FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src
COPY src/HomelabAtlas/HomelabAtlas.csproj src/HomelabAtlas/
RUN dotnet restore src/HomelabAtlas/HomelabAtlas.csproj
COPY src/ src/
RUN dotnet publish src/HomelabAtlas/HomelabAtlas.csproj -c Release -o /app/publish /p:UseAppHost=false

# ---------- Runtime ----------
FROM mcr.microsoft.com/dotnet/aspnet:10.0
WORKDIR /app
ENV ASPNETCORE_HTTP_PORTS=8080 \
    DataDir=/data
RUN mkdir -p /data && chown -R $APP_UID /data
USER $APP_UID
COPY --from=build /app/publish .
VOLUME /data
EXPOSE 8080
ENTRYPOINT ["dotnet", "HomelabAtlas.dll"]
