#!/usr/bin/env bash
# Личные ключи для сервера «только Telegram».
#   telegram-add-user Имя            — создать ключ и показать ссылку vless:// и код для ByeDPI PC
#   telegram-add-user --remove Имя   — отозвать ключ
#   telegram-add-user --list         — список пользователей
set -euo pipefail
CONF=/etc/sing-box/config.json
STATE=/etc/sing-box/byedpi-telegram.env
# shellcheck disable=SC1090
. "$STATE"

case "${1:-}" in
  --list)
    jq -r '.inbounds[0].users[].name' "$CONF"
    exit 0 ;;
  --remove)
    NAME="${2:?Укажите имя}"
    jq --arg n "$NAME" '.inbounds[0].users |= map(select(.name != $n))' "$CONF" > "$CONF.tmp" && mv "$CONF.tmp" "$CONF"
    sing-box check -c "$CONF" && systemctl restart sing-box
    echo "Ключ «$NAME» отозван."
    exit 0 ;;
  "" )
    echo "Использование: telegram-add-user Имя"; exit 1 ;;
esac

NAME="$1"
if jq -e --arg n "$NAME" '.inbounds[0].users[] | select(.name == $n)' "$CONF" >/dev/null; then
  UUID=$(jq -r --arg n "$NAME" '.inbounds[0].users[] | select(.name == $n) | .uuid' "$CONF")
else
  UUID=$(sing-box generate uuid)
  jq --arg n "$NAME" --arg u "$UUID" '.inbounds[0].users += [{"name": $n, "uuid": $u, "flow": "xtls-rprx-vision"}]' "$CONF" > "$CONF.tmp" && mv "$CONF.tmp" "$CONF"
  sing-box check -c "$CONF"
  systemctl restart sing-box
fi

HOST="$IP"
case "$HOST" in *:*) HOST="[$HOST]" ;; esac
LINK="vless://$UUID@$HOST:$PORT?encryption=none&flow=xtls-rprx-vision&security=reality&sni=$MASK&fp=chrome&pbk=$PUBLIC&sid=$SHORT_ID&type=tcp#Telegram-$NAME"

# Код для ByeDPI PC: ключ + правило «Telegram → внешний прокси».
JSON=$(jq -cn --arg n "Telegram для $NAME" --arg l "$LINK" '{
  format: "byedpi-pc-share", version: 1, name: $n,
  comment: "Личный ключ: не передавайте его другим.",
  rules: [ { Id: "preset.telegram", Name: "Telegram", Enabled: true, Route: "Upstream", IsPreset: true,
             Processes: [], Domains: [], IpCidrs: [] } ],
  upstream: $l }')
CODE="bdpi:$(printf '%s' "$JSON" | gzip -9 | base64 -w0 | tr '+/' '-_' | tr -d '=')"
printf '%s\n' "$JSON" > "/root/telegram-$NAME.bdpi"

echo "Пользователь: $NAME"
echo
echo "Ссылка (для любых клиентов VLESS):"
echo "$LINK"
echo
echo "Код для ByeDPI PC (Настройки → Обмен настройками → Импорт → Вставить код):"
echo "$CODE"
echo
echo "Файл доступа: /root/telegram-$NAME.bdpi (отправьте его — его можно просто перетащить в окно программы)"
