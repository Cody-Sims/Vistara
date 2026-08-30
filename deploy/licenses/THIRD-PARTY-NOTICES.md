# Third-party notices

Vistara container images dynamically link the following third-party software.
The image SBOM and `/usr/share/vistara/provenance/libvips.json` record the
packaged native version and source digest.

## libvips 8.18.6

- License: GNU Lesser General Public License 2.1 or later
  (`LGPL-2.1-or-later`)
- Upstream: <https://github.com/libvips/libvips>
- Corresponding source:
  <https://github.com/libvips/libvips/releases/download/v8.18.6/vips-8.18.6.tar.xz>
- SHA-256:
  `3c41e1d5458081bfa4a5bc54e116c46259c75c6760a18027764555632b9dda3e`
- Build recipe: `deploy/containers/build-libvips-runtime.sh`

The containers use libvips as a replaceable shared library. The complete
LGPL-2.1 license is installed at `/usr/share/licenses/libvips/LICENSE`.
Vistara does not modify the libvips source. Recipients may replace the shared
library with an ABI-compatible build. If the corresponding source URL becomes
unavailable, the Vistara maintainers offer to provide the exact source used for
these binaries, for no more than the cost of distribution, for at least three
years after the image was distributed. Request it through the repository issue
tracker and identify the image digest.

## NetVips 3.2.0

- License: MIT
- Upstream: <https://github.com/kleisauke/net-vips>
- Corresponding source:
  <https://github.com/kleisauke/net-vips/tree/v3.2.0>

The complete MIT notice is in `deploy/licenses/NetVips-MIT.txt` and is
installed in container images at `/usr/share/licenses/netvips/LICENSE`.
