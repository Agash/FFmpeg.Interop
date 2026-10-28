#!/usr/bin/env bash
# Installs what eng/build-ffmpeg.sh needs on AlmaLinux 8, the image the Linux libraries are built in:
# glibc 2.28, so they load on distributions and SBC images far older than the build machine. The CI job
# runs it in that image; locally:
#
#   docker run --rm -v "$PWD:/src" -w /src almalinux:8 bash -c \
#     'eng/linux-build-image.sh && source /opt/rh/gcc-toolset-13/enable && eng/build-ffmpeg.sh'
set -euo pipefail
dnf install -y -q dnf-plugins-core epel-release
dnf config-manager --set-enabled powertools
# GCC 13 from the toolset, still linking against the system's glibc 2.28; meson needs a newer Python
# than the image's 3.6.
dnf install -y -q gcc-toolset-13-gcc gcc-toolset-13-gcc-c++ make cmake git tar xz bzip2 nasm patchelf perl-core \
  pkgconf-pkg-config python3.11 python3.11-pip binutils diffutils which autoconf automake libtool
python3.11 -m pip install -q meson ninja
