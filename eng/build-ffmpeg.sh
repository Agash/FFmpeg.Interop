#!/usr/bin/env bash
# Builds FFmpeg (the release generate/inputs.json pins, the one the bindings are generated from) and the
# libraries it uses, for one Linux runtime, into artifacts/native/<rid>: lib/ for the shared libraries
# the package carries, bin/ for the ffmpeg and ffprobe the tests compare against.
#
#   eng/build-ffmpeg.sh [linux-x64|linux-arm64]      (defaults to this machine's)
#
# Everything is built from pinned, SHA-256-checked source (eng/native-inputs.json), unmodified. The
# libraries FFmpeg uses are linked in statically, except libva and libdrm, whose drivers expect the
# system's own copies; NVIDIA, AMD and Vulkan runtimes are opened at run time. The result is LGPL 2.1:
# no GPL or non-free component is enabled.
#
# Meant for an old glibc (the CI builds in AlmaLinux 8, glibc 2.28, so the libraries load on
# distributions and SBC images far older than the runner) with current kernel interfaces: the DMA-BUF
# header comes from Linux 6.12, so FFmpeg's DMA-BUF synchronisation is compiled in.
#
# Needs a C/C++ toolchain, nasm, cmake, meson, ninja, make, pkg-config, python3, curl, git and patchelf.
set -euo pipefail
shopt -s inherit_errexit

root="$(cd "$(dirname "$0")/.." && pwd)"
case "${1:-}" in
  "") case "$(uname -m)" in x86_64) rid=linux-x64 ;; aarch64) rid=linux-arm64 ;; *) echo "No runtime for $(uname -m)." >&2; exit 1 ;; esac ;;
  linux-x64|linux-arm64) rid=$1 ;;
  *) echo "Usage: $0 [linux-x64|linux-arm64]" >&2; exit 2 ;;
esac

work="$root/artifacts/obj/ffmpeg/$rid"
sources="$root/artifacts/obj/ffmpeg/sources"
prefix="$work/prefix"
out="$root/artifacts/native/$rid"
jobs="$(nproc)"
mkdir -p "$work" "$sources" "$prefix/include" "$prefix/lib" "$prefix/bin"
export PATH="$prefix/bin:$PATH"
export PKG_CONFIG_PATH="$prefix/lib/pkgconfig:$prefix/lib64/pkgconfig:$prefix/share/pkgconfig"
export CFLAGS="-O2 -fPIC" CXXFLAGS="-O2 -fPIC"

# input <file> <name> <field>: one pin's field.
input() { python3 -c 'import json,sys; print(json.load(open(sys.argv[1]))[sys.argv[2]][sys.argv[3]])' "$1" "$2" "$3"; }
generator="$root/generate/inputs.json"
native="$root/eng/native-inputs.json"

# fetch <pins> <name>: the pinned archive, verified and extracted once; prints its directory.
fetch() {
  local pins=$1 name=$2 url sha archive dir
  url="$(input "$pins" "$name" url)"
  sha="$(input "$pins" "$name" sha256)"
  dir="$sources/$name-$sha"
  if [[ ! -d "$dir" ]]; then
    archive="$sources/$name.archive"
    curl -sfL --retry 4 -o "$archive" "$url"
    echo "$sha  $archive" | sha256sum -c --quiet - || { echo "$name: $url does not match its pin." >&2; exit 1; }
    mkdir -p "$dir.tmp"
    tar -xf "$archive" -C "$dir.tmp" --strip-components=1
    rm "$archive"
    mv "$dir.tmp" "$dir"
  fi
  echo "$dir"
}

# Linux's DMA-BUF uAPI, newer than the build image's kernel headers.
dmabuf="$sources/dma-buf.h"
if [[ ! -f "$dmabuf" ]]; then
  curl -sfL --retry 4 -o "$dmabuf" "$(input "$native" linuxUapiDmaBuf url)"
  echo "$(input "$native" linuxUapiDmaBuf sha256)  $dmabuf" | sha256sum -c --quiet -
fi
mkdir -p "$prefix/include/linux"
cp "$dmabuf" "$prefix/include/linux/dma-buf.h"

# Headers only: NVIDIA's codec interface, AMF's, Vulkan's.
make -C "$(fetch "$generator" nvcodec)" PREFIX="$prefix" install >/dev/null
amf="$sources/amf-$(input "$native" amf commit)"
if [[ ! -d "$amf" ]]; then
  git clone -q --filter=blob:none --sparse --no-checkout "$(input "$native" amf git)" "$amf.tmp"
  git -C "$amf.tmp" sparse-checkout set "$(input "$native" amf path)"
  git -C "$amf.tmp" checkout -q "$(input "$native" amf commit)"
  mv "$amf.tmp" "$amf"
fi
rm -rf "$prefix/include/AMF" && cp -r "$amf/$(input "$native" amf path)" "$prefix/include/AMF"
cp -r "$(fetch "$generator" vulkan)/include/." "$prefix/include/"

# glslang compiles FFmpeg's Vulkan shaders at build time; it is not shipped.
if [[ ! -x "$prefix/bin/glslang" ]]; then
  cmake -S "$(fetch "$native" glslang)" -B "$work/glslang" -G Ninja -DCMAKE_BUILD_TYPE=Release \
    -DCMAKE_INSTALL_PREFIX="$prefix" -DENABLE_OPT=OFF -DGLSLANG_TESTS=OFF -DENABLE_SPVREMAPPER=OFF \
    -DBUILD_SHARED_LIBS=OFF >/dev/null
  cmake --build "$work/glslang" --target install >/dev/null
fi

meson_build() { # <name> <pins> [options...]: a meson project into the prefix
  local name=$1 pins=$2; shift 2
  [[ -f "$work/$name.done" ]] && return
  meson setup --reconfigure "$work/$name" "$(fetch "$pins" "$name")" --prefix="$prefix" --libdir=lib \
    --buildtype=release "$@" >/dev/null
  ninja -C "$work/$name" install >/dev/null
  touch "$work/$name.done"
}
cmake_build() { # <name> <pins> [options...]: a CMake project into the prefix
  local name=$1 pins=$2; shift 2
  [[ -f "$work/$name.done" ]] && return
  cmake -S "$(fetch "$pins" "$name")" -B "$work/$name" -G Ninja -DCMAKE_BUILD_TYPE=Release \
    -DCMAKE_INSTALL_PREFIX="$prefix" -DCMAKE_INSTALL_LIBDIR=lib -DCMAKE_POSITION_INDEPENDENT_CODE=ON "$@" >/dev/null
  cmake --build "$work/$name" --target install >/dev/null
  touch "$work/$name.done"
}

# libdrm and libva are linked against shared: at run time the system's copies serve, with its drivers.
meson_build libdrm "$native" -Ddefault_library=shared -Dtests=false -Dman-pages=disabled -Dvalgrind=disabled \
  -Dcairo-tests=disabled -Damdgpu=enabled -Dradeon=disabled -Dnouveau=disabled -Dintel=disabled -Dvmwgfx=disabled
meson_build libva "$generator" -Ddefault_library=shared -Denable_docs=false -Dwith_x11=no -Dwith_glx=no \
  -Dwith_wayland=no -Ddisable_drm=false

# Codec libraries, static, inside FFmpeg's libraries.
meson_build dav1d "$native" -Ddefault_library=static -Denable_tools=false -Denable_tests=false
meson_build openh264 "$native" -Ddefault_library=static -Dtests=disabled
cmake_build libvpl "$native" -DBUILD_SHARED_LIBS=OFF -DBUILD_TOOLS=OFF -DBUILD_EXAMPLES=OFF -DBUILD_TESTS=OFF \
  -DINSTALL_EXAMPLES=OFF
cmake_build svtav1 "$native" -DBUILD_SHARED_LIBS=OFF -DBUILD_APPS=OFF -DBUILD_TESTING=OFF -DBUILD_DEC=OFF
cmake_build kvazaar "$native" -DBUILD_SHARED_LIBS=OFF -DBUILD_TESTS=OFF
if [[ ! -f "$work/libvpx.done" ]]; then
  vpx="$(fetch "$native" libvpx)"
  mkdir -p "$work/libvpx"
  (cd "$work/libvpx" && "$vpx/configure" --prefix="$prefix" --libdir="$prefix/lib" --enable-pic --enable-static \
    --disable-shared --disable-examples --disable-tools --disable-docs --disable-unit-tests \
    --enable-vp9-highbitdepth >/dev/null && make -j"$jobs" >/dev/null && make install >/dev/null)
  touch "$work/libvpx.done"
fi

# FFmpeg: every feature named, nothing autodetected, so a missing dependency fails here instead of
# silently dropping a backend.
ffmpeg="$(fetch "$generator" ffmpeg)"
rm -rf "$work/ffmpeg" "$out" && mkdir -p "$work/ffmpeg" "$out"
(cd "$work/ffmpeg" && "$ffmpeg/configure" \
  --prefix="$work/install" --enable-shared --disable-static --enable-pic --disable-autodetect \
  --disable-doc --disable-ffplay --disable-debug \
  --pkg-config-flags=--static --extra-cflags="-I$prefix/include" \
  --extra-ldflags="-L$prefix/lib -Wl,-rpath-link,$prefix/lib" \
  --extra-libs="-lstdc++ -lm -lpthread -ldl" \
  --enable-pthreads \
  --enable-libdav1d --enable-libsvtav1 --enable-libopenh264 --enable-libkvazaar --enable-libvpx \
  --enable-libdrm --enable-vaapi --enable-vulkan --enable-libvpl --enable-amf \
  --enable-ffnvcodec --enable-nvenc --enable-nvdec --enable-cuvid \
  > "$work/configure.log" || { tail -40 "$work/ffmpeg/ffbuild/config.log" >&2; exit 1; })
make -C "$work/ffmpeg" -j"$jobs" >/dev/null
make -C "$work/ffmpeg" install >/dev/null

mkdir -p "$out/lib" "$out/bin"
cp -P "$work/install/lib/"*.so* "$out/lib/"
cp "$work/install/bin/ffmpeg" "$work/install/bin/ffprobe" "$out/bin/"
cp "$ffmpeg/COPYING.LGPLv2.1" "$root/artifacts/native/LICENSE.ffmpeg.txt"
# The libraries find each other beside themselves, and the tools find them in ../lib.
for so in "$out/lib/"*.so.*; do [[ -L "$so" ]] || patchelf --set-rpath '$ORIGIN' "$so"; done
patchelf --set-rpath '$ORIGIN/../lib' "$out/bin/ffmpeg" "$out/bin/ffprobe"

# What the bindings and StreamTransport rely on is compiled in, and the libraries need no newer glibc
# than the image's.
# libdrm and libva, which the system provides where the libraries run, come from the build here.
LD_LIBRARY_PATH="$prefix/lib" "$root/eng/check-ffmpeg.sh" "$out"
echo "$rid -> $out"
