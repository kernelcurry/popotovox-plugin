#!/usr/bin/env bash
#
# Build PopotoVox inside a Docker container and produce both:
#   - ./dist/PopotoVox/      (loose files for Dalamud's "Dev Plugin Location")
#   - ./dist/PopotoVox.zip   (release artifact for the custom repo / Plugin Installer)
#
# No host .NET SDK required.

set -euo pipefail

cd "$(dirname "$0")"

if ! command -v docker >/dev/null 2>&1; then
  echo "docker not found on PATH. Install Docker Desktop (macOS/Windows) or docker (Linux) and retry." >&2
  exit 1
fi

mkdir -p dist
rm -rf dist/*

IMAGE_TAG=popotovox-build:latest
CONTAINER_NAME="popotovox-extract-$$"

cleanup() {
  docker rm -f "$CONTAINER_NAME" >/dev/null 2>&1 || true
}
trap cleanup EXIT

echo ">> Building PopotoVox image…"
docker build -t "$IMAGE_TAG" .

echo ">> Extracting build output…"
docker create --name "$CONTAINER_NAME" "$IMAGE_TAG" >/dev/null
docker cp "$CONTAINER_NAME:/src/plugin/bin/Release/PopotoVox" ./dist/

if [ ! -f dist/PopotoVox/latest.zip ]; then
  echo "Expected dist/PopotoVox/latest.zip not produced — check the build log above." >&2
  exit 1
fi

# DalamudPackager produces dist/PopotoVox/{latest.zip, PopotoVox.json}. To get
# a folder loadable as a Dev Plugin Location AND a clean release artifact, we
# move the zip up one level (renamed) and extract its loose files into the
# PopotoVox/ folder.
mv dist/PopotoVox/latest.zip dist/PopotoVox.zip
( cd dist/PopotoVox && unzip -q -o ../PopotoVox.zip )

echo
echo ">> Built:"
echo "   ./dist/PopotoVox/      (loose files — point Dalamud's Dev Plugin Location here)"
echo "   ./dist/PopotoVox.zip   (release artifact — uploaded by CI to GitHub Releases)"
ls -lh dist/PopotoVox/

cat <<EOF

Load the plugin into Dalamud one of two ways:
  1. Custom repo (preferred):
     - Open Dalamud Settings → Experimental → Custom Plugin Repositories.
     - Add: https://raw.githubusercontent.com/kernelcurry/popotovox-plugin/main/repo.json
     - Find PopotoVox in the Plugin Installer and install.
  2. Dev plugin location (no GitHub releases needed):
     - Open Dalamud Settings → Experimental → Dev Plugin Locations.
     - Add: $(pwd)/dist
     - Enable PopotoVox in the Dev Tools tab of the Plugin Installer.
EOF
