# syntax=docker/dockerfile:1.7
FROM mcr.microsoft.com/dotnet/sdk:10.0.302-noble@sha256:72dd743782f2ae7e5476fd64f6a460045e3998dc862218b80e6944cba79a01b0 AS build
WORKDIR /src
COPY global.json Directory.Build.props Directory.Packages.props word-mcp.sln ./
COPY src/WordMcp.Api/WordMcp.Api.csproj src/WordMcp.Api/
COPY src/WordMcp.Api/packages.lock.json src/WordMcp.Api/
COPY tests/WordMcp.Tests/WordMcp.Tests.csproj tests/WordMcp.Tests/
COPY tests/WordMcp.Tests/packages.lock.json tests/WordMcp.Tests/
RUN dotnet restore word-mcp.sln --locked-mode
COPY . .
RUN dotnet build word-mcp.sln --configuration Release --no-restore

FROM build AS test
ARG DEBIAN_FRONTEND=noninteractive
RUN apt-get update \
    && apt-get install -y --no-install-recommends \
        libreoffice-writer-nogui=4:24.2.7-0ubuntu0.24.04.6 \
        libreoffice-core-nogui=4:24.2.7-0ubuntu0.24.04.6 \
        python3-uno=4:24.2.7-0ubuntu0.24.04.6 \
        poppler-utils=24.02.0-1ubuntu9.9 \
        fonts-noto-cjk=1:20230817+repack1-3 \
        fonts-noto-mono=20201225-2 \
        ca-certificates=20260601~24.04.1 \
    && apt-get clean \
    && rm -rf /var/lib/apt/lists/*
ENV WORD_MCP_RENDER_INTEGRATION=1
ENTRYPOINT ["dotnet", "test", "word-mcp.sln", "--configuration", "Release", "--no-build", "--logger", "console;verbosity=normal"]

FROM build AS audit
ENTRYPOINT ["/src/scripts/audit-dependencies.sh"]

FROM build AS publish
RUN dotnet publish src/WordMcp.Api/WordMcp.Api.csproj --configuration Release --no-build --output /app/publish

FROM mcr.microsoft.com/dotnet/aspnet:10.0.10-noble@sha256:f1126d438ccc359f51cc6d4701a8deae513856cf10f5fe645d29ea6403dcac6b AS runtime
ARG DEBIAN_FRONTEND=noninteractive
RUN apt-get update \
    && apt-get install -y --no-install-recommends \
        libreoffice-writer-nogui=4:24.2.7-0ubuntu0.24.04.6 \
        libreoffice-core-nogui=4:24.2.7-0ubuntu0.24.04.6 \
        python3-uno=4:24.2.7-0ubuntu0.24.04.6 \
        poppler-utils=24.02.0-1ubuntu9.9 \
        fonts-noto-cjk=1:20230817+repack1-3 \
        fonts-noto-mono=20201225-2 \
        ca-certificates=20260601~24.04.1 \
    && apt-get clean \
    && rm -rf /var/lib/apt/lists/*
WORKDIR /app
COPY --from=publish --chown=1654:1654 /app/publish ./
RUN mkdir -p /data/word-mcp /data/librechat-uploads /data/word-templates \
    && chown -R 1654:1654 /data/word-mcp
ENV ASPNETCORE_URLS=http://0.0.0.0:8080 \
    DOTNET_EnableDiagnostics=0 \
    HOME=/tmp \
    TMPDIR=/tmp
USER 1654:1654
EXPOSE 8080
ENTRYPOINT ["dotnet", "WordMcp.Api.dll"]
