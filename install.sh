#!/bin/bash
echo "========================================="
echo " Start der PVManager Installation..."
echo "========================================="

# 1. Ordner erstellen
sudo mkdir -p /opt/pvmanager

# 2. Programm herunterladen (Achtung: Link anpassen, wenn du die Datei bei Releases hochlädst!)
echo "Lade PVManager herunter..."
sudo wget -O /opt/pvmanager/PVManager https://github.com/DEIN_NAME/DEIN_REPO/releases/latest/download/PVManager-Raspberry

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

echo "========================================="
echo " INTSALLATION ERFOLGREICH ABGESCHLOSSEN!"
echo " Du erreichst das Dashboard ab sofort unter:"
echo " http://<IP-DEINES-RASPBERRY-PI>"
echo "========================================="
