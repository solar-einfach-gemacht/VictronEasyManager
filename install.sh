#!/bin/bash
set -e 

echo "========================================="
echo " Start der PVManager Installation..."
echo "========================================="

# 1. Ordner erstellen
sudo mkdir -p /opt/pvmanager

# 2. Programm herunterladen (holt jetzt VictronEasyManager und speichert es als PVManager)
echo "Lade PVManager herunter..."
sudo wget -O /opt/pvmanager/PVManager https://github.com/solar-einfach-gemacht/VictronEasyManager/releases/latest/download/VictronEasyManager

# 3. Datei ausführbar machen
sudo chmod +x /opt/pvmanager/PVManager

# 4. Autostart einrichten (Systemd Service)
echo "Richte Autostart ein..."
cat <<EOF | sudo tee /etc/systemd/system/pvmanager.service
[Unit]
Description=PVManager Hintergrund-Dienst
After=network.target

[Service]
ExecStart=/opt/pvmanager/PVManager
WorkingDirectory=/opt/pvmanager
Restart=always
User=root

[Install]
WantedBy=multi-user.target
EOF

# 5. Service aktivieren und starten
sudo systemctl daemon-reload
sudo systemctl enable pvmanager
sudo systemctl start pvmanager

echo "========================================================="
echo " 🎉 INSTALLATION ERFOLGREICH ABGESCHLOSSEN!"
echo " Du erreichst das Dashboard ab sofort in deinem Browser:"
echo " ➡️ http://raspberrypi.local"
echo "    (oder unter der IP-Adresse deines Raspberry Pi)"
echo "========================================================="
