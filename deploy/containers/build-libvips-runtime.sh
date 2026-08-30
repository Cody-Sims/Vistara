#!/usr/bin/env bash
set -euo pipefail

: "${LIBVIPS_VERSION:?LIBVIPS_VERSION is required}"
: "${LIBVIPS_SOURCE_SHA256:?LIBVIPS_SOURCE_SHA256 is required}"
: "${LIBVIPS_SOURCE_URL:?LIBVIPS_SOURCE_URL is required}"

export DEBIAN_FRONTEND=noninteractive

apt-get update
apt-get install --yes --no-install-recommends \
  build-essential \
  ca-certificates \
  curl \
  libexpat1-dev \
  libglib2.0-dev \
  libjpeg-turbo8-dev \
  liblcms2-dev \
  libpng-dev \
  libwebp-dev \
  meson \
  ninja-build \
  pkg-config \
  xz-utils \
  zlib1g-dev

archive="/var/cache/vips-${LIBVIPS_VERSION}.tar.xz"
source_dir="/usr/src/vips-${LIBVIPS_VERSION}"
package_root="/opt/vistara-libvips-package"
architecture="$(dpkg --print-architecture)"
multiarch="$(dpkg-architecture --query DEB_HOST_MULTIARCH)"

curl --fail --location --show-error --silent \
  --proto '=https' \
  --proto-redir '=https' \
  --retry 3 \
  --tlsv1.2 \
  --output "$archive" \
  "$LIBVIPS_SOURCE_URL"
printf '%s *%s\n' "$LIBVIPS_SOURCE_SHA256" "$archive" | sha256sum --check -

mkdir --parents "$source_dir" "$package_root/DEBIAN" /out
tar --extract --file "$archive" --xz --strip-components=1 --directory "$source_dir"

meson setup "$source_dir/build" "$source_dir" \
  --prefix=/usr \
  --libdir="lib/${multiarch}" \
  --buildtype=release \
  --strip \
  -Ddeprecated=false \
  -Dexamples=false \
  -Dcplusplus=false \
  -Ddocs=false \
  -Dmodules=disabled \
  -Dintrospection=disabled \
  -Dcfitsio=disabled \
  -Dcgif=disabled \
  -Dexif=disabled \
  -Dfftw=disabled \
  -Dfontconfig=disabled \
  -Darchive=disabled \
  -Dheif=disabled \
  -Dheif-module=disabled \
  -Dimagequant=disabled \
  -Djpeg=enabled \
  -Duhdr=disabled \
  -Djpeg-xl=disabled \
  -Djpeg-xl-module=disabled \
  -Dlcms=enabled \
  -Dmagick=disabled \
  -Dmagick-module=disabled \
  -Dmatio=disabled \
  -Dnifti=disabled \
  -Dopenexr=disabled \
  -Dopenjpeg=disabled \
  -Dopenslide=disabled \
  -Dopenslide-module=disabled \
  -Dhighway=disabled \
  -Dorc=disabled \
  -Dpangocairo=disabled \
  -Dpdfium=disabled \
  -Dpng=enabled \
  -Dpoppler=disabled \
  -Dpoppler-module=disabled \
  -Dquantizr=disabled \
  -Draw=disabled \
  -Drsvg=disabled \
  -Dspng=disabled \
  -Dtiff=disabled \
  -Dwebp=enabled \
  -Dzlib=enabled \
  -Dnsgif=false \
  -Dppm=true \
  -Danalyze=false \
  -Dradiance=false

meson compile -C "$source_dir/build" --jobs "$(nproc)" --verbose
DESTDIR="$package_root" meson install -C "$source_dir/build" --no-rebuild

rm --recursive --force \
  "$package_root/usr/include" \
  "$package_root/usr/lib/${multiarch}/pkgconfig" \
  "$package_root/usr/share/man"

mkdir --parents \
  "$package_root/usr/share/doc/vistara-libvips-runtime" \
  "$package_root/usr/share/licenses/libvips" \
  "$package_root/usr/share/vistara/provenance"
cp "$source_dir/LICENSE" "$package_root/usr/share/licenses/libvips/LICENSE"

cat >"$package_root/usr/share/vistara/provenance/libvips.json" <<EOF
{
  "name": "libvips",
  "version": "${LIBVIPS_VERSION}",
  "license": "LGPL-2.1-or-later",
  "source": "${LIBVIPS_SOURCE_URL}",
  "sourceSha256": "${LIBVIPS_SOURCE_SHA256}",
  "buildSystem": "meson",
  "distribution": "Ubuntu 24.04 noble",
  "architecture": "${architecture}",
  "features": ["jpeg", "png", "webp", "lcms2", "zlib"],
  "linkage": "shared"
}
EOF

cat >"$package_root/usr/share/doc/vistara-libvips-runtime/copyright" <<EOF
Format: https://www.debian.org/doc/packaging-manuals/copyright-format/1.0/
Upstream-Name: libvips
Source: ${LIBVIPS_SOURCE_URL}

Files: *
Copyright: The libvips contributors
License: LGPL-2.1-or-later
 The complete license is installed at /usr/share/licenses/libvips/LICENSE.
EOF

cat >"$package_root/DEBIAN/control" <<EOF
Package: vistara-libvips-runtime
Version: ${LIBVIPS_VERSION}
Architecture: ${architecture}
Maintainer: Vistara maintainers
Section: libs
Priority: optional
Depends: libexpat1, libglib2.0-0t64, libjpeg-turbo8, liblcms2-2, libpng16-16t64, libwebp7, libwebpdemux2, libwebpmux3, zlib1g
Homepage: https://github.com/libvips/libvips
Description: Minimal libvips runtime for Vistara
 Unmodified libvips built from the official release source with JPEG, PNG,
 WebP, ICC color management, and zlib support.
EOF

dpkg-deb --build --root-owner-group \
  "$package_root" \
  "/out/vistara-libvips-runtime_${LIBVIPS_VERSION}_${architecture}.deb"
cp "/out/vistara-libvips-runtime_${LIBVIPS_VERSION}_${architecture}.deb" \
  /out/vistara-libvips-runtime.deb

dpkg --install /out/vistara-libvips-runtime.deb
test "$(vips --version)" = "vips-${LIBVIPS_VERSION}"
operations="$(vips -l foreign)"
for operation in jpegload jpegsave pngload pngsave webpload webpsave; do
  grep --fixed-strings "$operation" <<<"$operations" >/dev/null
done
