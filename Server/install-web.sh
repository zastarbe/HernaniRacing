#!/usr/bin/env bash
# Instala (o actualiza) la versión web del juego en el servidor.
# Uso:  sudo ./install-web.sh  CARPETA-DEL-BUILD-WEBGL   (o un .zip con esa carpeta)
# Después se juega abriendo  http://IP-DEL-SERVIDOR:8080  en Chrome, Edge o Firefox.
set -euo pipefail
WEB_DIR=/srv/hernani-web
CFG=/etc/hernani-server/server.json
SRC="${1:-}"
if [ "$(id -u)" -ne 0 ]; then echo "Hay que ejecutarlo con sudo:  sudo ./install-web.sh CARPETA-O-ZIP"; exit 1; fi
if [ -z "$SRC" ] || [ ! -e "$SRC" ]; then
  echo "Uso: sudo ./install-web.sh CARPETA-DEL-BUILD-WEBGL   (o un .zip con ella)"; exit 1
fi
TMP=""
if [ -f "$SRC" ]; then
  command -v unzip >/dev/null 2>&1 || apt-get install -y unzip >/dev/null
  TMP=$(mktemp -d)
  unzip -q "$SRC" -d "$TMP"
  SRC="$TMP"
fi
# la carpeta buena es la que tiene index.html y Build/
IDX=""
while IFS= read -r f; do
  d=$(dirname "$f")
  if [ -d "$d/Build" ]; then IDX="$d"; break; fi
done < <(find "$SRC" -maxdepth 3 -name index.html)
if [ -z "$IDX" ]; then
  echo "No encuentro un build WebGL (index.html + carpeta Build/) en $1"; [ -n "$TMP" ] && rm -rf "$TMP"; exit 1
fi
mkdir -p $WEB_DIR
rm -rf "${WEB_DIR:?}"/*
cp -r "$IDX"/* $WEB_DIR/
chmod -R a+rX $WEB_DIR
if [ -n "$TMP" ]; then rm -rf "$TMP"; fi
WEBPORT=$(grep -o '"webPort"[^0-9]*[0-9]*' $CFG 2>/dev/null | grep -o '[0-9]*$' || echo 8080)
IP=$(curl -s -4 --max-time 5 https://ifconfig.me 2>/dev/null || hostname -I | awk '{print $1}')
echo "Versión web instalada en $WEB_DIR ($(du -sh $WEB_DIR | cut -f1))."
if ls $WEB_DIR/Build/*.br >/dev/null 2>&1; then
  echo "AVISO: el build usa compresión Brotli (.br), que los navegadores sólo aceptan por https."
  echo "       En Unity: Player Settings > WebGL > Publishing Settings > Compression Format = Gzip, y vuelve a hacer el build."
fi
echo "Abre:  http://$IP:$WEBPORT"
