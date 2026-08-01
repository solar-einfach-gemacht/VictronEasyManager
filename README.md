# ⚡ PVManager für Victron Cerbo GX

Ein lokaler, ultraleichter Smart-Home Energiemanager für Victron-Systeme (Cerbo GX). Steuert Wallboxen, my-PV Heizstäbe, Kaskaden und smarte Steckdosen (Shelly/Tasmota) dynamisch nach PV-Überschuss und Batteriestand (SOC).

**Das Ziel dieses Projekts: Extreme Einfachheit!** 
Keine komplexen YAML-Dateien, keine wochenlange Einarbeitung in Home Assistant oder ioBroker. Eine simple Weboberfläche, klare Regeln und intelligente Hintergrundlogik (Wolkenschutz, Hysterese, intelligentes Herantasten), die einfach funktioniert.

---

## ⚠️ Aktueller Status & Disclaimer
Dieses Projekt ist frisch aus der Entwicklung. Bitte beachtet folgenden Test-Status:

*   **✔️ GETESTET & LÄUFT:** Smarte Steckdosen / Relais (Shelly Gen 1 & Gen 2, Tasmota), Kaskaden-Schaltung.
*   **❓ UNGETESTET (Beta):** my-PV Heizstäbe (stufenlos) und Wallboxen (go-e, KEBA, Heidelberg). 
*   **Aufruf an die Community:** Wer diese Geräte besitzt, bitte testen und Feedback geben!

---

## 💡 Das Besondere: Warum nicht einfach Home Assistant?

Viele Standard-Skripte haben ein massives Problem bei Nulleinspeise- oder Inselanlagen: Das **MPPT-Abregel-Problem**. Wenn der Akku voll ist (z.B. bei 95%), regelt der Victron die Solaranlage ab. Normale Skripte sehen dann "0 Watt Überschuss" und starten die Wallbox nicht. 

**Der PVManager löst das intelligent:**
Im Modus `Batteriestand + PV-Überschuss` startet das Skript bei Erreichen des Wunsch-SOCs behutsam mit der kleinsten Leistung (z.B. 6A bei der Wallbox oder 0W beim my-PV). Danach prüft es alle 15 Sekunden die reale Batteriebilanz:
*   Wird der Akku weiter geladen? ➔ Das Skript schaltet stufenweise hoch.
*   Wird der Akku entladen (Wolke)? ➔ Das Skript drosselt sofort sanft herunter.
Das Ergebnis: Maximale Ausnutzung der Sonnenenergie, ohne nerviges Relais-Klackern!

---

## 🛠️ Voraussetzungen

Damit der PVManager Werte von deiner Anlage lesen kann, muss im Victron Cerbo GX **Modbus TCP** aktiviert sein!

1. Öffne die Remote Console deines Cerbo GX.
2. Gehe zu **Einstellungen ➔ Dienste ➔ Modbus TCP**.
3. Setze den Schalter auf **Aktiviert**.
4. Notiere dir die IP-Adresse deines Cerbo GX (wird im PVManager Dashboard benötigt).

---

## 🚀 Installation & Start

Du kannst das Tool entweder auf einem Windows-PC oder auf einem Linux-Rechner (z.B. Raspberry Pi) laufen lassen.

**Tipp: Erst auf Windows testen!**
Es ist sehr empfehlenswert, erst die Windows `.exe` herunterzuladen. Starte sie einfach auf deinem Laptop/PC, richte deine Geräte ein und schau, ob alles schaltet wie gewünscht. Wenn du zufrieden bist, kannst du das Tool dauerhaft auf einen stromsparenden Raspberry Pi umziehen.

### Für Windows:
1. Lade dir unter [Releases](https://github.com/solar-einfach-gemacht/VictronEasyManager/releases) die Windows `.exe` herunter.
2. Führe die Datei aus. Es öffnet sich ein schwarzes Konsolenfenster (das ist das "Gehirn", das im Hintergrund läuft).
3. Öffne deinen Webbrowser und tippe ein: `http://localhost:5000` (oder `http://localhost`).

### Für Raspberry Pi (Linux):
Öffne dein Terminal (SSH) auf dem Raspberry Pi und füge diesen einen Befehl ein. Er lädt das Programm herunter und richtet den Autostart automatisch ein:

```bash
curl -sSL https://raw.githubusercontent.com/solar-einfach-gemacht/VictronEasyManager/main/install.sh | bash
```

---

## ❓ FAQ: Die clevere 3-Phasen Kaskade

Die Kaskaden-Funktion schaltet Heizstäbe sanft in bis zu 3 Stufen hoch. Der **"Aus"-Wert** im Dashboard hat dabei eine besonders clevere Sicherheitsfunktion:

*   **Bei Netz-Regelung (Überschuss in Watt):**
    Der Aus-Wert ist deine **Runterschalt-Grenze**. Empfehlung: Trag hier ca. `10 Watt` ein. Fällt dein Überschuss unter 10 Watt (z.B. wegen einer Wolke), schaltet die Kaskade *nicht* komplett ab, sondern nimmt sanft nur eine Stufe heraus. Kein Ping-Pong-Effekt!
*   **Bei Batterie-Regelung (SOC in %):**
    Der Aus-Wert ist dein **Komplett-Aus (Sicherheitsnetz)**. Beispiel: Stufe 1 startet bei 90%. Du trägst als Aus-Wert `80%` ein. Wenn eine Wolke kommt, zieht der Heizstab gewollt Strom aus dem Akku, um nicht ständig zu schalten. Fällt der Akku aber unter 80%, zieht das System die Reißleine und schaltet alle Stufen ab, damit der Akku nachts nicht leergesaugt wird.

---

## 🤝 Mitmachen
Dieses Projekt ist Open Source. Fühle dich frei, den Code zu studieren, Pull Requests zu erstellen oder in den "Issues" Feedback zu den Beta-Geräten (Wallbox / my-PV) zu geben!
