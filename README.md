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

1. Lade dir unter **[Releases]** die passende Version für dein System herunter (Windows `.exe` oder Linux).
2. Führe die Datei aus. Es öffnet sich ein schwarzes Konsolenfenster (das ist das "Gehirn", das im Hintergrund läuft).
3. Öffne deinen Webbrowser und tippe ein:
   * Wenn du am **Windows-PC** testest: `http://localhost:5000`
   * Wenn das Tool auf einem **Raspberry Pi** läuft: `http://victroneasymanager.local` *(falls das Netzwerk den Namen nicht auflöst, nutze einfach die IP-Adresse des Pi).*
4. Klicke im Dashboard auf **⚙️ System Einstellungen** und trage die IP-Adresse deines Victron Cerbo GX ein. Fertig!

---

## 🧠 Die Logik (So arbeitet der Manager)

Der PVManager nimmt dir das komplizierte Mitdenken ab. Er reagiert nicht panisch auf jede Sekunde Schatten, sondern analysiert dein System intelligent.

### 1. Der Wolkenschutz (Hysterese)
Du kannst für jedes Gerät eine Zeitverzögerung einstellen. Geht z.B. kurz der Wasserkocher an und dein Stromzähler rutscht in den Netzbezug, schaltet das Skript deine Heizstäbe nicht sofort panisch aus. Es startet ein unsichtbarer Timer. Nur wenn der Netzbezug z.B. 5 Minuten lang anhält, wird abgeschaltet (Relais-Schutz!).

### 2. Smarte Kaskadensteuerung (Für 3-Phasen-Heizstäbe)
Du kannst bis zu 3 Relais zu einer Kaskade bündeln. 
* **Bei Netz-Anlagen:** Das Skript misst den Überschuss. Ist genug Strom für Stufe 1 da, geht sie an. Der Netz-Überschuss sinkt logischerweise. Steigt die Sonne weiter und es entsteht *erneut* Überschuss, kommt Stufe 2 dazu. Fällt die Sonne weg, schalten sich die Stufen mit einem festen 2-Minuten-Timer nacheinander wieder sauber ab.
* **Bei Insel-Anlagen:** Steuer deine Kaskade rein nach dem Batteriestand (z.B. Stufe 1 ab 90%, Stufe 2 ab 95%).

### 3. Wallbox & stufenlose Heizstäbe (Prioritäten-Weiche)
Diese Geräte beherrschen eine geniale Logik: **"Batteriestand + PV-Überschuss"**.
Das bedeutet: Dein Haus-Akku wird z.B. bis 95% geladen. Sobald die 95% erreicht sind, friert das Skript die Batterieladung quasi ein und leitet den gesamten restlichen PV-Strom vom Dach exakt und stufenlos in dein Auto oder deinen Heizstab um. 

---

## 🤝 Mithelfen
Da dies ein Community-Projekt ist: Ladet euch den Code herunter, bastelt daran herum, meldet Fehler (Issues) oder reicht Verbesserungen ein. Besonderes Augenmerk liegt aktuell auf dem Testen der Wallbox- und my-PV-Schnittstellen!

*Lizenz: MIT License - Nutzung auf eigene Gefahr.*
