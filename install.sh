#!/bin/bash
set -e

echo "========================================="
echo " Start der PVManager Installation..."
echo "========================================="

ARCH=$(uname -m)
echo "Erkannte Architektur: $ARCH (das Release muss dazu passen: aarch64 = linux-arm64, armv7l = linux-arm)"

sudo mkdir -p /opt/pvmanager

# Bei einem Update zuerst stoppen, sonst "Text file busy"
sudo systemctl stop pvmanager 2>/dev/null || true

echo "Lade PVManager herunter..."
sudo wget -O /opt/pvmanager/PVManager.new https://github.com/solar-einfach-gemacht/VictronEasyManager/releases/latest/download/VictronEasyManager
sudo chmod +x /opt/pvmanager/PVManager.new
sudo mv /opt/pvmanager/PVManager.new /opt/pvmanager/PVManager

echo "Richte Autostart ein..."
cat <<EOF | sudo tee /etc/systemd/system/pvmanager.service > /dev/null
[Unit]
Description=PVManager Hintergrund-Dienst
After=network.target

[Service]
ExecStart=/opt/pvmanager/PVManager
WorkingDirectory=/opt/pvmanager
Restart=always
RestartSec=5
User=root

[Install]
WantedBy=multi-user.target
EOF

sudo systemctl daemon-reload
sudo systemctl enable pvmanager
sudo systemctl restart pvmanager

echo "========================================================="
echo " 🎉 INSTALLATION ERFOLGREICH ABGESCHLOSSEN!"
echo " Du erreichst das Dashboard ab sofort in deinem Browser:"
echo " ➡️ http://raspberrypi.local"
echo "    (oder unter der IP-Adresse deines Raspberry Pi)"
echo "========================================================="
