#!/usr/bin/env bash
# Installs what eng/build-ffmpeg.sh needs on Ubuntu 24.04, the image the Linux libraries are built in:
# glibc 2.39 and libva 2.20, which set the oldest systems they load on. The CI job runs it in that image;
# locally:
#
#   docker run --rm -v "$PWD:/src" -w /src ubuntu:24.04 bash -c \
#     'eng/linux-build-image.sh && eng/build-ffmpeg.sh'
set -euo pipefail
export DEBIAN_FRONTEND=noninteractive
apt-get update -qq
apt-get install -y -qq --no-install-recommends ca-certificates curl gcc-14 g++-14 make cmake git tar xz-utils \
  bzip2 nasm patchelf perl pkg-config python3 python3-venv binutils diffutils autoconf automake libtool
update-alternatives --install /usr/bin/cc cc /usr/bin/gcc-14 100
update-alternatives --install /usr/bin/c++ c++ /usr/bin/g++-14 100
update-alternatives --install /usr/bin/gcc gcc /usr/bin/gcc-14 100
update-alternatives --install /usr/bin/g++ g++ /usr/bin/g++-14 100
python3 -m venv /opt/build-tools
/opt/build-tools/bin/pip install -q meson ninja
ln -sf /opt/build-tools/bin/meson /opt/build-tools/bin/ninja /usr/local/bin/
