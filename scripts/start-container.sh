#!/bin/bash
set -e

SCRIPT_DIR="$( cd "$( dirname "${BASH_SOURCE[0]}" )" && pwd )"
IMAGE_NAME="ghcr.io/philfanzhou/structadoc:latest"
CONTAINER_NAME="structadoc"
MEMORY_LIMIT="2g"
HTTP_PORT=8080
DATA_PATH="$( cd "$SCRIPT_DIR/.." && pwd )/data"
APP_UID=1654

if [ -n "$(docker ps -q --filter "name=^/${CONTAINER_NAME}$")" ]; then
    echo "Container is already running, stopping it..."; docker stop "$CONTAINER_NAME"
fi
if [ -n "$(docker ps -aq --filter "name=^/${CONTAINER_NAME}$")" ]; then
    echo "Removing old container..."; docker rm "$CONTAINER_NAME"
fi

mkdir -p "${DATA_PATH}"
chown -R "${APP_UID}:${APP_UID}" "${DATA_PATH}"

docker run -d \
  --name "$CONTAINER_NAME" \
  --restart unless-stopped \
  --memory "${MEMORY_LIMIT}" \
  --log-opt max-size=50m \
  --log-opt max-file=3 \
  --add-host=host.docker.internal:host-gateway \
  --read-only \
  --security-opt no-new-privileges:true \
  --cap-drop ALL \
  --tmpfs /tmp:size=256m,mode=1777 \
  -p "${HTTP_PORT}:8080" \
  -e TZ=Asia/Shanghai \
  -v "${DATA_PATH}":/data \
  "$IMAGE_NAME"
