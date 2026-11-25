#!/usr/bin/env bash
set -euo pipefail
root="$(realpath "$1")"
output="$2"
prefix="$root/prefix/bin"
mkdir -p "$output/player-notices"
native="$(find "$prefix" -maxdepth 1 -iname '*mpv*.dll')"
[[ -f "$native" ]]
declare -A seen
visit() {
  local file="$1" name="$2" dll key binary package license
  key="$(echo "$name" | tr '[:upper:]' '[:lower:]')"
  if [[ $key == vulkan-1.dll || $key == msys-2.0.dll ]]; then
    echo "Unexpected runtime dependency: $name" >&2
    exit 1
  fi
  if [[ -n "${seen[$key]+present}" ]]; then return; fi
  seen["$key"]=1
  cp "$file" "$output/$name"
  if [[ "$file" == /ucrt64/bin/* ]]; then
    package="$(pacman -Qqo "$file")"
    mkdir -p "$output/player-notices/$package"
    while read -r license; do
      if [[ -f "$license" ]]; then cp "$license" "$output/player-notices/$package/"; fi
    done < <(pacman -Qlq "$package" | grep '/share/licenses/' || true)
  fi
  while read -r dll; do
    binary="$(find "$prefix" /ucrt64/bin -maxdepth 1 -iname "$dll" -print -quit)"
    if [[ -n "$binary" ]]; then
      visit "$binary" "$dll"
    elif [[ "$dll" == [Aa][Pp][Ii]-[Mm][Ss]-* || "$dll" == [Ee][Xx][Tt]-[Mm][Ss]-* || -f "/c/Windows/System32/$dll" ]]; then
      continue
    else
      echo "Unresolved import in $name: $dll" >&2
      exit 1
    fi
  done < <(objdump -p "$file" | awk '/DLL Name:/ {print $3}' | tr -d '\r')
}
visit "$native" libmpv-2.dll
(
  cd "$output"
  sha256sum -- *.dll > SHA256SUMS.txt
)
cp "$output/SHA256SUMS.txt" "$output/player-notices/"
pacman -Q > "$output/player-notices/toolchain-packages.txt"
(
  cd "$root"
  sha256sum ffmpeg.tar.gz mpv.tar.gz libplacebo.tar.gz
) > "$output/player-notices/source-archives.txt"
cp "$root"/mpv-*/LICENSE.LGPL "$output/player-notices/mpv-LICENSE.LGPL"
cp "$root"/FFmpeg-*/COPYING.LGPLv3 "$output/player-notices/ffmpeg-COPYING.LGPLv3"
cp "$root"/libplacebo-*/LICENSE "$output/player-notices/libplacebo-LICENSE"
