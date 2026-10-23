#!/usr/bin/env bash
# Сервер «только Telegram» для ByeDPI PC: sing-box + VLESS Reality.
# Запуск на чистом VPS (Debian/Ubuntu) от root:
#   curl -fsSL https://raw.githubusercontent.com/anomal3/ByeDPI-PC/main/docs/server/install.sh | bash
# или скачайте файл и выполните: bash install.sh [порт] [сайт-маскировка]
set -euo pipefail

PORT="${1:-443}"
MASK="${2:-www.microsoft.com}"   # сайт, под который маскируется сервер (Reality handshake)
DIR=/etc/sing-box
CONF=$DIR/config.json
STATE=$DIR/byedpi-telegram.env

[ "$(id -u)" = 0 ] || { echo "Запустите от root (sudo -i)"; exit 1; }

echo "== Устанавливаю sing-box и jq"
if ! command -v sing-box >/dev/null; then
  curl -fsSL https://sing-box.app/install.sh | sh
fi
command -v jq >/dev/null || { apt-get update -qq && apt-get install -y -qq jq; }

echo "== Генерирую ключи Reality"
KEYS=$(sing-box generate reality-keypair)
PRIVATE=$(echo "$KEYS" | awk '/PrivateKey/ {print $2}')
PUBLIC=$(echo "$KEYS" | awk '/PublicKey/ {print $2}')
SHORT_ID=$(sing-box generate rand 8 --hex)
IP=$(curl -fsS4 https://api.ipify.org || hostname -I | awk '{print $1}')

mkdir -p $DIR
cat > $STATE <<EOF
PORT=$PORT
MASK=$MASK
PUBLIC=$PUBLIC
SHORT_ID=$SHORT_ID
IP=$IP
EOF

# Адреса и домены Telegram — всё остальное сервер отклоняет, ключ нельзя использовать для других сайтов.
cat > $CONF <<EOF
{
  "log": { "level": "warn", "timestamp": true },
  "dns": { "servers": [ { "tag": "local", "type": "local" } ] },
  "inbounds": [
    {
      "type": "vless",
      "tag": "vless-in",
      "listen": "::",
      "listen_port": $PORT,
      "users": [],
      "tls": {
        "enabled": true,
        "server_name": "$MASK",
        "reality": {
          "enabled": true,
          "handshake": { "server": "$MASK", "server_port": 443 },
          "private_key": "$PRIVATE",
          "short_id": [ "$SHORT_ID" ]
        }
      }
    }
  ],
  "outbounds": [ { "type": "direct", "tag": "direct" } ],
  "route": {
    "rules": [
      { "action": "sniff" },
      {
        "domain_suffix": [
          "telegram.org", "t.me", "telegram.me", "telegram.dog", "telesco.pe", "telegra.ph",
          "tdesktop.com", "cdn-telegram.org", "telegram-cdn.org", "tg.dev"
        ],
        "outbound": "direct"
      },
      {
        "ip_cidr": [
          "91.105.192.0/23", "91.108.4.0/22", "91.108.8.0/22", "91.108.12.0/22", "91.108.16.0/22",
          "91.108.20.0/22", "91.108.56.0/22", "95.161.64.0/20", "149.154.160.0/20", "185.76.151.0/24",
          "2001:67c:4e8::/48", "2001:b28:f23c::/47", "2001:b28:f23f::/48", "2a0a:f280::/32"
        ],
        "outbound": "direct"
      },
      { "inbound": [ "vless-in" ], "action": "reject" }
    ],
    "final": "direct",
    "default_domain_resolver": "local"
  }
}
EOF

echo "== Открываю порт $PORT"
if command -v ufw >/dev/null && ufw status | grep -q active; then ufw allow "$PORT"/tcp; fi

cp "$(dirname "$0")/add-user.sh" /usr/local/bin/telegram-add-user 2>/dev/null || \
  curl -fsSL https://raw.githubusercontent.com/anomal3/ByeDPI-PC/main/docs/server/add-user.sh -o /usr/local/bin/telegram-add-user
chmod +x /usr/local/bin/telegram-add-user

systemctl enable sing-box >/dev/null 2>&1 || true
echo "== Готово. Добавляю первого пользователя:"
telegram-add-user "$(hostname)-1"
echo
echo "Новые ключи для друзей:  telegram-add-user Имя"
echo "Отозвать ключ:           telegram-add-user --remove Имя"
