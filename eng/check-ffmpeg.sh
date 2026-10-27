#!/usr/bin/env bash
# Checks an FFmpeg build (artifacts/native/<rid>) for what the bindings and their users rely on, so a
# dependency that went missing fails the build instead of shipping a library without a backend.
#
#   eng/check-ffmpeg.sh <artifacts/native/rid>
set -euo pipefail
dir="$1"
ffmpeg="$dir/bin/ffmpeg"
failed=0
need() { # <what> <list> <names...>: each name is in the list
  local what=$1 list=$2; shift 2
  for name in "$@"; do
    grep -qw -- "$name" <<<"$list" || { echo "missing $what: $name" >&2; failed=1; }
  done
}

# LGPL 2.1 or later: nothing GPL, non-free or version 3 is configured in.
grep -q "Lesser General Public" <<<"$("$ffmpeg" -hide_banner -L)" || { echo "not LGPL" >&2; failed=1; }
if grep -qE -- "--enable-(gpl|nonfree|version3)" <<<"$("$ffmpeg" -hide_banner -buildconf)"; then
  echo "configured with a GPL, non-free or version 3 component" >&2
  failed=1
fi

case "$(uname -s)" in
  Linux)
    need hwaccel "$("$ffmpeg" -hide_banner -hwaccels)" vaapi vulkan cuda drm qsv
    need encoder "$("$ffmpeg" -hide_banner -encoders)" \
      h264_vaapi hevc_vaapi av1_vaapi h264_vulkan hevc_vulkan av1_vulkan \
      h264_nvenc hevc_nvenc av1_nvenc h264_amf hevc_amf av1_amf h264_qsv hevc_qsv av1_qsv \
      libopenh264 libkvazaar libsvtav1 libvpx libvpx-vp9
    need decoder "$("$ffmpeg" -hide_banner -decoders)" libdav1d h264_cuvid hevc_cuvid av1_cuvid
    # Shaders compiled at build time: without a SPIR-V compiler these are left out.
    need filter "$("$ffmpeg" -hide_banner -filters)" scale_vulkan hwupload hwmap
    # VA-API opens render nodes through libva-drm; configure drops it quietly when its test link
    # fails, leaving a VA-API that finds no display.
    grep -q "NEEDED.*libva-drm.so.2" <<<"$(objdump -p "$dir"/lib/libavutil.so.*.*)" \
      || { echo "VA-API without DRM displays: libavutil does not use libva-drm" >&2; failed=1; }
    # FFmpeg's DMA-BUF synchronisation (DMA_BUF_IOCTL_EXPORT_SYNC_FILE), absent when built against
    # kernel headers older than Linux 6.0.
    grep -q "Failed to retrieve implicit DRM sync file" "$dir"/lib/libavutil.so.* \
      || { echo "missing DMA-BUF synchronisation in libavutil" >&2; failed=1; }
    # Nothing newer than the build image's glibc and libstdc++, so the libraries load where it does.
    glibc="$(ldd --version | head -1 | grep -oE '[0-9]+\.[0-9]+$')"
    for so in "$dir"/lib/*.so.*; do
      [[ -L "$so" ]] && continue
      newest="$(objdump -T "$so" | grep -oE 'GLIBC_[0-9.]+' | sort -uV | tail -1 | cut -d_ -f2)"
      if [[ -n "$newest" && "$(printf '%s\n%s\n' "$newest" "$glibc" | sort -V | tail -1)" != "$glibc" ]]; then
        echo "$(basename "$so") needs glibc $newest, newer than $glibc" >&2
        failed=1
      fi
    done
    ;;
esac

[[ $failed -eq 0 ]] && echo "FFmpeg build has everything it needs."
exit $failed
