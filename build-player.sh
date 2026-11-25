#!/usr/bin/env bash
set -euo pipefail

# Run in MSYS2 UCRT64 with a space-free build path.
root="$(realpath "$1")"
prefix="$root/prefix"
export PKG_CONFIG_PATH="$(cygpath -m "$prefix/lib/pkgconfig")"
export PATH="$prefix/bin:$PATH"

sources=(
  "mpv:mpv-player/mpv:d075ec2a288463a3a47a8df27b551369d8c1b57a:b828ac8824ac973c2df0ee4f05a8b3206a9480ccea3fe4fe30be6543b19983b2"
  "ffmpeg:FFmpeg/FFmpeg:49a3b1f259e1c34033bf6cca4091a4cb07ebf90d:c7e29021a7871036bfa9ed445bb8d8845a90deead70a6a523c5bde0f14d46cdb"
  "libplacebo:haasn/libplacebo:c2cf6e72daee00cbb8ef089059a5d12c06018cbd:9c498bbae489e7bc294bd4d6b6c0c06be4ff02ede39ae4335e03a0cbfc535b8f"
)
mkdir -p "$prefix"
for spec in "${sources[@]}"; do
  IFS=: read -r name repo revision digest <<< "$spec"
  if [[ ! -f "$root/$name.tar.gz" ]]; then
    curl -fLsS --retry 2 "https://codeload.github.com/$repo/tar.gz/$revision" -o "$root/$name.tar.gz"
  fi
  printf '%s  %s\n' "$digest" "$root/$name.tar.gz" | sha256sum -c -
  tar -xf "$root/$name.tar.gz" --exclude='*/AGENTS.md' -C "$root"
done

if [[ ! -f "$prefix/lib/pkgconfig/libavcodec.pc" ]]; then
  mkdir -p "$root/ffmpeg-build"
  cd "$root/ffmpeg-build"
  "$root/ffmpeg-49a3b1f259e1c34033bf6cca4091a4cb07ebf90d/configure" \
    --prefix="$(cygpath -m "$prefix")" --target-os=mingw32 --arch=x86_64 \
    --disable-autodetect --disable-gpl --disable-nonfree --enable-version3 \
    --disable-vulkan --disable-libplacebo --enable-d3d11va \
    --enable-shared --disable-static --disable-programs --disable-doc \
    --enable-libdav1d --enable-libjxl --enable-libopus --enable-libass \
    --enable-libzimg --enable-gnutls --enable-zlib --enable-bzlib
  make -j8
  make install
fi

placebo="$root/libplacebo-c2cf6e72daee00cbb8ef089059a5d12c06018cbd"
meson setup "$root/placebo-build" "$placebo" --prefix="$(cygpath -m "$prefix")" --buildtype=release \
  -Dvulkan=disabled -Dopengl=disabled -Dd3d11=enabled -Dshaderc=enabled -Dglslang=disabled -Ddemos=false
meson compile -C "$root/placebo-build" -j 8
meson install -C "$root/placebo-build"

mpv="$root/mpv-d075ec2a288463a3a47a8df27b551369d8c1b57a"
meson setup "$root/mpv-build" "$mpv" --prefix="$(cygpath -m "$prefix")" --buildtype=release \
  -Dgpl=false -Dlibmpv=true -Dcplayer=false -Dvulkan=disabled -Dd3d11=enabled -Dd3d-hwaccel=enabled \
  -Dlua=luajit -Dlibarchive=enabled
meson compile -C "$root/mpv-build" -j 8
meson install -C "$root/mpv-build"
