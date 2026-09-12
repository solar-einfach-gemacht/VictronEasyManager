using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using System.Net.Http;
using System.Net.Sockets; 
using System.Net;         
using System.Text;        
using FluentModbus; 
using System.Linq;

namespace VictronEasyManager
{
    public class AutomationEngine
    {
        private readonly DeviceManager _deviceManager;
        
        private readonly Dictionary<string, DateTime> _pendingOnTimers = new Dictionary<string, DateTime>();
        private readonly Dictionary<string, DateTime> _pendingOffTimers = new Dictionary<string, DateTime>();

        private readonly Dictionary<string, int> _deviceTargets = new Dictionary<string, int>();
        private readonly Dictionary<string, DateTime> _lastAdjustments = new Dictionary<string, DateTime>();

        private static readonly HttpClient _httpClient = new HttpClient { Timeout = TimeSpan.FromSeconds(3) };

        public AutomationEngine(DeviceManager deviceManager)
        {
            _deviceManager = deviceManager;
        }

        public async Task StartAsync()
        {
            Console.WriteLine("==================================================");
            Console.WriteLine("  PVManager Gehirn (Intelligente Logik) gestartet!");
            Console.WriteLine("==================================================");

            using var client = new ModbusTcpClient();

            while (true)
            {
                try
                {
                    string aktuelleVictronIp = _deviceManager.Settings.VictronIp;

                    if (string.IsNullOrWhiteSpace(aktuelleVictronIp))
                    {
                        Console.WriteLine("[INFO] Warte auf Victron Cerbo IP... (Bitte im Dashboard eintragen!)");
                        await Task.Delay(5000);
                        continue;
                    }

                    if (!client.IsConnected) 
                    { 
                        Console.WriteLine($"[VERBINDUNG] Verbinde mit Victron unter: {aktuelleVictronIp} ...");
                        client.Connect(aktuelleVictronIp); 
                    }

                    var (currentSoc, currentVoltage, currentGridPower, currentPv, currentAc, g1, g2, g3, ac1, ac2, ac3) = ReadVictronData(client);
                    
                    _deviceManager.LiveSoc = currentSoc;
                    _deviceManager.LiveVoltage = currentVoltage;
                    _deviceManager.LiveGrid = currentGridPower;
                    _deviceManager.LivePv = currentPv;
                    _deviceManager.LiveAcLoad = currentAc;
                    
                    _deviceManager.LiveGridL1 = g1;
                    _deviceManager.LiveGridL2 = g2;
                    _deviceManager.LiveGridL3 = g3;
                    
                    _deviceManager.LiveAcL1 = ac1;
                    _deviceManager.LiveAcL2 = ac2;
                    _deviceManager.LiveAcL3 = ac3;

                    double calcBatteryCharge = currentPv - currentAc + currentGridPower;

                    Console.WriteLine($"[LIVE] SOC: {currentSoc}% | PV: {currentPv}W | AC-Last: {currentAc}W | Netz: {currentGridPower}W | Akku lädt mit: {calcBatteryCharge}W");

                    // ==========================================================
                    // NEU: Priorisierung und Virtueller Überschuss
                    // ==========================================================
                    // Geräte nach Priorität sortieren (1 = höchste)
                    var devices = _deviceManager.GetAllDevices().OrderBy(d => d.Priority).ToList();
                    
                    // Den verfügbaren Überschuss ermitteln (Negativer Netzbezug = Einspeisung)
                    double virtualSurplus = currentGridPower < 0 ? Math.Abs(currentGridPower) : 0;

                    foreach (var device in devices)
                    {
                        // ==========================================================
                        // MANUELLE ÜBERSTEUERUNG
                        // ==========================================================
                        if (device.SystemType == "cascade")
                        {
                            if (device.ManualOverride == "off") {
                                if (device.CurrentCascadeStep != 0) {
                                    device.CurrentCascadeStep = 0;
                                    _deviceManager.SaveDevices();
                                    await ApplyCascadeStateAsync(device);
                                }
                                continue;
                            }
                            else if (device.ManualOverride == "on") {
                                if (device.CurrentCascadeStep != 3) {
                                    device.CurrentCascadeStep = 3;
                                    _deviceManager.SaveDevices();
                                    await ApplyCascadeStateAsync(device);
                                }
                                continue;
                            }
                        }
                        else
                        {
                            if (device.ManualOverride == "off") {
                                if (device.IsOn) {
                                    device.IsOn = false; _deviceManager.SaveDevices();
                                    await ApplyManualStateAsync(device, false);
                                }
                                continue; 
                            }
                            else if (device.ManualOverride == "on") {
                                if (!device.IsOn) {
                                    device.IsOn = true; _deviceManager.SaveDevices();
                                    await ApplyManualStateAsync(device, true);
                                }
                                continue; 
                            }
                        }

                        // ==========================================================
                        // KASKADEN-WEICHE (3-Phasen-Heizstab)
                        // ==========================================================
                        if (device.SystemType == "cascade")
                        {
                            int desiredStep = device.CurrentCascadeStep;

                            if (device.ControlMode == "soc")
                            {
                                if (currentSoc >= device.OnValue3) desiredStep = 3;
                                else if (currentSoc >= device.OnValue2) desiredStep = 2;
                                else if (currentSoc >= device.OnValue) desiredStep = 1;
                                else if (currentSoc < device.OffValue) desiredStep = 0;
                            }
                            else // Grid Mode
                            {
                                if (device.CurrentCascadeStep == 0 && virtualSurplus >= device.OnValue) { 
                                    desiredStep = 1; virtualSurplus -= device.OnValue; 
                                }
                                else if (device.CurrentCascadeStep == 1) {
                                    if (virtualSurplus >= device.OnValue2) { desiredStep = 2; virtualSurplus -= device.OnValue2; }
                                    else if (virtualSurplus < device.OffValue) { desiredStep = 0; }
                                }
                                else if (device.CurrentCascadeStep == 2) {
                                    if (virtualSurplus >= device.OnValue3) { desiredStep = 3; virtualSurplus -= device.OnValue3; }
                                    else if (virtualSurplus < device.OffValue) { desiredStep = 1; }
                                }
                                else if (device.CurrentCascadeStep == 3) {
                                    if (virtualSurplus < device.OffValue) { desiredStep = 2; }
                                }
                                
                                if (virtualSurplus < 0) virtualSurplus = 0; // Absicherung
                            }

                            if (desiredStep > device.CurrentCascadeStep)
                            {
                                _pendingOffTimers.Remove(device.Id);
                                if (!_pendingOnTimers.ContainsKey(device.Id)) _pendingOnTimers[device.Id] = DateTime.Now;
                                else if ((DateTime.Now - _pendingOnTimers[device.Id]).TotalMinutes >= device.OnDelayMinutes) {
                                    device.CurrentCascadeStep = desiredStep; _deviceManager.SaveDevices(); _pendingOnTimers.Remove(device.Id);
                                    _ = ApplyCascadeStateAsync(device);
                                }
                            }
                            else if (desiredStep < device.CurrentCascadeStep)
                            {
                                _pendingOnTimers.Remove(device.Id);
                                if (!_pendingOffTimers.ContainsKey(device.Id)) _pendingOffTimers[device.Id] = DateTime.Now;
                                else if ((DateTime.Now - _pendingOffTimers[device.Id]).TotalMinutes >= device.OffDelayMinutes) {
                                    device.CurrentCascadeStep = desiredStep; _deviceManager.SaveDevices(); _pendingOffTimers.Remove(device.Id);
                                    _ = ApplyCascadeStateAsync(device);
                                }
                            }
                            else 
                            {
                                _pendingOnTimers.Remove(device.Id);
                                _pendingOffTimers.Remove(device.Id);
                            }
                            
                            continue;
                        }

                        // ==========================================================
                        // HEIZSTAB-WEICHE (Stufenlos my-PV)
                        // ==========================================================
                        if (device.SystemType == "heater" && device.DeviceModel == "mypv")
                        {
                            int targetWatt = 0;

                            if (device.ControlMode == "soc")
                            {
                                if (currentSoc >= device.OnValue) targetWatt = 3000; 
                                else if (currentSoc < device.OffValue) targetWatt = 0;
                                else targetWatt = device.IsOn ? 1500 : 0; 
                            }
                            else if (device.ControlMode == "soc_pv")
                            {
                                bool allowHeat = false;
                                if (currentSoc >= device.OnValue) allowHeat = true;
                                else if (currentSoc < device.OffValue) allowHeat = false;
                                else allowHeat = device.IsOn;

                                if (allowHeat)
                                {
                                    if (!_deviceTargets.ContainsKey(device.Id)) _deviceTargets[device.Id] = 0;
                                    if (!_lastAdjustments.ContainsKey(device.Id)) _lastAdjustments[device.Id] = DateTime.Now.AddSeconds(-20);

                                    targetWatt = _deviceTargets[device.Id];

                                    if ((DateTime.Now - _lastAdjustments[device.Id]).TotalSeconds >= 15)
                                    {
                                        if (calcBatteryCharge > 400) { targetWatt += 200; } 
                                        else if (calcBatteryCharge < -200) { targetWatt -= 200; } 

                                        if (targetWatt < 0) targetWatt = 0;
                                        if (targetWatt > 3000) targetWatt = 3000;

                                        if (targetWatt != _deviceTargets[device.Id]) {
                                            _deviceTargets[device.Id] = targetWatt;
                                            _lastAdjustments[device.Id] = DateTime.Now;
                                        }
                                    }
                                }
                            }
                            else // Grid Mode
                            {
                                targetWatt = (int)Math.Floor(virtualSurplus);
                                if (targetWatt > 3000) targetWatt = 3000;
                                
                                // Reservierung vom virtuellen Überschuss
                                if (targetWatt > 0) {
                                    virtualSurplus -= targetWatt;
                                    if (virtualSurplus < 0) virtualSurplus = 0;
                                }
                            }

                            bool desiredHeat = targetWatt > 0;

                            if (desiredHeat && !device.IsOn)
                            {
                                _pendingOffTimers.Remove(device.Id);
                                if (!_pendingOnTimers.ContainsKey(device.Id)) _pendingOnTimers[device.Id] = DateTime.Now;
                                else if ((DateTime.Now - _pendingOnTimers[device.Id]).TotalMinutes >= device.OnDelayMinutes) {
                                    device.IsOn = true; _deviceManager.SaveDevices(); _pendingOnTimers.Remove(device.Id);
                                }
                            }
                            else if (!desiredHeat && device.IsOn)
                            {
                                _pendingOnTimers.Remove(device.Id);
                                if (!_pendingOffTimers.ContainsKey(device.Id)) _pendingOffTimers[device.Id] = DateTime.Now;
                                else if ((DateTime.Now - _pendingOffTimers[device.Id]).TotalMinutes >= device.OffDelayMinutes) {
                                    device.IsOn = false; _deviceManager.SaveDevices(); _pendingOffTimers.Remove(device.Id);
                                }
                                else { targetWatt = 0; } 
                            }
                            else 
                            {
                                _pendingOnTimers.Remove(device.Id);
                                _pendingOffTimers.Remove(device.Id);
                            }

                            int finalWatt = device.IsOn ? targetWatt : 0;
                            await SendMyPvCommandAsync(device.IpAddress, finalWatt);
                            
                            continue; 
                        }

                        // ==========================================================
                        // WALLBOX-WEICHE
                        // ==========================================================
                        if (device.SystemType == "wallbox")
                        {
                            bool desiredAllowCharge = false;
                            int desiredAmpere = 6;

                            if (device.ControlMode == "soc")
                            {
                                if (currentSoc >= device.OnValue) { desiredAllowCharge = true; desiredAmpere = 16; }
                                else if (currentSoc < device.OffValue) { desiredAllowCharge = false; }
                                else { desiredAllowCharge = device.IsOn; desiredAmpere = 10; }
                            }
                            else if (device.ControlMode == "soc_pv")
                            {
                                if (currentSoc >= device.OnValue) desiredAllowCharge = true;
                                else if (currentSoc < device.OffValue) desiredAllowCharge = false;
                                else desiredAllowCharge = device.IsOn;

                                if (desiredAllowCharge)
                                {
                                    if (!_deviceTargets.ContainsKey(device.Id)) _deviceTargets[device.Id] = 6;
                                    if (!_lastAdjustments.ContainsKey(device.Id)) _lastAdjustments[device.Id] = DateTime.Now.AddSeconds(-20);

                                    desiredAmpere = _deviceTargets[device.Id];

                                    if ((DateTime.Now - _lastAdjustments[device.Id]).TotalSeconds >= 15)
                                    {
                                        if (calcBatteryCharge > 500) { desiredAmpere++; } 
                                        else if (calcBatteryCharge < -200) { desiredAmpere--; } 

                                        if (desiredAmpere < 6) desiredAmpere = 6;
                                        if (desiredAmpere > 16) desiredAmpere = 16;

                                        if (desiredAmpere != _deviceTargets[device.Id]) {
                                            _deviceTargets[device.Id] = desiredAmpere;
                                            _lastAdjustments[device.Id] = DateTime.Now;
                                        }
                                    }
                                }
                            }
                            else // Grid Mode
                            {
                                desiredAmpere = (int)Math.Floor(virtualSurplus / 690.0);

                                if (desiredAmpere >= 6) { 
                                    if (desiredAmpere > 16) desiredAmpere = 16; 
                                    desiredAllowCharge = true; 
                                    
                                    // Reservierung vom virtuellen Überschuss
                                    virtualSurplus -= (desiredAmpere * 690.0);
                                    if (virtualSurplus < 0) virtualSurplus = 0;
                                }
                                else { desiredAllowCharge = false; desiredAmpere = 6; }
                            }

                            if (desiredAllowCharge && !device.IsOn)
                            {
                                _pendingOffTimers.Remove(device.Id);
                                if (!_pendingOnTimers.ContainsKey(device.Id)) _pendingOnTimers[device.Id] = DateTime.Now;
                                else if ((DateTime.Now - _pendingOnTimers[device.Id]).TotalMinutes >= device.OnDelayMinutes) {
                                    device.IsOn = true; _deviceManager.SaveDevices(); _pendingOnTimers.Remove(device.Id);
                                }
                            }
                            else if (!desiredAllowCharge && device.IsOn)
                            {
                                _pendingOnTimers.Remove(device.Id);
                                if (!_pendingOffTimers.ContainsKey(device.Id)) _pendingOffTimers[device.Id] = DateTime.Now;
                                else if ((DateTime.Now - _pendingOffTimers[device.Id]).TotalMinutes >= device.OffDelayMinutes) {
                                    device.IsOn = false; _deviceManager.SaveDevices(); _pendingOffTimers.Remove(device.Id);
                                }
                                else { desiredAllowCharge = true; desiredAmpere = 6; } 
                            }
                            else
                            {
                                _pendingOnTimers.Remove(device.Id);
                                _pendingOffTimers.Remove(device.Id);
                            }

                            bool finalAllowCharge = device.IsOn;
                            int finalAmpere = finalAllowCharge ? desiredAmpere : 6;

                            if (device.DeviceModel == "goe") await SendGoeCommandAsync(device.IpAddress, finalAllowCharge, finalAmpere);
                            else if (device.DeviceModel == "keba") await SendKebaCommandAsync(device.IpAddress, finalAllowCharge, finalAmpere);
                            else if (device.DeviceModel == "heidelberg") await SendHeidelbergCommandAsync(device.IpAddress, finalAllowCharge, finalAmpere);
                            else if (device.DeviceModel == "openwb") await SendOpenWbCommandAsync(device.IpAddress, finalAllowCharge, finalAmpere, isV2: false);
                            else if (device.DeviceModel == "openwb2") await SendOpenWbCommandAsync(device.IpAddress, finalAllowCharge, finalAmpere, isV2: true);

                            continue; 
                        }

                        // ==========================================================
                        // STECKDOSEN LOGIK (Grid / Island)
                        // ==========================================================
                        bool? echterStatus = await FrageGeraeteStatusAbAsync(device);
                        if (echterStatus.HasValue && device.IsOn != echterStatus.Value)
                        {
                            device.IsOn = echterStatus.Value; 
                            _deviceManager.SaveDevices(); 
                        }

                        bool shouldBeOn = false;
                        bool shouldBeOff = false;

                        if (device.SystemType == "grid")
                        {
                            if (virtualSurplus >= device.OnValue) { 
                                shouldBeOn = true; 
                                
                                // Reservierung vom virtuellen Überschuss (nur wenn es noch nicht an ist, 
                                // da der reale Überschuss sonst verfälscht wird)
                                if (!device.IsOn) {
                                    virtualSurplus -= device.OnValue;
                                    if (virtualSurplus < 0) virtualSurplus = 0;
                                }
                            }
                            else if (virtualSurplus < device.OffValue) { shouldBeOff = true; }
                        }
                        else if (device.SystemType == "island")
                        {
                            if (currentSoc >= device.OnValue) { shouldBeOn = true; }
                            else if (currentSoc < device.OffValue) { shouldBeOff = true; }
                        }

                        if (shouldBeOn && !device.IsOn)
                        {
                            _pendingOffTimers.Remove(device.Id);
                            if (!_pendingOnTimers.ContainsKey(device.Id)) _pendingOnTimers[device.Id] = DateTime.Now;
                            else if ((DateTime.Now - _pendingOnTimers[device.Id]).TotalMinutes >= device.OnDelayMinutes) {
                                device.IsOn = true; _deviceManager.SaveDevices(); _pendingOnTimers.Remove(device.Id);
                                _ = SendSwitchCommandAsync(device, true);
                            }
                        }
                        else { _pendingOnTimers.Remove(device.Id); }

                        if (shouldBeOff && device.IsOn)
                        {
                            _pendingOnTimers.Remove(device.Id);
                            if (!_pendingOffTimers.ContainsKey(device.Id)) _pendingOffTimers[device.Id] = DateTime.Now;
                            else if ((DateTime.Now - _pendingOffTimers[device.Id]).TotalMinutes >= device.OffDelayMinutes) {
                                device.IsOn = false; _deviceManager.SaveDevices(); _pendingOffTimers.Remove(device.Id);
                                _ = SendSwitchCommandAsync(device, false);
                            }
                        }
                        else { _pendingOffTimers.Remove(device.Id); }
                    }
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"[FEHLER] Modbus-Verbindung unterbrochen: {ex.Message}");
                    if (client.IsConnected) { client.Disconnect(); }
                }

                await Task.Delay(5000); 
            }
        }

        private async Task ApplyCascadeStateAsync(ShellyDevice device)
        {
            bool st1 = device.CurrentCascadeStep >= 1;
            bool st2 = device.CurrentCascadeStep >= 2;
            bool st3 = device.CurrentCascadeStep >= 3;

            await SendRelayCommandAsync(device.IpAddress, st1);
            if (!string.IsNullOrWhiteSpace(device.IpAddress2)) await SendRelayCommandAsync(device.IpAddress2, st2);
            if (!string.IsNullOrWhiteSpace(device.IpAddress3)) await SendRelayCommandAsync(device.IpAddress3, st3);
        }

        private async Task SendRelayCommandAsync(string ip, bool turnOn)
        {
            if (string.IsNullOrWhiteSpace(ip) || ip == "0.0.0.0") return;
            string actionGen1 = turnOn ? "on" : "off";
            string actionGen2 = turnOn ? "true" : "false";
            string actionTasmota = turnOn ? "On" : "Off"; 
            try { var res = await _httpClient.GetAsync($"http://{ip}/cm?cmnd=Power%20{actionTasmota}"); if (res.IsSuccessStatusCode) return; } catch { } 
            try { var res = await _httpClient.GetAsync($"http://{ip}/relay/0?turn={actionGen1}"); if (res.IsSuccessStatusCode) return; } catch { } 
            try { var res = await _httpClient.GetAsync($"http://{ip}/rpc/Switch.Set?id=0&on={actionGen2}"); if (res.IsSuccessStatusCode) return; } catch { } 
        }

        private async Task ApplyManualStateAsync(ShellyDevice device, bool turnOn)
        {
            if (device.SystemType == "heater" && device.DeviceModel == "mypv")
            {
                await SendMyPvCommandAsync(device.IpAddress, turnOn ? 3000 : 0); 
            }
            else if (device.SystemType == "wallbox")
            {
                int amp = turnOn ? 16 : 6;
                if (device.DeviceModel == "goe") await SendGoeCommandAsync(device.IpAddress, turnOn, amp);
                else if (device.DeviceModel == "keba") await SendKebaCommandAsync(device.IpAddress, turnOn, amp);
                else if (device.DeviceModel == "heidelberg") await SendHeidelbergCommandAsync(device.IpAddress, turnOn, amp);
                else if (device.DeviceModel == "openwb") await SendOpenWbCommandAsync(device.IpAddress, turnOn, amp, isV2: false);
                else if (device.DeviceModel == "openwb2") await SendOpenWbCommandAsync(device.IpAddress, turnOn, amp, isV2: true);
            }
            else
            {
                await SendSwitchCommandAsync(device, turnOn);
            }
        }

        private async Task SendMyPvCommandAsync(string ip, int watt)
        {
            if (string.IsNullOrWhiteSpace(ip) || ip == "0.0.0.0") return;
            // Wichtig: "data.jsn" ist bei myPV (AC-Thor / AC ELWA-E / ELWA 2) ein reiner Status-Endpunkt
            // (nur Lesen)! Hier stand sogar zusaetzlich "data.js" (falscher Dateiname, ohne "n").
            // Zum Steuern ist "setup.jsn" mit "devmode" (0=aus, 1=an) und "ptarget"
            // (Ziel-Netzbezugspunkt in Watt, negativ = so viel Ueberschuss soll noch eingespeist werden) noetig.
            try {
                string url = watt <= 0
                    ? $"http://{ip}/setup.jsn?devmode=0"
                    : $"http://{ip}/setup.jsn?devmode=1&ptarget=-{watt}";
                await _httpClient.GetAsync(url);
            } catch { } 
        }

        private async Task SendGoeCommandAsync(string ip, bool allowCharge, int ampere)
        {
            if (string.IsNullOrWhiteSpace(ip) || ip == "0.0.0.0") return;
            try {
                // go-eCharger API v2 (Standard seit ca. 2021, Hardware v2/v3):
                // amp = Ladestrom (6-32A), frc = forceState (0=Automatik, 1=Aus, 2=An/Erzwingen)
                // Vorher wurde faelschlich der alte v1-Parameter "alw" mit dem neuen v2-Pfad
                // "/api/set" gemischt - das wird von der Box ignoriert und haette nicht funktioniert.
                int frc = allowCharge ? 2 : 1;
                await _httpClient.GetAsync($"http://{ip}/api/set?amp={ampere}&frc={frc}");
            } catch { } 
        }

        private async Task SendKebaCommandAsync(string ip, bool allowCharge, int ampere)
        {
            if (string.IsNullOrWhiteSpace(ip) || ip == "0.0.0.0") return;
            try
            {
                using var udpClient = new UdpClient();
                var endpoint = new IPEndPoint(IPAddress.Parse(ip), 7090);
                int mA = ampere * 1000;
                byte[] currBytes = Encoding.ASCII.GetBytes($"curr {mA}");
                await udpClient.SendAsync(currBytes, currBytes.Length, endpoint);
                await Task.Delay(100); 
                int ena = allowCharge ? 1 : 0;
                byte[] enaBytes = Encoding.ASCII.GetBytes($"ena {ena}");
                await udpClient.SendAsync(enaBytes, enaBytes.Length, endpoint);
            }
            catch { }
        }

        private async Task SendHeidelbergCommandAsync(string ip, bool allowCharge, int ampere)
        {
            if (string.IsNullOrWhiteSpace(ip) || ip == "0.0.0.0") return;
            try
            {
                await Task.Run(() => 
                {
                    // Register 261 = Maximaler Ladestrom in 0.1A (Holding Register)
                    // Register 259 = Remote Lock: 0 = gesperrt, 1 = entriegelt (Ladefreigabe!)
                    // Achtung: Register 258 ist NICHT die Ladefreigabe, sondern nur die interne
                    // Standby-Stromsparfunktion - das wurde hier vorher faelschlich fuer die
                    // Freigabe verwendet, und der Strom wurde beim Ausschalten nie auf 0 gesetzt.
                    using var hClient = new ModbusTcpClient();
                    hClient.Connect(ip); 
                    int deciAmpere = allowCharge ? (ampere * 10) : 0; // Beim Ausschalten wirklich auf 0
                    byte[] currBytes = new byte[] { (byte)(deciAmpere >> 8), (byte)(deciAmpere & 0xFF) };
                    hClient.WriteSingleRegister(1, 261, currBytes); 
                    int lockVal = allowCharge ? 1 : 0; // Register 259 (Remote Lock)
                    byte[] lockBytes = new byte[] { (byte)(lockVal >> 8), (byte)(lockVal & 0xFF) };
                    hClient.WriteSingleRegister(1, 259, lockBytes); 
                    hClient.Disconnect();
                });
            }
            catch { }
        }

        private async Task SendOpenWbCommandAsync(string ip, bool allowCharge, int ampere, bool isV2 = false)
        {
            if (string.IsNullOrWhiteSpace(ip) || ip == "0.0.0.0") return;
            try
            {
                if (isV2)
                {
                    var openWbV2 = new OpenWbManagerV2(ip);
                    if (allowCharge)
                    {
                        await openWbV2.SetzeAmpere(ampere);
                        await openWbV2.StarteLaden();
                    }
                    else
                    {
                        await openWbV2.StoppeLaden();
                    }
                }
                else
                {
                    var openWb = new OpenWbManager(ip);
                    if (allowCharge)
                    {
                        await openWb.SetzeAmpere(ampere);
                        await openWb.StarteLaden();
                    }
                    else
                    {
                        await openWb.StoppeLaden();
                    }
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[FEHLER] OpenWB MQTT-Befehl fehlgeschlagen: {ex.Message}");
            }
        }

        private async Task SendSwitchCommandAsync(ShellyDevice device, bool turnOn)
        {
            if (device.DeviceModel == "custom")
            {
                string url = turnOn ? device.CustomOnUrl : device.CustomOffUrl;
                if (!string.IsNullOrWhiteSpace(url))
                {
                    try { await _httpClient.GetAsync(url); } catch { }
                }
                return;
            }
            await SendRelayCommandAsync(device.IpAddress, turnOn);
        }

        private async Task<bool?> FrageGeraeteStatusAbAsync(ShellyDevice device)
        {
            if (device.DeviceModel == "custom") return null;

            string ip = device.IpAddress;
            if (string.IsNullOrWhiteSpace(ip) || ip == "0.0.0.0") return null;

            try {
                var response = await _httpClient.GetAsync($"http://{ip}/cm?cmnd=Power");
                if (response.IsSuccessStatusCode) {
                    string content = await response.Content.ReadAsStringAsync();
                    if (content.Contains("\"POWER\":\"ON\"")) return true;
                    if (content.Contains("\"POWER\":\"OFF\"")) return false;
                }
            } catch { }

            try {
                var response = await _httpClient.GetAsync($"http://{ip}/relay/0");
                if (response.IsSuccessStatusCode) {
                    string content = await response.Content.ReadAsStringAsync();
                    if (content.Contains("\"ison\":true")) return true;
                    if (content.Contains("\"ison\":false")) return false;
                }
            } catch { }

            try {
                var response = await _httpClient.GetAsync($"http://{ip}/rpc/Switch.GetStatus?id=0");
                if (response.IsSuccessStatusCode) {
                    string content = await response.Content.ReadAsStringAsync();
                    if (content.Contains("\"output\":true")) return true;
                    if (content.Contains("\"output\":false")) return false;
                }
            } catch { }

            return null;
        }

        private (double Soc, double Voltage, double GridPower, double Pv, double AcLoad, double G1, double G2, double G3, double Ac1, double Ac2, double Ac3) ReadVictronData(ModbusTcpClient client)
        {
            double soc = 0; double voltage = 0; double gridPower = 0; double pv = 0; double acLoad = 0;
            short g1 = 0, g2 = 0, g3 = 0;
            ushort ac1 = 0, ac2 = 0, ac3 = 0;

            try { soc = SwapBytes(client.ReadHoldingRegisters<ushort>(100, 843, 1).ToArray()[0]); } catch { }
            try { voltage = SwapBytes(client.ReadHoldingRegisters<ushort>(100, 840, 1).ToArray()[0]) / 10.0; } catch { }
            
            try 
            {
                g1 = (short)SwapBytes(client.ReadHoldingRegisters<ushort>(100, 820, 1).ToArray()[0]);
                g2 = (short)SwapBytes(client.ReadHoldingRegisters<ushort>(100, 821, 1).ToArray()[0]);
                g3 = (short)SwapBytes(client.ReadHoldingRegisters<ushort>(100, 822, 1).ToArray()[0]);
                gridPower = g1 + g2 + g3;
            } 
            catch { }

            try { pv = SwapBytes(client.ReadHoldingRegisters<ushort>(100, 850, 1).ToArray()[0]); } catch { }
            
            try 
            {
                ac1 = SwapBytes(client.ReadHoldingRegisters<ushort>(100, 817, 1).ToArray()[0]);
                ac2 = SwapBytes(client.ReadHoldingRegisters<ushort>(100, 818, 1).ToArray()[0]);
                ac3 = SwapBytes(client.ReadHoldingRegisters<ushort>(100, 819, 1).ToArray()[0]);
                acLoad = ac1 + ac2 + ac3;
            } 
            catch { }
            
            return (soc, voltage, gridPower, pv, acLoad, g1, g2, g3, ac1, ac2, ac3);
        }

        private ushort SwapBytes(ushort val)
        {
            return (ushort)((val << 8) | (val >> 8));
        }
    }
}
