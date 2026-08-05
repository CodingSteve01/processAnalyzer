FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
ARG VERSION=0.0.0-dev
WORKDIR /src
COPY ProcessAnalyzer.Web/ProcessAnalyzer.Web.csproj ProcessAnalyzer.Web/
RUN dotnet restore ProcessAnalyzer.Web/ProcessAnalyzer.Web.csproj
COPY ProcessAnalyzer.Web/ ProcessAnalyzer.Web/
RUN dotnet publish ProcessAnalyzer.Web/ProcessAnalyzer.Web.csproj -c Release /p:InformationalVersion=$VERSION -o /out

FROM mcr.microsoft.com/dotnet/aspnet:10.0
WORKDIR /app
# curl is the healthcheck client — the compose healthcheck calls /health from inside the container.
RUN apt-get update \
    && apt-get install -y --no-install-recommends curl \
    && rm -rf /var/lib/apt/lists/*
COPY --from=build /out/ ./
ENV ASPNETCORE_URLS=http://0.0.0.0:5100
ENTRYPOINT ["dotnet", "ProcessAnalyzer.Web.dll"]
