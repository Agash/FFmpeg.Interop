#!/usr/bin/env bash
# Installs what eng/build-ffmpeg.sh needs to cross-compile the Windows libraries on Ubuntu 24.04; the
# toolchain itself, llvm-mingw, is pinned in eng/native-inputs.json and fetched by the build. Locally:
#
#   docker run --rm -v "$PWD:/src" -w /src ubuntu:24.04 bash -c \
#     'eng/windows-build-image.sh && eng/build-ffmpeg.sh win-x64'
set -euo pipefail
export DEBIAN_FRONTEND=noninteractive
apt-get update -qq
apt-get install -y -qq --no-install-recommends build-essential cmake git curl ca-certificates xz-utils nasm \
  pkg-config python3 python3-pip meson ninja-build binutils autoconf automake libtool >/dev/null
