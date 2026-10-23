#!/usr/bin/env bash
# SSH-туннель для ByeDPI PC: когда у VPS открыт только порт SSH (NAT-VPS и т.п.).
# Создаёт учётки «только туннель» (без shell, без входа в систему) и команду выдачи ключей:
#   tunnel-add-user Имя          — доступ только к Telegram (для друзей)
#   tunnel-add-user Имя --full   — полный доступ (весь трафик правил «Внешний прокси»)
#   tunnel-add-user --remove Имя / --list
# Установка (от root): bash ssh-tunnel.sh [внешний-адрес] [внешний-порт-ssh]
set -euo pipefail
[ "$(id -u)" = 0 ] || { echo "Запустите от root"; exit 1; }

PUBLIC_HOST="${1:-$(curl -fsS4 --max-time 8 https://api.ipify.org)}"
PUBLIC_PORT="${2:-22}"
STATE=/etc/byedpi-tunnel.env

cat > "$STATE" <<EOF
PUBLIC_HOST=$PUBLIC_HOST
PUBLIC_PORT=$PUBLIC_PORT
EOF

getent group tunnel >/dev/null || groupadd --system tunnel        # все туннельные учётки
getent group tunnel-tg >/dev/null || groupadd --system tunnel-tg  # только Telegram

# sshd: туннельным учёткам — только проброс TCP, ни shell, ни TTY, ни агентов.
cat > /etc/ssh/sshd_config.d/90-byedpi-tunnel.conf <<'EOF'
Match Group tunnel
    PasswordAuthentication yes
    AllowTcpForwarding local
    PermitTTY no
    X11Forwarding no
    AllowAgentForwarding no
    AllowStreamLocalForwarding no
    PermitTunnel no
    GatewayPorts no
    ForceCommand /usr/sbin/nologin
EOF
sshd -t
systemctl reload ssh

# Файрвол: соединения, которые sshd открывает от имени учёток группы tunnel-tg, — только к Telegram.
cat > /usr/local/sbin/byedpi-tunnel-firewall <<'EOF'
#!/bin/sh
TG4="91.105.192.0/23 91.108.4.0/22 91.108.8.0/22 91.108.12.0/22 91.108.16.0/22 91.108.20.0/22 91.108.56.0/22 95.161.64.0/20 149.154.160.0/20 185.76.151.0/24"
TG6="2001:67c:4e8::/48 2001:b28:f23c::/47 2001:b28:f23f::/48 2a0a:f280::/32"
for cmd in iptables ip6tables; do
  $cmd -D OUTPUT -m owner --gid-owner tunnel-tg -j BYEDPI_TG 2>/dev/null || true
  $cmd -F BYEDPI_TG 2>/dev/null || $cmd -N BYEDPI_TG
done
iptables -A BYEDPI_TG -d 127.0.0.0/8 -j RETURN
for n in $TG4; do iptables -A BYEDPI_TG -d "$n" -j RETURN; done
iptables -A BYEDPI_TG -j REJECT
ip6tables -A BYEDPI_TG -d ::1 -j RETURN
for n in $TG6; do ip6tables -A BYEDPI_TG -d "$n" -j RETURN; done
ip6tables -A BYEDPI_TG -j REJECT
iptables -I OUTPUT -m owner --gid-owner tunnel-tg -j BYEDPI_TG
ip6tables -I OUTPUT -m owner --gid-owner tunnel-tg -j BYEDPI_TG
EOF
chmod +x /usr/local/sbin/byedpi-tunnel-firewall

cat > /etc/systemd/system/byedpi-tunnel-firewall.service <<'EOF'
[Unit]
Description=ByeDPI PC: Telegram-only firewall for SSH tunnel users
After=network-pre.target
Wants=network-pre.target

[Service]
Type=oneshot
RemainAfterExit=yes
ExecStart=/usr/local/sbin/byedpi-tunnel-firewall

[Install]
WantedBy=multi-user.target
EOF
systemctl daemon-reload
systemctl enable --now byedpi-tunnel-firewall.service >/dev/null
systemctl restart byedpi-tunnel-firewall.service

# Команда выдачи ключей.
cat > /usr/local/bin/tunnel-add-user <<'EOF'
#!/usr/bin/env bash
set -euo pipefail
. /etc/byedpi-tunnel.env
case "${1:-}" in
  --list)
    for u in $(getent group tunnel | cut -d: -f4 | tr ',' ' '); do
      if id -nG "$u" | grep -qw tunnel-tg; then echo "$u (только Telegram)"; else echo "$u (полный доступ)"; fi
    done; exit 0 ;;
  --remove)
    U="tun-${2:?Укажите имя}"; pkill -u "$U" 2>/dev/null || true; userdel -r "$U" 2>/dev/null || userdel "$U"
    echo "Доступ «$2» отозван."; exit 0 ;;
  ""|-*) echo "Использование: tunnel-add-user Имя [--full] | --remove Имя | --list"; exit 1 ;;
esac
NAME="$1"; FULL="${2:-}"
U="tun-$(echo "$NAME" | tr -cd 'A-Za-z0-9_-' | tr 'A-Z' 'a-z')"
PASS=$(openssl rand -base64 48 | tr -dc 'A-Za-z0-9' | cut -c1-24)
if id "$U" >/dev/null 2>&1; then
  usermod -s /usr/sbin/nologin "$U"
else
  if [ "$FULL" = "--full" ]; then
    useradd --system --create-home --shell /usr/sbin/nologin --groups tunnel "$U"
  else
    useradd --system --create-home --shell /usr/sbin/nologin --gid tunnel-tg --groups tunnel "$U"
  fi
fi
echo "$U:$PASS" | chpasswd
HOST="$PUBLIC_HOST"; case "$HOST" in *:*) HOST="[$HOST]" ;; esac
LINK="ssh://$U:$PASS@$HOST:$PUBLIC_PORT#$( [ "$FULL" = "--full" ] && echo "Туннель-$NAME" || echo "Telegram-$NAME" )"
if [ "$FULL" = "--full" ]; then
  RULES='[]'; TITLE="Туннель для $NAME"
else
  RULES='[{"Id":"preset.telegram","Name":"Telegram","Enabled":true,"Route":"Upstream","IsPreset":true,"Processes":[],"Domains":[],"IpCidrs":[]}]'
  TITLE="Telegram для $NAME"
fi
JSON=$(printf '{"format":"byedpi-pc-share","version":1,"name":"%s","comment":"Личный доступ: не передавайте его другим.","rules":%s,"upstream":"%s"}' "$TITLE" "$RULES" "$LINK")
CODE="bdpi:$(printf '%s' "$JSON" | gzip -9 | base64 -w0 | tr '+/' '-_' | tr -d '=')"
printf '%s\n' "$JSON" > "/root/$U.bdpi"
echo "Пользователь: $NAME ($U), $( [ "$FULL" = "--full" ] && echo 'полный доступ' || echo 'только Telegram')"
echo
echo "Ключ (Настройки → Расширенные → Внешний прокси):"
echo "$LINK"
echo
echo "Код для ByeDPI PC (Настройки → Обмен настройками → Импорт → Вставить код):"
echo "$CODE"
echo
echo "Файл доступа: /root/$U.bdpi"
EOF
chmod +x /usr/local/bin/tunnel-add-user

echo "Готово. Внешний адрес: $PUBLIC_HOST:$PUBLIC_PORT"
echo "Выдать доступ: tunnel-add-user Имя [--full]"
