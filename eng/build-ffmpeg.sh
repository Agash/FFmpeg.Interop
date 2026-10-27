#!/usr/bin/env bash
# Builds FFmpeg (the release generate/inputs.json pins, the one the bindings are generated from) and the
# libraries it uses, for one runtime, into artifacts/native/<rid>: the shared libraries the package
# carries (lib/ on Linux, bin/ on Windows) and the ffmpeg and ffprobe the tests compare against.
#
#   eng/build-ffmpeg.sh [linux-x64|linux-arm64|win-x64|win-arm64]    (defaults to this Linux machine's)
#
# Everything is built from pinned, SHA-256-checked source (eng/native-inputs.json), unmodified. The
# libraries FFmpeg uses are linked in statically, except libva and libdrm on Linux, whose drivers expect
# the system's own copies; NVIDIA, AMD, Intel and Vulkan runtimes are opened at run time. The result is
# LGPL 2.1: no GPL or non-free component is enabled.
#
# Linux builds are meant for an old glibc (the CI builds in AlmaLinux 8, glibc 2.28, so the libraries
# load on distributions and SBC images far older than the runner) with current kernel interfaces: the
# DMA-BUF header comes from Linux 6.12, so FFmpeg's DMA-BUF synchronisation is compiled in. Windows
# builds are cross-compiled on Linux with llvm-mingw against the UCRT.
#
# Needs a C/C++ toolchain, nasm, cmake, meson, ninja, make, pkg-config, python3, curl and git, and
# patchelf for Linux.
set -Eeuo pipefail
shopt -s inherit_errexit

root="$(cd "$(dirname "$0")/.." && pwd)"
case "${1:-}" in
  "") case "$(uname -m)" in x86_64) rid=linux-x64 ;; aarch64) rid=linux-arm64 ;; *) echo "No runtime for $(uname -m)." >&2; exit 1 ;; esac ;;
  linux-x64|linux-arm64|win-x64|win-arm64) rid=$1 ;;
  *) echo "Usage: $0 [linux-x64|linux-arm64|win-x64|win-arm64]" >&2; exit 2 ;;
esac
os="${rid%%-*}"
case "${rid##*-}" in x64) arch=x86_64 ;; arm64) arch=aarch64 ;; esac

work="$root/artifacts/obj/ffmpeg/$rid"
sources="$root/artifacts/obj/ffmpeg/sources"
prefix="$work/prefix"
host="$root/artifacts/obj/ffmpeg/host"
out="$root/artifacts/native/$rid"
jobs="$(nproc)"
mkdir -p "$work" "$sources" "$prefix/include" "$prefix/lib" "$host/bin"
# Every step writes to one log; a failure shows its end.
log="$work/build.log"
: >"$log"
trap 'echo "Building $rid failed; the end of $log:" >&2; tail -60 "$log" >&2' ERR
export PATH="$host/bin:$PATH"
export PKG_CONFIG_PATH="$prefix/lib/pkgconfig:$prefix/lib64/pkgconfig:$prefix/share/pkgconfig"
export PKG_CONFIG_LIBDIR="$PKG_CONFIG_PATH"

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

# glslang compiles FFmpeg's Vulkan shaders at build time, on this machine; it is not shipped.
if [[ ! -x "$host/bin/glslang" ]]; then
  env -u CC -u CXX cmake -S "$(fetch "$native" glslang)" -B "$host/glslang" -G Ninja -DCMAKE_BUILD_TYPE=Release \
    -DCMAKE_INSTALL_PREFIX="$host" -DENABLE_OPT=OFF -DGLSLANG_TESTS=OFF -DENABLE_SPVREMAPPER=OFF \
    -DBUILD_SHARED_LIBS=OFF >>"$log" 2>&1
  cmake --build "$host/glslang" --target install >>"$log" 2>&1
fi

# The target's toolchain, and how meson and CMake are told about it.
meson_cross=()
cmake_cross=()
if [[ $os == win ]]; then
  toolchain="$sources/llvm-mingw-$(input "$native" llvmMingw version)"
  if [[ ! -d "$toolchain" ]]; then
    curl -sfL --retry 4 -o "$sources/llvm-mingw.archive" "$(input "$native" llvmMingw url)"
    echo "$(input "$native" llvmMingw sha256)  $sources/llvm-mingw.archive" | sha256sum -c --quiet -
    mkdir -p "$toolchain.tmp" && tar -xf "$sources/llvm-mingw.archive" -C "$toolchain.tmp" --strip-components=1
    rm "$sources/llvm-mingw.archive" && mv "$toolchain.tmp" "$toolchain"
  fi
  triple="$arch-w64-mingw32"
  export PATH="$toolchain/bin:$PATH"
  # The C++ runtime, its unwinder and winpthreads are linked in: without their import libraries every
  # library that asks for them, a dependency's pkg-config included, gets the static archive, and the DLLs
  # need only Windows' own.
  rm -f "$toolchain/$triple/lib/"{libc++,libunwind,libwinpthread,libpthread}.dll.a
  export CC="$triple-clang" CXX="$triple-clang++" AR=llvm-ar RANLIB=llvm-ranlib NM=llvm-nm STRIP=llvm-strip
  cpu=$([[ $arch == x86_64 ]] && echo x86_64 || echo aarch64)
  cat > "$work/cross.ini" <<EOF
[binaries]
c = '$triple-clang'
cpp = '$triple-clang++'
ar = 'llvm-ar'
strip = 'llvm-strip'
windres = '$triple-windres'
pkg-config = 'pkg-config'
nasm = 'nasm'
[host_machine]
system = 'windows'
cpu_family = '$cpu'
cpu = '$cpu'
endian = 'little'
EOF
  cat > "$work/cross.cmake" <<EOF
set(CMAKE_SYSTEM_NAME Windows)
set(CMAKE_SYSTEM_PROCESSOR $cpu)
set(CMAKE_C_COMPILER $triple-clang)
set(CMAKE_CXX_COMPILER $triple-clang++)
set(CMAKE_RC_COMPILER $triple-windres)
set(CMAKE_FIND_ROOT_PATH $prefix)
set(CMAKE_FIND_ROOT_PATH_MODE_PROGRAM NEVER)
set(CMAKE_FIND_ROOT_PATH_MODE_LIBRARY ONLY)
set(CMAKE_FIND_ROOT_PATH_MODE_INCLUDE ONLY)
EOF
  meson_cross=(--cross-file "$work/cross.ini")
  cmake_cross=(-DCMAKE_TOOLCHAIN_FILE="$work/cross.cmake")
  export CFLAGS="-O2" CXXFLAGS="-O2"
else
  export CFLAGS="-O2 -fPIC" CXXFLAGS="-O2 -fPIC"
fi

meson_build() { # <name> <pins> [options...]: a meson project into the prefix
  local name=$1 pins=$2; shift 2
  [[ -f "$work/$name.done" ]] && return
  meson setup --reconfigure "$work/$name" "$(fetch "$pins" "$name")" "${meson_cross[@]}" --prefix="$prefix" \
    --libdir=lib --buildtype=release "$@" >>"$log" 2>&1
  ninja -C "$work/$name" install >>"$log" 2>&1
  touch "$work/$name.done"
}
cmake_build() { # <name> <pins> [options...]: a CMake project into the prefix
  local name=$1 pins=$2; shift 2
  [[ -f "$work/$name.done" ]] && return
  cmake -S "$(fetch "$pins" "$name")" -B "$work/$name" -G Ninja "${cmake_cross[@]}" -DCMAKE_BUILD_TYPE=Release \
    -DCMAKE_INSTALL_PREFIX="$prefix" -DCMAKE_INSTALL_LIBDIR=lib -DCMAKE_POSITION_INDEPENDENT_CODE=ON "$@" >>"$log" 2>&1
  cmake --build "$work/$name" --target install >>"$log" 2>&1
  touch "$work/$name.done"
}

# Headers only: NVIDIA's codec interface, AMF's, Vulkan's.
make -C "$(fetch "$generator" nvcodec)" PREFIX="$prefix" install >>"$log" 2>&1
amf="$sources/amf-$(input "$native" amf commit)"
if [[ ! -d "$amf" ]]; then
  git clone -q --filter=blob:none --sparse --no-checkout "$(input "$native" amf git)" "$amf.tmp"
  git -C "$amf.tmp" sparse-checkout set "$(input "$native" amf path)"
  git -C "$amf.tmp" checkout -q "$(input "$native" amf commit)"
  mv "$amf.tmp" "$amf"
fi
rm -rf "$prefix/include/AMF" && cp -r "$amf/$(input "$native" amf path)" "$prefix/include/AMF"
cp -r "$(fetch "$generator" vulkan)/include/." "$prefix/include/"

if [[ $os == linux ]]; then
  # Linux's DMA-BUF uAPI, newer than the build image's kernel headers.
  dmabuf="$sources/dma-buf.h"
  if [[ ! -f "$dmabuf" ]]; then
    curl -sfL --retry 4 -o "$dmabuf" "$(input "$native" linuxUapiDmaBuf url)"
    echo "$(input "$native" linuxUapiDmaBuf sha256)  $dmabuf" | sha256sum -c --quiet -
  fi
  mkdir -p "$prefix/include/linux"
  cp "$dmabuf" "$prefix/include/linux/dma-buf.h"

  # libdrm and libva are linked against shared: at run time the system's copies serve, with its drivers.
  meson_build libdrm "$native" -Ddefault_library=shared -Dtests=false -Dman-pages=disabled -Dvalgrind=disabled \
    -Dcairo-tests=disabled -Damdgpu=enabled -Dradeon=disabled -Dnouveau=disabled -Dintel=disabled -Dvmwgfx=disabled
  meson_build libva "$generator" -Ddefault_library=shared -Denable_docs=false -Dwith_x11=no -Dwith_glx=no \
    -Dwith_wayland=no -Ddisable_drm=false
fi

# Rockchip's MPP, the hardware codecs of RK3588-class boards, on linux-arm64 only: FFmpeg counts it among
# its LGPL version 3 libraries, so it makes this one runtime's build LGPL 3 and leaves the others 2.1.
if [[ $rid == linux-arm64 ]]; then
  cmake_build mpp "$native" -DBUILD_SHARED_LIBS=OFF -DBUILD_TEST=OFF -DRKPLATFORM=ON -DHAVE_DRM=ON
fi

# Codec libraries, static, inside FFmpeg's libraries.
meson_build dav1d "$native" -Ddefault_library=static -Denable_tools=false -Denable_tests=false
# OpenH264 with its own Makefile, the build its project maintains: its meson build leaves the AVX2
# routines out on Windows, which halves the encoder there.
if [[ ! -f "$work/openh264.done" ]]; then
  openh264="$(fetch "$native" openh264)"
  rm -rf "$work/openh264" && cp -r "$openh264" "$work/openh264"
  case "$rid" in
    win-*) h264os=(OS=mingw_nt ARCH="$arch" CC="$CC" CXX="$CXX" AR="$AR") ;;
    linux-*) h264os=(OS=linux ARCH="$([[ $arch == x86_64 ]] && echo x86_64 || echo arm64)") ;;
  esac
  make -C "$work/openh264" -j"$jobs" "${h264os[@]}" PREFIX="$prefix" LIBDIR_NAME=lib BUILDTYPE=Release \
    DEBUGSYMBOLS=False install-static >>"$log" 2>&1
  touch "$work/openh264.done"
fi
# Intel's QSV exists only on x64. libvpl defines wcscpy_s as a macro for compilers other than MSVC, which
# breaks mingw-w64's windows.h when that comes after it (intel/libvpl#198); windows.h included first takes
# the inline functions before the macro exists, without touching libvpl's source.
if [[ $arch == x86_64 ]]; then
  vplflags=()
  [[ $os == win ]] && vplflags=(-DCMAKE_CXX_FLAGS="$CXXFLAGS -include windows.h")
  cmake_build libvpl "$native" -DBUILD_SHARED_LIBS=OFF -DBUILD_TOOLS=OFF -DBUILD_EXAMPLES=OFF -DBUILD_TESTS=OFF \
    -DINSTALL_EXAMPLES=OFF "${vplflags[@]}"
fi
cmake_build svtav1 "$native" -DBUILD_SHARED_LIBS=OFF -DBUILD_APPS=OFF -DBUILD_TESTING=OFF -DBUILD_DEC=OFF
# kvazaar's autotools build, which passes GNU as its MinGW alignment workaround only when the compiler is
# GCC; its CMake build passes it to clang too, whose assembler refuses it.
if [[ ! -f "$work/kvazaar.done" ]]; then
  kvazaar="$(fetch "$native" kvazaar)"
  rm -rf "$work/kvazaar" && cp -r "$kvazaar" "$work/kvazaar"
  (cd "$work/kvazaar" && ./autogen.sh >>"$log" 2>&1 && ./configure ${triple:+--host=$triple} --prefix="$prefix" \
    --libdir="$prefix/lib" --disable-shared --enable-static --with-pic >>"$log" 2>&1 && make -j"$jobs" >>"$log" 2>&1 \
    && make install >>"$log" 2>&1)
  touch "$work/kvazaar.done"
fi
if [[ ! -f "$work/libvpx.done" ]]; then
  vpx="$(fetch "$native" libvpx)"
  case "$rid" in
    win-x64) vpxtarget=(--target=x86_64-win64-gcc) ;;
    win-arm64) vpxtarget=(--target=arm64-win64-gcc) ;;
    *) vpxtarget=() ;;
  esac
  mkdir -p "$work/libvpx"
  (cd "$work/libvpx" && CROSS="${triple:+$triple-}" "$vpx/configure" "${vpxtarget[@]}" --prefix="$prefix" \
    --libdir="$prefix/lib" --enable-pic --enable-static --disable-shared --disable-examples --disable-tools \
    --disable-docs --disable-unit-tests --enable-vp9-highbitdepth >>"$log" 2>&1 && make -j"$jobs" >>"$log" 2>&1 \
    && make install >>"$log" 2>&1)
  touch "$work/libvpx.done"
fi

# FFmpeg: every feature named, nothing autodetected, so a missing dependency fails here instead of
# silently dropping a backend.
common=(
  --prefix="$work/install" --enable-shared --disable-static --enable-pic --disable-autodetect
  --disable-doc --disable-ffplay --disable-debug
  --pkg-config-flags=--static --extra-cflags="-I$prefix/include"
  --enable-libdav1d --enable-libsvtav1 --enable-libopenh264 --enable-libkvazaar --enable-libvpx
  --enable-vulkan --enable-amf --enable-ffnvcodec --enable-nvenc --enable-nvdec --enable-cuvid
)
if [[ $arch == x86_64 ]]; then
  common+=(--enable-libvpl)
fi
if [[ $rid == linux-arm64 ]]; then
  common+=(--enable-rkmpp --enable-version3)
fi
if [[ $os == win ]]; then
  platform=(
    --target-os=mingw32 --arch="$arch" --enable-cross-compile --cross-prefix="$triple-"
    --cc="$triple-clang" --cxx="$triple-clang++" --ar=llvm-ar --ranlib=llvm-ranlib --nm=llvm-nm --strip=llvm-strip
    --pkg-config=pkg-config --extra-ldflags="-L$prefix/lib" --extra-libs="-lc++ -lunwind" --extra-cflags=-DKVZ_STATIC_LIB
    --enable-w32threads --enable-d3d11va --enable-d3d12va --enable-dxva2 --enable-mediafoundation
  )
else
  # Hardened as distributions build it: checked buffer functions, stack protection, and relocations made
  # read-only after loading.
  platform=(
    --extra-cflags="-D_FORTIFY_SOURCE=2 -fstack-protector-strong -fstack-clash-protection"
    --extra-ldflags="-L$prefix/lib -Wl,-rpath-link,$prefix/lib -Wl,-z,relro,-z,now"
    --extra-libs="-lstdc++ -lm -lpthread -ldl"
    --enable-pthreads --enable-libdrm --enable-vaapi --enable-v4l2-m2m
  )
fi
ffmpeg="$(fetch "$generator" ffmpeg)"
rm -rf "$work/ffmpeg" "$work/install" "$out" && mkdir -p "$work/ffmpeg" "$out"
(cd "$work/ffmpeg" && "$ffmpeg/configure" "${common[@]}" "${platform[@]}" > "$work/configure.log") \
  || { tail -40 "$work/ffmpeg/ffbuild/config.log" >&2; exit 1; }
make -C "$work/ffmpeg" -j"$jobs" >>"$log" 2>&1
make -C "$work/ffmpeg" install >>"$log" 2>&1

cp "$ffmpeg/COPYING.LGPLv2.1" "$root/artifacts/native/LICENSE.ffmpeg.txt"
if [[ $os == win ]]; then
  # Windows finds DLLs beside the executable, so the libraries and tools share bin/.
  mkdir -p "$out/bin"
  cp "$work/install/bin/"*.dll "$work/install/bin/ffmpeg.exe" "$work/install/bin/ffprobe.exe" "$out/bin/"
else
  mkdir -p "$out/lib" "$out/bin"
  cp -P "$work/install/lib/"*.so* "$out/lib/"
  cp "$work/install/bin/ffmpeg" "$work/install/bin/ffprobe" "$out/bin/"
  # The libraries find each other beside themselves, and the tools find them in ../lib.
  for so in "$out/lib/"*.so.*; do [[ -L "$so" ]] || patchelf --set-rpath '$ORIGIN' "$so"; done
  patchelf --set-rpath '$ORIGIN/../lib' "$out/bin/ffmpeg" "$out/bin/ffprobe"
fi

# What the bindings and StreamTransport rely on is compiled in. libdrm and libva, which the system
# provides where the Linux libraries run, come from the build here.
LD_LIBRARY_PATH="$prefix/lib" "$root/eng/check-ffmpeg.sh" "$out" "$rid"
echo "$rid -> $out"
