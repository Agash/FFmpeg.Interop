#!/usr/bin/env bash
# Installs what eng/build-ffmpeg.sh needs on macOS, besides Xcode's clang: the build systems and nasm, from
# Homebrew. Locally:
#
#   eng/macos-build-image.sh && "$(brew --prefix)/bin/bash" eng/build-ffmpeg.sh osx-arm64
set -euo pipefail
# bash 5: the scripts use what macOS's own bash 3.2 lacks.
brew install --quiet bash meson ninja nasm autoconf automake libtool pkg-config cmake
