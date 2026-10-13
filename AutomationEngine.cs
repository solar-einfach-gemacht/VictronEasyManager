using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using System.Text;
using System.Threading.Tasks;
using FluentModbus;

namespace VictronEasyManager
{
    public class AutomationEngine
    {
        // ---- Konfiguration ----
        private const double WattPerAmpere = 690.0;   // 3-phasig (3 x 230 V). Bei 1-phasigem Laden: 230.0
        private const int MaxAmpere = 16;
        private const int MaxHeaterWatt = 3000;
        private const double SocHysteresis = 2.0;      // Kaskade (SOC-Modus): Stufe runter bei 2 % unter Einschaltwert
        private static readonly TimeSpan RefreshInterval = TimeSpan.FromSeconds(60);

        // my-PV: true  = http://IP/control.html?power=WATT (Steuerquelle im Gerät auf "HTTP" stellen)
        //        false = http://IP/setup.jsn?devmode=..&ptarget=.. (alte Variante)
        // Bitte an deinem Gerät testen und ggf. umstellen.
        private const bool MyPvUseControlHtml = true;

        private readonly DeviceManager _deviceManager;

        private readonly Dictionary<string, DateTime> _pendingOnTimers = new Dictionary<string, DateTime>();
        private readonly Dictionary<string, DateTime> _pendingOffTimers = new Dictionary<string, DateTime>();

        private readonly Dictionary<string, int> _deviceTargets = new Dictionary<string, int>();
        private readonly Dictionary<string, DateTime> _lastAdjustments = new Dictionary<string, DateTime>();

        // Zuletzt gesendete Leistung (W) je Gerät -> "eigener Verbrauch" im Überschuss
        private readonly Dictionary<string, double> _lastPower = new Dictionary<string, double>();

        // Letzter gesendeter Befehl je Gerät (nur bei Änderung / alle 60 s erneut senden)
        private readonly Dictionary<string, (string State, DateTime At)> _lastCmd = new Dictionary<string, (string, DateTime)>();

        // Steckdosen-Status: erkanntes Protokoll + Offline-Sperre
        private readonly Dictionary<string, int> _protocol = new Dictionary<string, int>();
        private readonly Dictionary<string, DateTime> _offlineUntil = new Dictionary<string, DateTime>();

        private static readonly HttpClient _httpClient = new HttpClient { Timeout = TimeSpan.FromSeconds(3) };

        private sealed class Ctx
        {
            public double Soc;
            public double BatteryPower;   // + laden / - entladen
            public double Surplus;        // + Einspeisung / - Netzbezug (vorzeichenbehaftet!)
        }

        private sealed record VictronData(
            double Soc, double Voltage, double Grid, double Pv, double AcLoad,
            double G1, double G2, double G3, double Ac1, double Ac2, double Ac3, double BatteryPower);

        public AutomationEngine(DeviceManager deviceManager)
        {
            _deviceManager = deviceManager;
        }

        // =====================================================================
        // HAUPTSCHLEIFE
        // =====================================================================
        public async Task StartAsync()
        {
            Console.WriteLine("==================================================");
            Console.WriteLine("  PVManager Gehirn (Intelligente Logik) gestartet!");
            Console.WriteLine("==================================================");

            using var client = new ModbusTcpClient();
            string? connectedIp = null;

            while (true)
            {
                try
                {
                    string victronIp = _deviceManager.Settings.VictronIp;

                    if (string.IsNullOrWhiteSpace(victronIp))
                    {
                        Console.WriteLine("[INFO] Warte auf Victron Cerbo IP... (Bitte im Dashboard eintragen!)");
                        await Task.Delay(5000);
                        continue;
                    }

                    // IP im Dashboard geändert -> neu verbinden
                    if (client.IsConnected && connectedIp != victronIp) client.Disconnect();
                    if (!client.IsConnected)
                    {
                        Console.WriteLine($"[VERBINDUNG] Verbinde mit Victron unter: {victronIp} ...");
                        client.Connect(victronIp);
                        connectedIp = victronIp;
                    }

                    // Wirft bei Lesefehlern eine Exception -> Zyklus wird übersprungen (kein Schalten mit Nullwerten)
                    var v = ReadVictronData(client);

                    _deviceManager.LiveSoc = v.Soc;
                    _deviceManager.LiveVoltage = v.Voltage;
                    _deviceManager.LiveGrid = v.Grid;
                    _deviceManager.LivePv = v.Pv;
                    _deviceManager.LiveAcLoad = v.AcLoad;
                    _deviceManager.LiveBatteryPower = v.BatteryPower;
                    _deviceManager.LiveGridL1 = v.G1;
                    _deviceManager.LiveGridL2 = v.G2;
                    _deviceManager.LiveGridL3 = v.G3;
                    _deviceManager.LiveAcL1 = v.Ac1;
                    _deviceManager.LiveAcL2 = v.Ac2;
                    _deviceManager.LiveAcL3 = v.Ac3;
                    _deviceManager.LastLiveUpdate = DateTime.UtcNow;

                    Console.WriteLine($"[LIVE] SOC: {v.Soc}% | PV: {v.Pv}W | AC-Last: {v.AcLoad}W | Netz: {v.Grid}W | Akku: {v.BatteryPower}W");

                    var ctx = new Ctx
                    {
                        Soc = v.Soc,
                        BatteryPower = v.BatteryPower,
                        Surplus = -v.Grid   // negativ = Netzbezug
                    };

                    var devices = _deviceManager.GetAllDevices().OrderBy(d => d.Priority).ToList();
                    PruneState(devices);

                    foreach (var device in devices)
                    {
                        try { await ProcessDeviceAsync(device, ctx); }
                        catch (Exception ex) { Console.WriteLine($"[FEHLER] Gerät '{device.Name}': {ex.Message}"); }
                    }
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"[FEHLER] Modbus-Verbindung unterbrochen: {ex.Message}");
                    try { if (client.IsConnected) client.Disconnect(); } catch { }
                }

                await Task.Delay(5000);
            }
        }

        private async Task ProcessDeviceAsync(ShellyDevice device, Ctx c)
        {
            if (device.ManualOverride == "on" || device.ManualOverride == "off")
            {
                await HandleManualAsync(device, device.ManualOverride == "on");
                return;
            }

            switch (device.SystemType)
            {
                case "cascade": ProcessCascade(device, c); break;
                case "heater" when device.DeviceModel == "mypv": await ProcessMyPvAsync(device, c); break;
                case "wallbox": await ProcessWallboxAsync(device, c); break;
                default: await ProcessSwitchAsync(device, c); break;
            }
        }

        // =====================================================================
        // HILFSFUNKTIONEN
        // =====================================================================
        private void PruneState(List<ShellyDevice> devices)
        {
            var ids = devices.Select(d => d.Id).ToHashSet();
            Prune(_pendingOnTimers, ids);
            Prune(_pendingOffTimers, ids);
            Prune(_deviceTargets, ids);
            Prune(_lastAdjustments, ids);
            Prune(_lastPower, ids);
            Prune(_lastCmd, ids);
            Prune(_protocol, ids);
            Prune(_offlineUntil, ids);
        }

        private static void Prune<T>(Dictionary<string, T> dict, HashSet<string> ids)
        {
            foreach (var key in dict.Keys.Where(k => !ids.Contains(k)).ToList()) dict.Remove(key);
        }

        // true, sobald die Verzögerung abgelaufen ist (bei 0 Minuten sofort)
        private bool DelayElapsed(Dictionary<string, DateTime> timers, string id, int minutes)
        {
            if (minutes <= 0) { timers.Remove(id); return true; }
            if (!timers.TryGetValue(id, out var start)) { timers[id] = DateTime.UtcNow; return false; }
            if ((DateTime.UtcNow - start).TotalMinutes >= minutes) { timers.Remove(id); return true; }
            return false;
        }

        // Prüft, ob ein Zustandswechsel jetzt fällig ist (desired == null -> halten)
        private bool StateChangeDue(ShellyDevice device, bool? desired, int onDelay, int offDelay)
        {
            if (desired == true && !device.IsOn)
            {
                _pendingOffTimers.Remove(device.Id);
                return DelayElapsed(_pendingOnTimers, device.Id, onDelay);
            }
            if (desired == false && device.IsOn)
            {
                _pendingOnTimers.Remove(device.Id);
                return DelayElapsed(_pendingOffTimers, device.Id, offDelay);
            }
            _pendingOnTimers.Remove(device.Id);
            _pendingOffTimers.Remove(device.Id);
            return false;
        }

        // Befehl nur senden, wenn er sich geändert hat (oder alle 60 s als Auffrischung)
        private bool ShouldSend(string id, string state, bool refresh = true)
        {
            var now = DateTime.UtcNow;
            if (_lastCmd.TryGetValue(id, out var last) && last.State == state && (!refresh || now - last.At < RefreshInterval))
                return false;
            _lastCmd[id] = (state, now);
            return true;
        }

        private static bool SocAllowed(ShellyDevice d, double soc)
            => soc >= d.OnValue || (soc >= d.OffValue && d.IsOn);

        // Stufenweises Nachregeln (alle 15 s) anhand der Batterieleistung
        private int AdjustTarget(string id, int start, int step, int min, int max, double charge, double upThreshold, double downThreshold)
        {
            if (!_deviceTargets.TryGetValue(id, out int target)) target = start;
            if (!_lastAdjustments.TryGetValue(id, out var last)) last = DateTime.UtcNow.AddSeconds(-20);

            if ((DateTime.UtcNow - last).TotalSeconds >= 15)
            {
                int n = target;
                if (charge > upThreshold) n += step;
                else if (charge < downThreshold) n -= step;
                n = Math.Clamp(n, min, max);
                if (n != target) { target = n; _lastAdjustments[id] = DateTime.UtcNow; }
            }
            _deviceTargets[id] = target;
            return target;
        }

        // =====================================================================
        // MANUELLE ÜBERSTEUERUNG
        // =====================================================================
        private async Task HandleManualAsync(ShellyDevice device, bool on)
        {
            if (device.SystemType == "cascade")
            {
                int step = on ? 3 : 0;
                if (device.CurrentCascadeStep != step) { device.CurrentCascadeStep = step; _deviceManager.SaveDevices(); }
                if (ShouldSend(device.Id, $"step{step}")) _ = ApplyCascadeStateAsync(device);
                return;
            }

            if (device.IsOn != on) { device.IsOn = on; _deviceManager.SaveDevices(); }

            if (device.SystemType == "heater") _lastPower[device.Id] = on ? MaxHeaterWatt : 0;
            else if (device.SystemType == "wallbox") _lastPower[device.Id] = on ? MaxAmpere * WattPerAmpere : 0;

            // Eigene HTTP-URLs könnten Toggles sein -> dort nicht auffrischen
            bool refresh = device.DeviceModel != "custom";
            if (ShouldSend(device.Id, $"manual:{on}", refresh))
                await ApplyManualStateAsync(device, on);
        }

        private async Task ApplyManualStateAsync(ShellyDevice device, bool turnOn)
        {
            if (device.SystemType == "heater" && device.DeviceModel == "mypv")
                await SendMyPvCommandAsync(device.IpAddress, turnOn ? MaxHeaterWatt : 0);
            else if (device.SystemType == "wallbox")
                await SendWallboxAsync(device, turnOn, turnOn ? MaxAmpere : 6);
            else
                await SendSwitchCommandAsync(device, turnOn);
        }

        // =====================================================================
        // KASKADE (3-Phasen-Heizstab)
        // =====================================================================
        private void ProcessCascade(ShellyDevice device, Ctx c)
        {
            int current = device.CurrentCascadeStep;
            int desired = current;

            if (device.ControlMode == "soc")
            {
                // Hochschalten
                if (c.Soc >= device.OnValue3) desired = 3;
                else if (c.Soc >= device.OnValue2 && desired < 2) desired = 2;
                else if (c.Soc >= device.OnValue && desired < 1) desired = 1;

                // Runterschalten mit Hysterese (verhindert Flattern zwischen den Stufen)
                if (desired >= 3 && c.Soc < device.OnValue3 - SocHysteresis) desired = 2;
                if (desired >= 2 && c.Soc < device.OnValue2 - SocHysteresis) desired = 1;
                if (desired >= 1 && c.Soc < device.OffValue) desired = 0;
            }
            else // Netz-Modus (c.Surplus ist vorzeichenbehaftet)
            {
                if (current == 0)
                {
                    if (c.Surplus >= device.OnValue) { desired = 1; c.Surplus -= device.OnValue; }
                }
                else if (current == 1)
                {
                    if (c.Surplus >= device.OnValue2) { desired = 2; c.Surplus -= device.OnValue2; }
                    else if (c.Surplus < device.OffValue) desired = 0;
                }
                else if (current == 2)
                {
                    if (c.Surplus >= device.OnValue3) { desired = 3; c.Surplus -= device.OnValue3; }
                    else if (c.Surplus < device.OffValue) desired = 1;
                }
                else
                {
                    if (c.Surplus < device.OffValue) desired = 2;
                }
            }

            if (desired > current)
            {
                _pendingOffTimers.Remove(device.Id);
                if (DelayElapsed(_pendingOnTimers, device.Id, device.OnDelayMinutes))
                {
                    device.CurrentCascadeStep = desired;
                    _deviceManager.SaveDevices();
                }
            }
            else if (desired < current)
            {
                _pendingOnTimers.Remove(device.Id);
                if (DelayElapsed(_pendingOffTimers, device.Id, device.OffDelayMinutes))
                {
                    device.CurrentCascadeStep = desired;
                    _deviceManager.SaveDevices();
                }
            }
            else
            {
                _pendingOnTimers.Remove(device.Id);
                _pendingOffTimers.Remove(device.Id);
            }

            // Bei Änderung sofort, sonst alle 60 s zur Synchronisation
            if (ShouldSend(device.Id, $"step{device.CurrentCascadeStep}"))
                _ = ApplyCascadeStateAsync(device);
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

        // =====================================================================
        // HEIZSTAB (stufenlos, my-PV)
        // =====================================================================
        private async Task ProcessMyPvAsync(ShellyDevice device, Ctx c)
        {
            _lastPower.TryGetValue(device.Id, out double lastPower);
            double own = device.IsOn ? lastPower : 0;   // eigener Verbrauch steckt schon in der Netzmessung
            int targetWatt = 0;

            if (device.ControlMode == "soc")
            {
                if (c.Soc >= device.OnValue) targetWatt = MaxHeaterWatt;
                else if (c.Soc < device.OffValue) targetWatt = 0;
                else targetWatt = device.IsOn ? MaxHeaterWatt / 2 : 0;
            }
            else if (device.ControlMode == "soc_pv")
            {
                if (SocAllowed(device, c.Soc))
                    targetWatt = AdjustTarget(device.Id, 0, 200, 0, MaxHeaterWatt, c.BatteryPower, 400, -200);
                else
                    _deviceTargets[device.Id] = 0;
            }
            else // Netz-Modus
            {
                targetWatt = (int)Math.Clamp(Math.Floor(c.Surplus + own), 0, MaxHeaterWatt);
                targetWatt = targetWatt / 50 * 50;      // 50-W-Raster gegen ständiges Flattern
                c.Surplus -= (targetWatt - own);        // nur die Differenz reservieren
            }

            // Verzögerungen nur im Netz-Modus (im UI nur dort einstellbar)
            int onDelay = device.ControlMode == "grid" ? device.OnDelayMinutes : 0;
            int offDelay = device.ControlMode == "grid" ? device.OffDelayMinutes : 0;

            bool desiredHeat = targetWatt > 0;
            if (StateChangeDue(device, desiredHeat, onDelay, offDelay))
            {
                device.IsOn = desiredHeat;
                _deviceManager.SaveDevices();
            }

            // Ausschaltverzögerung läuft: letzte Leistung halten
            if (!desiredHeat && device.IsOn) targetWatt = (int)lastPower;

            int finalWatt = device.IsOn ? targetWatt : 0;
            _lastPower[device.Id] = finalWatt;

            if (ShouldSend(device.Id, finalWatt.ToString()))
                await SendMyPvCommandAsync(device.IpAddress, finalWatt);
        }

        // =====================================================================
        // WALLBOX
        // =====================================================================
        private async Task ProcessWallboxAsync(ShellyDevice device, Ctx c)
        {
            _lastPower.TryGetValue(device.Id, out double lastPower);
            double own = device.IsOn ? lastPower : 0;

            bool allow = false;
            int amp = 6;

            if (device.ControlMode == "soc")
            {
                if (c.Soc >= device.OnValue) { allow = true; amp = MaxAmpere; }
                else if (c.Soc < device.OffValue) { allow = false; }
                else { allow = device.IsOn; amp = 10; }
            }
            else if (device.ControlMode == "soc_pv")
            {
                allow = SocAllowed(device, c.Soc);
                if (allow) amp = AdjustTarget(device.Id, 6, 1, 6, MaxAmpere, c.BatteryPower, 500, -200);
                else _deviceTargets[device.Id] = 6;
            }
            else // Netz-Modus
            {
                int a = (int)Math.Floor((c.Surplus + own) / WattPerAmpere);
                if (a >= 6)
                {
                    amp = Math.Min(a, MaxAmpere);
                    allow = true;
                    c.Surplus -= (amp * WattPerAmpere - own);
                }
                else { allow = false; amp = 6; }
            }

            int onDelay = device.ControlMode == "grid" ? device.OnDelayMinutes : 0;
            int offDelay = device.ControlMode == "grid" ? device.OffDelayMinutes : 0;

            if (StateChangeDue(device, allow, onDelay, offDelay))
            {
                device.IsOn = allow;
                _deviceManager.SaveDevices();
            }

            // Ausschaltverzögerung läuft: mit Minimalstrom weiterladen
            if (!allow && device.IsOn) { allow = true; amp = 6; }

            bool finalAllow = device.IsOn;
            int finalAmp = finalAllow ? amp : 6;
            _lastPower[device.Id] = finalAllow ? finalAmp * WattPerAmpere : 0;

            if (ShouldSend(device.Id, $"{finalAllow}:{finalAmp}"))
                await SendWallboxAsync(device, finalAllow, finalAmp);
        }

        private async Task SendWallboxAsync(ShellyDevice device, bool allow, int amp)
        {
            switch (device.DeviceModel)
            {
                case "goe": await SendGoeCommandAsync(device.IpAddress, allow, amp); break;
                case "keba": await SendKebaCommandAsync(device.IpAddress, allow, amp); break;
                case "heidelberg": await SendHeidelbergCommandAsync(device.IpAddress, allow, amp); break;
                case "openwb": await SendOpenWbCommandAsync(device.IpAddress, allow, amp, isV2: false); break;
                case "openwb2": await SendOpenWbCommandAsync(device.IpAddress, allow, amp, isV2: true); break;
            }
        }

        // =====================================================================
        // STECKDOSEN (Netz / Insel)
        // =====================================================================
        private async Task ProcessSwitchAsync(ShellyDevice device, Ctx c)
        {
            bool? real = await FrageGeraeteStatusAbAsync(device);
            if (real.HasValue && device.IsOn != real.Value)
            {
                device.IsOn = real.Value;
                _deviceManager.SaveDevices();
            }

            // Gerät nicht erreichbar -> nicht schalten, Sperre läuft (siehe FrageGeraeteStatusAbAsync)
            if (real == null && device.DeviceModel != "custom") return;

            bool? desired = null;

            if (device.SystemType == "grid")
            {
                if (c.Surplus >= device.OnValue)
                {
                    desired = true;
                    // Nur reservieren, wenn es noch nicht läuft (sonst steckt der Verbrauch schon in der Messung)
                    if (!device.IsOn) c.Surplus -= device.OnValue;
                }
                else if (c.Surplus < device.OffValue) desired = false;
            }
            else if (device.SystemType == "island")
            {
                if (c.Soc >= device.OnValue) desired = true;
                else if (c.Soc < device.OffValue) desired = false;
            }
            else return;

            if (StateChangeDue(device, desired, device.OnDelayMinutes, device.OffDelayMinutes))
            {
                bool target = desired!.Value;
                if (await SendSwitchCommandAsync(device, target))
                {
                    device.IsOn = target;
                    _deviceManager.SaveDevices();
                }
            }
        }

        private async Task<bool> SendSwitchCommandAsync(ShellyDevice device, bool turnOn)
        {
            if (device.DeviceModel == "custom")
            {
                string url = turnOn ? device.CustomOnUrl : device.CustomOffUrl;
                if (string.IsNullOrWhiteSpace(url)) return true;
                try { var res = await _httpClient.GetAsync(url); return res.IsSuccessStatusCode; }
                catch { return false; }
            }
            return await SendRelayCommandAsync(device.IpAddress, turnOn);
        }

        private async Task<bool> SendRelayCommandAsync(string ip, bool turnOn)
        {
            if (string.IsNullOrWhiteSpace(ip) || ip == "0.0.0.0") return false;

            string[] urls =
            {
                $"http://{ip}/cm?cmnd=Power%20{(turnOn ? "On" : "Off")}",   // Tasmota
                $"http://{ip}/relay/0?turn={(turnOn ? "on" : "off")}",      // Shelly Gen1
                $"http://{ip}/rpc/Switch.Set?id=0&on={(turnOn ? "true" : "false")}" // Shelly Gen2+
            };

            foreach (var url in urls)
            {
                try { var res = await _httpClient.GetAsync(url); if (res.IsSuccessStatusCode) return true; }
                catch { }
            }
            return false;
        }

        private async Task<bool?> FrageGeraeteStatusAbAsync(ShellyDevice device)
        {
            if (device.DeviceModel == "custom") return null;

            string ip = device.IpAddress;
            if (string.IsNullOrWhiteSpace(ip) || ip == "0.0.0.0") return null;

            // Offline-Sperre: nicht jede Runde neu in Timeouts laufen
            if (_offlineUntil.TryGetValue(device.Id, out var until) && DateTime.UtcNow < until) return null;

            if (_protocol.TryGetValue(device.Id, out int known))
            {
                var r = await TryStatusAsync(ip, known);
                if (r.HasValue) return r;
                _protocol.Remove(device.Id);   // beim nächsten Mal neu erkennen
            }
            else
            {
                for (int p = 0; p < 3; p++)
                {
                    var r = await TryStatusAsync(ip, p);
                    if (r.HasValue) { _protocol[device.Id] = p; return r; }
                }
            }

            _offlineUntil[device.Id] = DateTime.UtcNow.AddSeconds(45);
            return null;
        }

        private static bool? ParseState(string content, string onToken, string offToken)
            => content.Contains(onToken) ? true : content.Contains(offToken) ? false : (bool?)null;

        private async Task<bool?> TryStatusAsync(string ip, int protocol)
        {
            try
            {
                string url = protocol switch
                {
                    0 => $"http://{ip}/cm?cmnd=Power",
                    1 => $"http://{ip}/relay/0",
                    _ => $"http://{ip}/rpc/Switch.GetStatus?id=0"
                };
                var res = await _httpClient.GetAsync(url);
                if (!res.IsSuccessStatusCode) return null;
                string content = await res.Content.ReadAsStringAsync();

                return protocol switch
                {
                    0 => ParseState(content, "\"POWER\":\"ON\"", "\"POWER\":\"OFF\""),
                    1 => ParseState(content, "\"ison\":true", "\"ison\":false"),
                    _ => ParseState(content, "\"output\":true", "\"output\":false")
                };
            }
            catch { return null; }
        }

        // =====================================================================
        // GERÄTE-TREIBER
        // =====================================================================
        private async Task SendMyPvCommandAsync(string ip, int watt)
        {
            if (string.IsNullOrWhiteSpace(ip) || ip == "0.0.0.0") return;
            try
            {
                string url;
                if (MyPvUseControlHtml)
                    url = $"http://{ip}/control.html?power={Math.Max(0, watt)}";
                else
                    url = watt <= 0
                        ? $"http://{ip}/setup.jsn?devmode=0"
                        : $"http://{ip}/setup.jsn?devmode=1&ptarget=-{watt}";
                await _httpClient.GetAsync(url);
            }
            catch { }
        }

        private async Task SendGoeCommandAsync(string ip, bool allowCharge, int ampere)
        {
            if (string.IsNullOrWhiteSpace(ip) || ip == "0.0.0.0") return;
            try
            {
                // go-eCharger API v2: amp = Ladestrom, frc = forceState (1 = Aus, 2 = An)
                int frc = allowCharge ? 2 : 1;
                await _httpClient.GetAsync($"http://{ip}/api/set?amp={ampere}&frc={frc}");
            }
            catch { }
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
                    // Register 261 = max. Ladestrom in 0.1 A, Register 259 = Remote Lock (1 = Ladefreigabe)
                    using var hClient = new ModbusTcpClient();
                    hClient.Connect(ip);
                    int deciAmpere = allowCharge ? (ampere * 10) : 0;
                    byte[] currBytes = new byte[] { (byte)(deciAmpere >> 8), (byte)(deciAmpere & 0xFF) };
                    hClient.WriteSingleRegister(1, 261, currBytes);
                    int lockVal = allowCharge ? 1 : 0;
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
                    if (allowCharge) { await openWbV2.SetzeAmpere(ampere); await openWbV2.StarteLaden(); }
                    else await openWbV2.StoppeLaden();
                }
                else
                {
                    var openWb = new OpenWbManager(ip);
                    if (allowCharge) { await openWb.SetzeAmpere(ampere); await openWb.StarteLaden(); }
                    else await openWb.StoppeLaden();
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[FEHLER] OpenWB MQTT-Befehl fehlgeschlagen: {ex.Message}");
            }
        }

        // =====================================================================
        // VICTRON AUSLESEN (ohne try/catch: Fehler müssen nach oben durchschlagen)
        // =====================================================================
        private VictronData ReadVictronData(ModbusTcpClient client)
        {
            ushort[] ac = client.ReadHoldingRegisters<ushort>(100, 817, 6).ToArray();   // 817-819 AC-Last, 820-822 Netz
            ushort[] bat = client.ReadHoldingRegisters<ushort>(100, 840, 4).ToArray();  // 840 Spannung, 842 Leistung, 843 SOC
            ushort pvRaw = client.ReadHoldingRegisters<ushort>(100, 850, 1).ToArray()[0];

            double ac1 = SwapBytes(ac[0]), ac2 = SwapBytes(ac[1]), ac3 = SwapBytes(ac[2]);
            double g1 = (short)SwapBytes(ac[3]), g2 = (short)SwapBytes(ac[4]), g3 = (short)SwapBytes(ac[5]);

            double voltage = SwapBytes(bat[0]) / 10.0;
            double battPower = (short)SwapBytes(bat[2]);   // + laden / - entladen
            double soc = SwapBytes(bat[3]);

            return new VictronData(soc, voltage, g1 + g2 + g3, SwapBytes(pvRaw), ac1 + ac2 + ac3,
                                   g1, g2, g3, ac1, ac2, ac3, battPower);
        }

        private static ushort SwapBytes(ushort val)
        {
            return (ushort)((val << 8) | (val >> 8));
        }
    }
}
