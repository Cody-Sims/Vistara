#!/usr/bin/env bash
set -euo pipefail

api_image="${API_IMAGE:-vistara-api:smoke}"
worker_image="${WORKER_IMAGE:-vistara-worker:smoke}"
api_container="vistara-api-smoke-$$"
worker_container="vistara-worker-smoke-$$"

cleanup() {
  docker rm --force "$api_container" "$worker_container" >/dev/null 2>&1 || true
}
trap cleanup EXIT

assert_image_runtime() {
  local image="$1"
  local configured_user

  configured_user="$(docker image inspect "$image" --format '{{.Config.User}}')"
  if [[ -z "$configured_user" || "$configured_user" == "0" || "$configured_user" == "root" ]]; then
    echo "$image must configure a non-root runtime user" >&2
    return 1
  fi

  docker run --rm --entrypoint sh "$image" -ec '
    test "$(id -u)" -ne 0
    test -r /usr/share/licenses/libvips/LICENSE
    test -r /usr/share/licenses/netvips/LICENSE
    test -r /usr/share/vistara/provenance/libvips.json
    dpkg-query -W vistara-libvips-runtime |
      grep -E "^vistara-libvips-runtime[[:space:]]+8[.]18[.]6$"
    test "$(vips --version)" = "vips-8.18.6"
    operations="$(vips -l foreign)"
    for operation in jpegload jpegsave pngload pngsave webpload webpsave; do
      printf "%s\n" "$operations" | grep -F "$operation" >/dev/null
    done
    for tool in cc gcc g++ meson ninja; do
      ! command -v "$tool" >/dev/null 2>&1
    done

    cd /var/lib/vistara/data
    printf "P6\n2 2\n255\n\377\000\000\000\377\000\000\000\377\377\377\377" > input.ppm
    vips thumbnail input.ppm output.jpg 1 --height 1
    vips copy output.jpg output.png
    vips copy output.png output.webp
    test "$(vipsheader -f width output.webp)" = "1"
    test "$(vipsheader -f height output.webp)" = "1"
  '
}

common_environment=(
  --env Persistence__Provider=Sqlite
  --env 'Persistence__ConnectionString=Data Source=/var/lib/vistara/data/vistara.db'
  --env Media__Storage__Provider=Local
  --env Media__Storage__Local__RootPath=/var/lib/vistara/media
  --env Media__Imaging__Provider=NetVips
)

assert_image_runtime "$api_image"
assert_image_runtime "$worker_image"

docker run --detach \
  --name "$api_container" \
  --publish 127.0.0.1::8080 \
  "${common_environment[@]}" \
  --env Platform__Authentication__ApiKeys__CurrentPepperVersion=v1 \
  --env Platform__Authentication__ApiKeys__Peppers__v1=BwcHBwcHBwcHBwcHBwcHBwcHBwcHBwcHBwcHBwcHBwc= \
  --env Platform__Authentication__Jwt__Issuers__0__ProfileId=smoke \
  --env Platform__Authentication__Jwt__Issuers__0__Issuer=https://issuer.example \
  --env Platform__Authentication__Jwt__Issuers__0__Audience=vistara-api \
  --env Platform__Authentication__Jwt__Issuers__0__MetadataAddress=https://issuer.example/.well-known/openid-configuration \
  --env Platform__Authentication__Jwt__Issuers__0__AllowedAlgorithms__0=RS256 \
  "$api_image" >/dev/null

api_port="$(docker port "$api_container" 8080/tcp | sed -n 's/.*://p')"
for attempt in {1..30}; do
  if curl --fail --silent "http://127.0.0.1:${api_port}/health/live" >/dev/null; then
    break
  fi
  if [[ "$attempt" == 30 ]]; then
    docker logs "$api_container" >&2
    exit 1
  fi
  sleep 1
done

docker run --detach \
  --name "$worker_container" \
  "${common_environment[@]}" \
  --env Worker__InstanceId=container-smoke \
  --env Worker__Jobs__MaximumConcurrency=1 \
  "$worker_image" >/dev/null

for attempt in {1..30}; do
  worker_logs="$(docker logs "$worker_container" 2>&1 || true)"
  if grep -F "Application started." <<<"$worker_logs" >/dev/null; then
    break
  fi
  if grep -E "Native libvips is unavailable|missing a required JPEG, PNG, or WebP codec" \
    <<<"$worker_logs" >/dev/null; then
    printf "%s\n" "$worker_logs" >&2
    exit 1
  fi
  if [[ "$attempt" == 30 ]]; then
    printf "%s\n" "$worker_logs" >&2
    exit 1
  fi
  sleep 1
done

echo "API and Worker container smoke tests passed."
