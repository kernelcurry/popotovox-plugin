# syntax=docker/dockerfile:1.7
#
# Reproducible build container for PopotoVox, using Dalamud.NET.Sdk/15.0.0.
#
# Why this looks simpler than a normal multi-stage build:
#   - Dalamud.NET.Sdk handles TargetFramework, PlatformTarget, DalamudPackager,
#     and reference DLL resolution. We only have to:
#       1. provide the Dalamud reference DLLs at $DALAMUD_HOME, and
#       2. pass -p:EnableWindowsTargeting=true when building from Linux.
#   - build.sh extracts the build output via `docker create` + `docker cp` so
#     we don't need the BuildKit-only `--output` flag.

FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build

# Dalamud reference DLLs are pulled from the official distribution channel.
# TODO(security, PRD §8.1): pin DALAMUD_DISTRIB_URL to a specific release tag
# + verify a SHA-256 checksum once we cut v0.1.
ARG DALAMUD_DISTRIB_URL=https://goatcorp.github.io/dalamud-distrib/latest.zip

RUN apt-get update \
 && apt-get install -y --no-install-recommends curl unzip ca-certificates \
 && rm -rf /var/lib/apt/lists/*

RUN mkdir -p /opt/dalamud \
 && curl -fsSL "$DALAMUD_DISTRIB_URL" -o /tmp/dalamud.zip \
 && unzip -q /tmp/dalamud.zip -d /opt/dalamud \
 && rm /tmp/dalamud.zip \
 && test -f /opt/dalamud/Dalamud.dll \
        || (echo "Dalamud.dll missing after extracting $DALAMUD_DISTRIB_URL" && ls /opt/dalamud && exit 1)

# Dalamud.NET.Sdk reads $DALAMUD_HOME to locate the reference DLLs on Linux.
ENV DALAMUD_HOME=/opt/dalamud

# Everything the plugin build references: the plugin itself plus the helper projects its csproj
# publishes/links into the output (the tts-host exe; the voxcpm-host script from M2 on).
WORKDIR /src
COPY plugin/ ./plugin/
COPY tts-host/ ./tts-host/
COPY voxcpm-host/ ./voxcpm-host/

WORKDIR /src/plugin
RUN dotnet restore -p:EnableWindowsTargeting=true

ARG BUILD_CONFIG=Release
RUN dotnet build \
      --no-restore \
      -c $BUILD_CONFIG \
      -p:EnableWindowsTargeting=true

RUN test -d "bin/$BUILD_CONFIG/PopotoVox" \
        || (echo "DalamudPackager output folder missing; see build log" && ls -la bin/$BUILD_CONFIG/ && exit 1)

# build.sh extracts /src/plugin/bin/Release/PopotoVox via `docker cp`.
