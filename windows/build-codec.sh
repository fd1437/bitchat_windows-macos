#!/bin/sh
set -eu
# Run on macOS with Homebrew mingw-w64, or a host with the same cross compiler.
# Source is included to permit rebuilding/replacing the LGPL codec independently.
windows_root=$(CDPATH= cd -- "$(dirname -- "$0")" && pwd)
codec_build="${1:-$windows_root/../../codec-build}"
mkdir -p "$codec_build"
tar -xf "$windows_root/vendor/media/source/ffmpeg-9.0.2.tar.xz" -C "$codec_build"
mkdir -p "$codec_build/windows"
cd "$codec_build/windows"
../ffmpeg-9.0.2/configure --target-os=mingw32 --arch=x86_64 \
  --cross-prefix=x86_64-w64-mingw32- --enable-cross-compile \
  --disable-everything --disable-doc --disable-autodetect --enable-ffmpeg \
  --disable-ffprobe --disable-ffplay --disable-network --enable-protocol=file,pipe \
  --enable-encoder=aac,pcm_s16le --enable-decoder=aac,pcm_s16le,pcm_f32le \
  --enable-muxer=adts,ipod,wav,s16le --enable-demuxer=aac,wav,s16le,f32le,mov \
  --enable-parser=aac --enable-filter=aresample,aformat,anull --enable-swresample \
  --enable-small --disable-debug --enable-static --disable-shared \
  --disable-x86asm --extra-ldflags=-static
make -j4
cp ffmpeg.exe "$windows_root/vendor/media/ffmpeg.exe"
