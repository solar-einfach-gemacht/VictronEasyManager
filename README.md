# ⚡ PVManager für Victron Cerbo GX

Ein lokaler, ultraleichter Smart-Home Energiemanager für Victron-Systeme (Cerbo GX). 

**Das Ziel dieses Projekts:** Extreme Einfachheit! Keine komplexen YAML-Dateien, keine wochenlange Einarbeitung in Home Assistant oder ioBroker. Eine simple Weboberfläche, klare Regeln und intelligente Hintergrundlogik (Wolkenschutz, Hysterese), die einfach funktioniert.

---

## ⚠️ Aktueller Status & Disclaimer
Dieses Projekt ist frisch aus der Entwicklung. Bitte beachtet folgenden Test-Status:
* **✔️ GETESTET & LÄUFT:** Smarte Steckdosen / Relais (Shelly Gen 1 & Gen 2, Tasmota), Kaskaden-Schaltung.
* **❓ UNGETESTET (Beta):** my-PV Heizstäbe (stufenlos) und Wallboxen (go-e, KEBA, Heidelberg). 
*Aufruf an die Community: Wer diese Geräte besitzt, bitte testen und Feedback geben!*

---

## 🛠️ Voraussetzungen
Damit der PVManager Werte von deiner Anlage lesen kann, muss im Victron Cerbo GX **Modbus TCP** aktiviert sein!
1. Öffne die Remote Console deines Cerbo GX.
2. Gehe zu **Einstellungen** ➔ **Dienste** ➔ **Modbus TCP**.
3. Setze den Schalter auf **Aktiviert**.
4. Notiere dir die IP-Adresse deines Cerbo GX (wird im PVManager Dashboard benötigt).

---

## 🚀 Installation & Start

Du kannst das Tool entweder auf einem Windows-PC oder auf einem Linux-Rechner (z.B. Raspberry Pi) laufen lassen.

### Tipp: Erst auf Windows testen!
Es ist sehr empfehlenswert, erst die **Windows `.exe`** herunterzuladen. Starte sie einfach auf deinem Laptop/PC, richte deine Geräte ein und schau, ob alles schaltet wie gewünscht. Wenn du zufrieden bist, kannst du das Tool dauerhaft auf einen stromsparenden Raspberry Pi umziehen.

**Für Windows:**
1. Lade dir unter **[Releases]** die Windows `.exe` herunter.
2. Führe die Datei aus. Es öffnet sich ein schwarzes Konsolenfenster (das ist das "Gehirn", das im Hintergrund läuft).
3. Öffne deinen Webbrowser und tippe ein: `http://localhost:5000`

**Für Raspberry Pi (Linux):**
Öffne dein Terminal (SSH) auf dem Raspberry Pi und füge diesen einen Befehl ein. Er lädt das Programm herunter und richtet den Autostart automatisch ein:
```bash
curl -sSL [https://raw.githubusercontent.com/solar-einfach-gemacht/VictronEasyManager/main/install.sh](https://raw.githubusercontent.com/solar-einfach-gemacht/VictronEasyManager/main/install.sh) | bash
