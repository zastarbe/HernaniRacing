#!/usr/bin/env bash
# Activa https en la versión web de Hernani Racing (necesario para el chat de voz en el navegador).
# Uso:   cd HernaniRacing/Server && sudo ./install-https.sh racing.astarbe.com [correo@ejemplo.com]
#
# Antes: el nombre (registro A del DNS) tiene que apuntar a la IP de este servidor.
# Qué hace:
#  1) Si ya hay un certificado de Let's Encrypt para ese nombre (/etc/letsencrypt/live/NOMBRE) lo usa.
#     Si no, lo pide con certbot en modo "standalone" (necesita el puerto 80 TCP libre y abierto un momento).
#  2) Copia fullchain.pem y privkey.pem a /etc/hernani-server/tls/ (el servicio corre como usuario
#     "hernani" y no puede leer /etc/letsencrypt) e instala un "deploy hook" que los vuelve a copiar
#     en cada renovación. El servidor recarga el certificado nuevo solo, sin reiniciar.
#  3) Pone tlsCert/tlsKey en /etc/hernani-server/server.json y reinicia el servicio.
# Desde entonces la web va por https://NOMBRE:PUERTO_WEB (el mismo puerto, 8080 por defecto) y quien
# entre por http:// a ese puerto es redirigido a https://.
set -euo pipefail

NAME="${1:-}"
EMAIL="${2:-}"
CFG_DIR=/etc/hernani-server
CFG=$CFG_DIR/server.json
TLS_DIR=$CFG_DIR/tls
SVC=hernani-server
HOOK=/etc/letsencrypt/renewal-hooks/deploy/hernani-racing.sh

if [ "$(id -u)" -ne 0 ]; then
  echo "Hay que ejecutarlo con sudo:  sudo ./install-https.sh nombre.dominio.com"; exit 1
fi
if [ -z "$NAME" ]; then
  echo "Uso: sudo ./install-https.sh nombre.dominio.com [correo]"; exit 1
fi
if [ ! -f $CFG ]; then
  echo "No encuentro $CFG: instala antes el servidor con  sudo ./install.sh"; exit 1
fi
echo "== Hernani Racing: https para $NAME =="

# comprobación del DNS (sólo aviso)
MYIP=$(curl -s -4 --max-time 5 https://ifconfig.me 2>/dev/null || true)
DNSIP=$(getent ahostsv4 "$NAME" 2>/dev/null | awk 'NR==1{print $1}' || true)
if [ -z "$DNSIP" ]; then
  echo "AVISO: $NAME todavía no resuelve a ninguna IP. Crea el registro A en tu DNS (-> $MYIP) y espera unos minutos."
elif [ -n "$MYIP" ] && [ "$DNSIP" != "$MYIP" ]; then
  echo "AVISO: $NAME apunta a $DNSIP pero este servidor es $MYIP."
fi

LIVE=/etc/letsencrypt/live/$NAME
if [ ! -f "$LIVE/fullchain.pem" ]; then
  if ! command -v certbot >/dev/null 2>&1; then
    echo "-> Instalando certbot..."
    apt-get update -y && apt-get install -y certbot
  fi
  if ss -ltn 2>/dev/null | awk '{print $4}' | grep -qE '(^|:)80$'; then
    echo "El puerto 80 lo está usando otro programa (¿nginx/apache?). Pide el certificado con él, por ejemplo"
    echo "   sudo certbot certonly --webroot -w /var/www/html -d $NAME"
    echo "(o con el plugin de nginx/apache) y vuelve a ejecutar este script: usará ese certificado."
    exit 1
  fi
  OPENED80=0
  if command -v ufw >/dev/null 2>&1 && ufw status | grep -q "Status: active"; then
    ufw allow 80/tcp comment "certbot (Hernani Racing)" >/dev/null && OPENED80=1
  fi
  echo "-> Pidiendo el certificado a Let's Encrypt (si tu proveedor tiene cortafuegos en su panel, abre el 80 TCP)..."
  if [ -n "$EMAIL" ]; then MAIL_ARGS=(-m "$EMAIL"); else MAIL_ARGS=(--register-unsafely-without-email); fi
  certbot certonly --standalone --non-interactive --agree-tos "${MAIL_ARGS[@]}" -d "$NAME"
  # la renovación automática también necesitará el puerto 80: lo dejamos abierto si lo abrimos nosotros
  [ $OPENED80 = 1 ] && echo "   (ufw: el puerto 80/tcp queda abierto para las renovaciones automáticas)"
fi

# copia legible para el usuario del servicio + hook de renovación
mkdir -p "$(dirname $HOOK)" $TLS_DIR
cat > $HOOK <<HOOKEOF
#!/bin/sh
# Copia el certificado renovado para Hernani Racing (lo recarga solo en menos de un minuto).
L=/etc/letsencrypt/live/$NAME
[ -f "\$L/fullchain.pem" ] || exit 0
install -o hernani -g hernani -m 600 "\$L/fullchain.pem" $TLS_DIR/fullchain.pem
install -o hernani -g hernani -m 600 "\$L/privkey.pem"   $TLS_DIR/privkey.pem
HOOKEOF
chmod 755 $HOOK
$HOOK
chown hernani:hernani $TLS_DIR
chmod 700 $TLS_DIR

# server.json: tlsCert / tlsKey
python3 - "$CFG" "$TLS_DIR" <<'PY'
import json, sys
cfg, tls = sys.argv[1], sys.argv[2]
with open(cfg, encoding="utf8") as f:
    d = json.load(f)
d["tlsCert"] = tls + "/fullchain.pem"
d["tlsKey"] = tls + "/privkey.pem"
d.setdefault("webTrustOwnPage", True)
with open(cfg, "w", encoding="utf8") as f:
    json.dump(d, f, ensure_ascii=False, indent=2)
PY
chown hernani:hernani $CFG
chmod 640 $CFG
systemctl restart $SVC
sleep 2
WEBPORT=$(grep -o '"webPort"[^0-9]*[0-9]*' $CFG | grep -o '[0-9]*$' || echo 8080)
echo
journalctl -u $SVC --no-pager -n 6 -o cat || true
echo
echo "================================================================"
echo " https activado. La versión web está en:"
echo "     https://$NAME:$WEBPORT"
echo " (http://$NAME:$WEBPORT y http://IP:$WEBPORT redirigen a https)"
echo " El certificado se renueva solo (certbot) y el servidor lo recarga sin reiniciarse."
echo " Para quitar https: borra tlsCert y tlsKey de $CFG y  sudo systemctl restart $SVC"
echo "================================================================"
