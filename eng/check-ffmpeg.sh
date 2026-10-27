#!/usr/bin/env bash
# Checks an FFmpeg build (artifacts/native/<rid>) for what the bindings and their users rely on, so a
# dependency that went missing fails the build instead of shipping a library without a backend.
#
#   eng/check-ffmpeg.sh <artifacts/native/rid> <rid>
#
# Linux builds are checked by running the tools they come with; Windows builds, made on Linux, by what
# their libraries contain. Output is read into variables before it is searched: a search that stops at
# its first match would cut the writer off, which pipefail turns into a failure.
set -euo pipefail
dir="$1"
rid="$2"
failed=0
need() { # <what> <list> <names...>: each name is in the list
  local what=$1 list=$2; shift 2
  for name in "$@"; do
    grep -qw -- "$name" <<<"$list" || { echo "missing $what: $name" >&2; failed=1; }
  done
}

# What every runtime has: the software codecs, PNG through zlib, and TLS for https and DTLS.
common() { # <encoders> <decoders> <protocols>
  need encoder "$1" libopenh264 libkvazaar libsvtav1 libvpx libvpx-vp9 libaom-av1 libopus libmp3lame png
  need decoder "$2" libdav1d libaom-av1 libopus png
  need protocol "$3" tls https dtls
}

case "$rid" in
  linux-*)
    ffmpeg="$dir/bin/ffmpeg"
    # LGPL 2.1 or later: nothing GPL, non-free or version 3 is configured in.
    grep -q "Lesser General Public" <<<"$("$ffmpeg" -hide_banner -L)" || { echo "not LGPL" >&2; failed=1; }
    # Version 3 only on linux-arm64, for Rockchip's MPP.
    banned="gpl|nonfree|version3"
    if [[ $rid == linux-arm64 ]]; then
      banned="gpl|nonfree"
    fi
    if grep -qE -- "--enable-($banned)" <<<"$("$ffmpeg" -hide_banner -buildconf)"; then
      echo "configured with a component the licence does not allow" >&2
      failed=1
    fi
    if [[ $rid == linux-arm64 ]]; then
      need encoder "$("$ffmpeg" -hide_banner -encoders)" h264_rkmpp hevc_rkmpp
      need decoder "$("$ffmpeg" -hide_banner -decoders)" h264_rkmpp hevc_rkmpp vp8_rkmpp vp9_rkmpp
    fi
    need hwaccel "$("$ffmpeg" -hide_banner -hwaccels)" vaapi vulkan cuda drm
    need encoder "$("$ffmpeg" -hide_banner -encoders)" \
      h264_vaapi hevc_vaapi av1_vaapi h264_vulkan hevc_vulkan av1_vulkan \
      h264_nvenc hevc_nvenc av1_nvenc h264_amf hevc_amf av1_amf \
      h264_v4l2m2m hevc_v4l2m2m
    common "$("$ffmpeg" -hide_banner -encoders)" "$("$ffmpeg" -hide_banner -decoders)"       "$("$ffmpeg" -hide_banner -protocols)"
    need device "$("$ffmpeg" -hide_banner -devices)" v4l2
    # Intel's QSV, on x64 only.
    if [[ $rid == *-x64 ]]; then
      need hwaccel "$("$ffmpeg" -hide_banner -hwaccels)" qsv
      need encoder "$("$ffmpeg" -hide_banner -encoders)" h264_qsv hevc_qsv av1_qsv
    fi
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
    # Only the system's libraries and FFmpeg's own: a dependency linked shared by mistake loads here,
    # from the build, and nowhere else.
    for so in "$dir"/lib/*.so.*; do
      [[ -L "$so" ]] && continue
      while read -r needed; do
        case "$needed" in
          libav*|libsw*|libva.so.2|libva-drm.so.2|libdrm.so.2|libc.so.6|libm.so.6|libpthread.so.0|libdl.so.2             |librt.so.1|libstdc++.so.6|libgcc_s.so.1|ld-linux*) ;;
          *) echo "$(basename "$so") needs $needed, which is not shipped" >&2; failed=1 ;;
        esac
      done <<<"$(objdump -p "$so" | sed -n 's/^ *NEEDED *//p')"
    done
    # Nothing newer than the build image's glibc, so the libraries load where it does.
    glibc="$(getconf GNU_LIBC_VERSION | cut -d' ' -f2)"
    for so in "$dir"/lib/*.so.*; do
      [[ -L "$so" ]] && continue
      symbols="$(objdump -T "$so")"
      newest="$(grep -oE 'GLIBC_[0-9.]+' <<<"$symbols" | sort -uV | tail -1 | cut -d_ -f2)"
      if [[ -n "$newest" && "$(printf '%s\n%s\n' "$newest" "$glibc" | sort -V | tail -1)" != "$glibc" ]]; then
        echo "$(basename "$so") needs glibc $newest, newer than $glibc" >&2
        failed=1
      fi
    done
    ;;
  osx-*)
    ffmpeg="$dir/bin/ffmpeg"
    grep -q "Lesser General Public" <<<"$("$ffmpeg" -hide_banner -L)" || { echo "not LGPL" >&2; failed=1; }
    if grep -qE -- "--enable-(gpl|nonfree|version3)" <<<"$("$ffmpeg" -hide_banner -buildconf)"; then
      echo "configured with a component the licence does not allow" >&2
      failed=1
    fi
    need hwaccel "$("$ffmpeg" -hide_banner -hwaccels)" videotoolbox
    need encoder "$("$ffmpeg" -hide_banner -encoders)" h264_videotoolbox hevc_videotoolbox aac_at
    common "$("$ffmpeg" -hide_banner -encoders)" "$("$ffmpeg" -hide_banner -decoders)"       "$("$ffmpeg" -hide_banner -protocols)"
    need device "$("$ffmpeg" -hide_banner -devices)" avfoundation audiotoolbox
    need filter "$("$ffmpeg" -hide_banner -filters)" scale_vt transpose_vt yadif_videotoolbox coreimage
    # Only the system's libraries and frameworks, and FFmpeg's own under @rpath.
    for dylib in "$dir"/lib/*.dylib; do
      if [[ -L "$dylib" ]]; then continue; fi
      while read -r linked; do
        case "$linked" in
          /System/Library/*|/usr/lib/*|@rpath/libav*|@rpath/libsw*) ;;
          *) echo "$(basename "$dylib") links $linked, which is not shipped" >&2; failed=1 ;;
        esac
      done <<<"$(otool -L "$dylib" | tail -n +2 | awk '{print $1}')"
    done
    ;;
  win-*)
    avcodec="$(echo "$dir"/bin/avcodec-*.dll)"
    avutil="$(echo "$dir"/bin/avutil-*.dll)"
    codecs="$(strings -n 6 "$avcodec")"
    need encoder "$codecs" h264_nvenc hevc_nvenc av1_nvenc h264_amf hevc_amf av1_amf \
      h264_mf hevc_mf h264_d3d12va hevc_d3d12va h264_vulkan hevc_vulkan av1_vulkan \
      libopenh264 libkvazaar libsvtav1 libvpx libdav1d libaom-av1 libopus libmp3lame
    need decoder "$codecs" libaom-av1 libopus png zlib
    need protocol "$(strings -n 4 "$(echo "$dir"/bin/avformat-*.dll)")" https dtls
    need device "$(strings -n 4 "$(echo "$dir"/bin/avdevice-*.dll)")" dshow gdigrab
    need hwaccel "$codecs" h264_d3d11va h264_d3d12va h264_dxva2 h264_vulkan av1_d3d12va
    need "device type" "$(strings -n 4 "$avutil")" d3d11va d3d12va dxva2 vulkan cuda amf
    if [[ $rid == *-x64 ]]; then
      need encoder "$codecs" h264_qsv hevc_qsv av1_qsv
    fi
    grep -qE "Lesser General Public|LGPL version 2.1 or later" <<<"$(strings -n 8 "$avutil")" \
      || { echo "not LGPL" >&2; failed=1; }
    # Only system DLLs and FFmpeg's own: the codec libraries and the C++ runtime are linked in.
    for dll in "$dir"/bin/*.dll; do
      # llvm-objdump, from the cross toolchain: the host's binutils read only x64 images.
      imports="$(llvm-objdump -p "$dll" | sed -n 's/^[[:space:]]*DLL Name: //p')"
      while read -r import; do
        case "${import,,}" in
          av*|sw*|api-ms-win-*|kernel32.dll|user32.dll|advapi32.dll|ole32.dll|oleaut32.dll|shell32.dll \
            |bcrypt.dll|ws2_32.dll|secur32.dll|crypt32.dll|mfplat.dll|mf.dll|mfuuid.dll|strmiids.dll \
            |gdi32.dll|psapi.dll|shlwapi.dll|vfw32.dll|cfgmgr32.dll|ucrtbase.dll|msvcrt.dll) ;;
          *) echo "$(basename "$dll") needs $import, which is not shipped" >&2; failed=1 ;;
        esac
      done <<<"$imports"
    done
    ;;
esac

[[ $failed -eq 0 ]] && echo "FFmpeg build has everything it needs."
exit $failed
