#!/usr/bin/env bash
# Desinstala por completo el servidor de Hernani Racing (online de PC + versión web) de este Ubuntu.
# Uso:   sudo ./uninstall.sh          (pregunta antes de borrar .NET y la carpeta del proyecto)
#        sudo ./uninstall.sh --todo   (lo borra todo sin preguntar, también .NET 8 y la carpeta HernaniRacing)
set -uo pipefail

SVC=hernani-server
APP_DIR=/opt/hernani-server
CFG_DIR=/etc/hernani-server
CFG=$CFG_DIR/server.json
WEB_DIR=/srv/hernani-web
DATA_DIR=/var/lib/hernani-server
UNIT=/etc/systemd/system/$SVC.service
ALL=0
[ "${1:-}" = "--todo" ] && ALL=1

if [ "$(id -u)" -ne 0 ]; then
  echo "Hay que ejecutarlo con sudo:  sudo ./uninstall.sh"; exit 1
fi

ask() {   # ask "pregunta" -> 0 si responde s/sí
  [ $ALL -eq 1 ] && return 0
  local r
  read -r -p "$1 [s/N] " r </dev/tty || return 1
  case "$r" in s|S|si|SI|sí|Sí|y|Y) return 0 ;; *) return 1 ;; esac
}

echo "== Hernani Racing: desinstalación del servidor =="

# puertos configurados (antes de borrar la configuración), para cerrar el cortafuegos
PORT=7777; WEBPORT=8080
if [ -f $CFG ]; then
  PORT=$(grep -o '"port"[^0-9]*[0-9]*' $CFG | grep -o '[0-9]*$' || echo 7777)
  WEBPORT=$(grep -o '"webPort"[^0-9]*[0-9]*' $CFG | grep -o '[0-9]*$' || echo 8080)
fi

# 1) servicio systemd
if systemctl list-unit-files 2>/dev/null | grep -q "^$SVC.service"; then
  systemctl disable --now $SVC 2>/dev/null || true
  echo "-> Servicio $SVC parado y desactivado"
fi
rm -f "$UNIT"
systemctl daemon-reload
systemctl reset-failed $SVC 2>/dev/null || true

# 2) programa, configuración (contraseña incluida), circuitos/rankings online y juego web
rm -rf "$APP_DIR" "$CFG_DIR" "$WEB_DIR" "$DATA_DIR" /var/lib/private/hernani-server
echo "-> Borrados $APP_DIR, $CFG_DIR, $DATA_DIR (circuitos y rankings) y $WEB_DIR"

# 3) usuario del sistema
if id -u hernani >/dev/null 2>&1; then
  userdel hernani 2>/dev/null || true
  echo "-> Borrado el usuario 'hernani'"
fi
getent group hernani >/dev/null 2>&1 && groupdel hernani 2>/dev/null || true

# 4) cortafuegos de Ubuntu (ufw)
if command -v ufw >/dev/null 2>&1; then
  for rule in "$PORT/udp" "$WEBPORT/tcp" "7777/udp" "8080/tcp"; do
    n=0
    while [ $n -lt 4 ] && ufw status 2>/dev/null | grep -q "^$rule .*Hernani Racing"; do
      ufw --force delete allow "$rule" >/dev/null 2>&1 || break
      echo "-> ufw: cerrado el puerto $rule"
      n=$((n + 1))
    done
  done
fi

# 5) restos de la compilación (se compiló como root)
rm -rf /root/.nuget/packages /root/.dotnet /root/.local/share/NuGet /tmp/dotnet-install.sh /tmp/NuGetScratch* 2>/dev/null
rmdir /root/.nuget 2>/dev/null || true

# 6) .NET 8 (sólo si no lo usas para otra cosa)
if command -v dotnet >/dev/null 2>&1 && ask "¿Borrar también .NET 8? (di que no si lo usas para otras cosas)"; then
  if dpkg -l 2>/dev/null | grep -qE '^ii +(dotnet|aspnetcore)-'; then
    apt-get purge -y 'dotnet-*' 'aspnetcore-*' 'netstandard-targeting-pack-*' >/dev/null 2>&1 || true
    apt-get autoremove -y >/dev/null 2>&1 || true
  fi
  if [ -L /usr/bin/dotnet ] && readlink /usr/bin/dotnet | grep -q '/usr/share/dotnet'; then rm -f /usr/bin/dotnet; fi
  rm -rf /usr/share/dotnet
  echo "-> .NET 8 desinstalado"
fi

# 7) carpeta del proyecto descargada con git clone y el zip del build web subido
USER_HOME=$(getent passwd "${SUDO_USER:-root}" | cut -d: -f6)
SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
for REPO_DIR in "$(dirname "$SCRIPT_DIR")" "$USER_HOME/HernaniRacing"; do
  if [ -f "$REPO_DIR/Server/install.sh" ] && [ -d "$REPO_DIR/Assets/HernaniRacing" ]; then
    if ask "¿Borrar también la carpeta del proyecto $REPO_DIR?"; then
      cd / && rm -rf "$REPO_DIR"
      echo "-> Borrada $REPO_DIR"
    fi
  fi
done
for z in "$USER_HOME"/HernaniWeb*.zip "$USER_HOME"/HernaniWeb; do
  [ -e "$z" ] || continue
  if ask "¿Borrar $z?"; then rm -rf "$z"; echo "-> Borrado $z"; fi
done

echo
echo "================================================================"
echo " Hernani Racing desinstalado de este servidor."
echo
echo " Queda por hacer a mano (fuera de este servidor):"
echo "   - En el panel de tu proveedor, borra las reglas del cortafuegos"
echo "     de los puertos $PORT UDP y $WEBPORT TCP."
echo
echo " El registro del sistema (journalctl) aún guarda los mensajes antiguos del"
echo " servidor. Se borran solos con el tiempo; para borrarlos ya (OJO: borra el"
echo " registro de TODO el sistema, no sólo el del juego):"
echo "   sudo journalctl --rotate && sudo journalctl --vacuum-time=1s"
echo "================================================================"
