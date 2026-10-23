#!/usr/bin/env bash
# Скачивает движки в engine/<rid>/ для Linux и macOS.
#   byedpi (ciadpi) — https://github.com/hufrea/byedpi (MIT). Для macOS собирается из исходников (нужны make и clang).
#   sing-box        — https://github.com/SagerNet/sing-box (GPL-3.0-or-later)
# Запуск: scripts/fetch-engines.sh [linux-x64|linux-arm64|osx-arm64|osx-x64]
set -euo pipefail

BYEDPI_VERSION=0.17.3
SINGBOX_VERSION=1.14.3

rid="${1:-}"
if [[ -z "$rid" ]]; then
  case "$(uname -s)-$(uname -m)" in
    Linux-x86_64) rid=linux-x64 ;;
    Linux-aarch64) rid=linux-arm64 ;;
    Darwin-arm64) rid=osx-arm64 ;;
    Darwin-x86_64) rid=osx-x64 ;;
    *) echo "Не знаю платформу $(uname -s)-$(uname -m), укажите rid явно" >&2; exit 1 ;;
  esac
fi

root="$(cd "$(dirname "$0")/.." && pwd)"
dest="$root/engine/$rid"
tmp="$(mktemp -d)"
trap 'rm -rf "$tmp"' EXIT
mkdir -p "$dest/LICENSES"

sha256() { if command -v sha256sum >/dev/null; then sha256sum "$1" | cut -c1-64; else shasum -a 256 "$1" | cut -c1-64; fi; }

fetch() { # url file sha
  echo "Скачиваю $1"
  curl -fsSL -o "$2" "$1"
  local actual; actual="$(sha256 "$2")"
  if [[ "$actual" != "$3" ]]; then echo "SHA256 не совпадает для $1: $actual" >&2; exit 1; fi
}

case "$rid" in
  linux-x64)
    fetch "https://github.com/hufrea/byedpi/releases/download/v$BYEDPI_VERSION/byedpi-17.3-x86_64.tar.gz" "$tmp/b.tgz" 98f73c32eacb571ebd88d790f6376ed9e70f02d44c1fe862472ecea75cd7117d
    tar -xzf "$tmp/b.tgz" -C "$tmp" && install -m 755 "$tmp/ciadpi-x86_64" "$dest/ciadpi"
    fetch "https://github.com/SagerNet/sing-box/releases/download/v$SINGBOX_VERSION/sing-box-$SINGBOX_VERSION-linux-amd64.tar.gz" "$tmp/s.tgz" e2bdf179c15a3652955dc44867e33ca0965c9c9b91b34267dcc5a5d639a5feee
    ;;
  linux-arm64)
    fetch "https://github.com/hufrea/byedpi/releases/download/v$BYEDPI_VERSION/byedpi-17.3-aarch64.tar.gz" "$tmp/b.tgz" d5806504e159e8119ede5af1164a609251b0520cd4483a7db611b6d238b9404b
    tar -xzf "$tmp/b.tgz" -C "$tmp" && install -m 755 "$(find "$tmp" -name 'ciadpi*' -type f | head -1)" "$dest/ciadpi"
    fetch "https://github.com/SagerNet/sing-box/releases/download/v$SINGBOX_VERSION/sing-box-$SINGBOX_VERSION-linux-arm64.tar.gz" "$tmp/s.tgz" 29dae24d76bea3bc62d07dd3e60cfc12003de7debbd6b768df9be9c68360174e
    ;;
  osx-arm64|osx-x64)
    # Готовых сборок byedpi под macOS нет — собираем из исходников релиза.
    fetch "https://github.com/hufrea/byedpi/archive/refs/tags/v$BYEDPI_VERSION.tar.gz" "$tmp/src.tgz" 0a9cb8585554c68c3e2be88c33c9bf6f99f8e8c7f54b362285adab99e262566c
    tar -xzf "$tmp/src.tgz" -C "$tmp"
    make -C "$tmp/byedpi-$BYEDPI_VERSION" CC=clang
    install -m 755 "$tmp/byedpi-$BYEDPI_VERSION/ciadpi" "$dest/ciadpi"
    if [[ "$rid" == osx-arm64 ]]; then
      fetch "https://github.com/SagerNet/sing-box/releases/download/v$SINGBOX_VERSION/sing-box-$SINGBOX_VERSION-darwin-arm64.tar.gz" "$tmp/s.tgz" 0360ce634a04c26a6b4fd957517987bb4f6c8950f63435ce3c5af2fcae5860c5
    else
      fetch "https://github.com/SagerNet/sing-box/releases/download/v$SINGBOX_VERSION/sing-box-$SINGBOX_VERSION-darwin-amd64.tar.gz" "$tmp/s.tgz" 6d002b74d03b478547349cc10af89e007868596e5aa931816d45da9f544715a9
    fi
    ;;
  *) echo "Неизвестный rid: $rid" >&2; exit 1 ;;
esac

tar -xzf "$tmp/s.tgz" -C "$tmp"
install -m 755 "$(find "$tmp" -name sing-box -type f | head -1)" "$dest/sing-box"
cp "$(find "$tmp" -path '*sing-box*' -name LICENSE | head -1)" "$dest/LICENSES/sing-box-GPL-3.0.txt"
curl -fsSL -o "$dest/LICENSES/byedpi-MIT.txt" https://raw.githubusercontent.com/hufrea/byedpi/main/LICENSE
echo "Готово: $dest"
ls -l "$dest"
