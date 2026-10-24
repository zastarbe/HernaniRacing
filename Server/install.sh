#!/usr/bin/env bash
# Instala (o actualiza) el servidor de Hernani Racing en Ubuntu como servicio de systemd.
# Uso:   cd HernaniRacing/Server && sudo ./install.sh
# Volver a ejecutarlo después de un "git pull" actualiza el servidor sin tocar la configuración.
set -euo pipefail

PORT_DEFAULT=7777
WEBPORT_DEFAULT=8080
WEB_DIR=/srv/hernani-web
APP_DIR=/opt/hernani-server
CFG_DIR=/etc/hernani-server
CFG=$CFG_DIR/server.json
SVC=hernani-server
SRC_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)/HernaniServer"

if [ "$(id -u)" -ne 0 ]; then
  echo "Hay que ejecutarlo con sudo:  sudo ./install.sh"; exit 1
fi
echo "== Hernani Racing: instalación del servidor =="

# 1) .NET 8 (para compilar y ejecutar el servidor)
if ! command -v dotnet >/dev/null 2>&1 || ! dotnet --list-sdks 2>/dev/null | grep -q '^8\.'; then
  echo "-> Instalando .NET 8..."
  apt-get update -y
  if ! apt-get install -y dotnet-sdk-8.0; then
    echo "   (no está en los repositorios de este Ubuntu: uso el instalador oficial de Microsoft)"
    apt-get install -y curl ca-certificates libicu-dev || true
    curl -sSL https://dot.net/v1/dotnet-install.sh -o /tmp/dotnet-install.sh
    bash /tmp/dotnet-install.sh --channel 8.0 --install-dir /usr/share/dotnet
    ln -sf /usr/share/dotnet/dotnet /usr/bin/dotnet
  fi
fi
echo "-> .NET: $(dotnet --version)"

# 2) Compilar y copiar a /opt
echo "-> Compilando el servidor..."
export DOTNET_CLI_TELEMETRY_OPTOUT=1 DOTNET_NOLOGO=1
BUILD_TMP=$(mktemp -d)
dotnet publish "$SRC_DIR/HernaniServer.csproj" -c Release -o "$BUILD_TMP" --nologo -v q
rm -rf "$SRC_DIR/bin" "$SRC_DIR/obj"
systemctl stop $SVC 2>/dev/null || true
mkdir -p $APP_DIR
rm -rf "${APP_DIR:?}"/*
cp -r "$BUILD_TMP"/* $APP_DIR/
rm -rf "$BUILD_TMP"

# 3) Usuario sin privilegios y configuración
id -u hernani >/dev/null 2>&1 || useradd --system --no-create-home --shell /usr/sbin/nologin hernani
mkdir -p $CFG_DIR
NEW_PASS=""
if [ ! -f $CFG ]; then
  NEW_PASS=$(tr -dc 'a-km-np-z2-9' </dev/urandom | head -c 8 || true)
  cat > $CFG <<JSON
{
  "port": $PORT_DEFAULT,
  "password": "$NEW_PASS",
  "serverName": "Servidor Hernani Racing",
  "motd": "¡Bienvenidos! Conducid con cuidado por Hernani.",
  "maxPlayers": 64,
  "maxRooms": 16,
  "webPort": $WEBPORT_DEFAULT,
  "webRoot": "$WEB_DIR"
}
JSON
fi
# carpeta de la versión web (el build WebGL se instala con install-web.sh)
mkdir -p $WEB_DIR
chmod 755 $WEB_DIR
chown -R hernani:hernani $CFG_DIR
chmod 640 $CFG
PORT=$(grep -o '"port"[^0-9]*[0-9]*' $CFG | grep -o '[0-9]*$' || echo $PORT_DEFAULT)
WEBPORT=$(grep -o '"webPort"[^0-9]*[0-9]*' $CFG | grep -o '[0-9]*$' || echo $WEBPORT_DEFAULT)

# 4) Servicio systemd (arranca solo al encender el servidor y se reinicia si falla)
cat > /etc/systemd/system/$SVC.service <<UNIT
[Unit]
Description=Hernani Racing - servidor multijugador
After=network-online.target
Wants=network-online.target

[Service]
User=hernani
WorkingDirectory=$CFG_DIR
ExecStart=/usr/bin/env dotnet $APP_DIR/hernani-server.dll --config $CFG
Restart=always
RestartSec=3
Environment=DOTNET_CLI_TELEMETRY_OPTOUT=1
NoNewPrivileges=true
ProtectSystem=strict
ProtectHome=true
ReadWritePaths=$CFG_DIR
PrivateTmp=true

[Install]
WantedBy=multi-user.target
UNIT
systemctl daemon-reload
systemctl enable $SVC >/dev/null
systemctl restart $SVC

# 5) Cortafuegos de Ubuntu (si está activo)
if command -v ufw >/dev/null 2>&1 && ufw status | grep -q "Status: active"; then
  ufw allow $PORT/udp comment "Hernani Racing" >/dev/null && echo "-> ufw: abierto el puerto $PORT/udp"
  if [ "$WEBPORT" != "0" ]; then
    ufw allow $WEBPORT/tcp comment "Hernani Racing web" >/dev/null && echo "-> ufw: abierto el puerto $WEBPORT/tcp (versión web)"
  fi
fi

sleep 2
echo
systemctl --no-pager --lines=5 status $SVC || true
IP=$(curl -s -4 --max-time 5 https://ifconfig.me 2>/dev/null || hostname -I | awk '{print $1}')
echo
echo "================================================================"
echo " Servidor instalado y en marcha."
echo "   Dirección para el juego:  $IP"
echo "   Puerto UDP:               $PORT"
if [ "$WEBPORT" != "0" ]; then
echo "   Versión web (navegador):  http://$IP:$WEBPORT   (puerto TCP $WEBPORT)"
if [ ! -f $WEB_DIR/index.html ]; then
echo "     -> aún sin juego web: haz el build WebGL en Unity y ejecuta"
echo "        sudo ./install-web.sh carpeta-o-zip-del-build"
fi
fi
if [ -n "$NEW_PASS" ]; then
echo "   Contraseña (nueva):       $NEW_PASS"
else
echo "   Contraseña:               la de $CFG"
fi
echo
echo " IMPORTANTE: si tu proveedor (OVH, Hetzner, AWS, Oracle...) tiene un"
echo " cortafuegos en su panel web, abre allí también el puerto $PORT UDP"
echo " y, para la versión web, el puerto $WEBPORT TCP."
echo
echo " Ver el registro:   sudo journalctl -u $SVC -f"
echo " Cambiar opciones:  sudo nano $CFG  y luego  sudo systemctl restart $SVC"
echo "================================================================"
