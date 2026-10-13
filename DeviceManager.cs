using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;

namespace VictronEasyManager
{
    public class AppSettings
    {
        public string VictronIp { get; set; } = "";
    }

    public class ShellyDevice
    {
        public string Id { get; set; } = Guid.NewGuid().ToString();

        // Priorität für die intelligente Steuerung (1 = höchste)
        public int Priority { get; set; } = 99;

        public string Name { get; set; } = "Neues Gerät";
        public string IpAddress { get; set; } = "";
        public string SystemType { get; set; } = "grid";
        public string DeviceModel { get; set; } = "switch";
        public string ControlMode { get; set; } = "grid";

        public double OnValue { get; set; } = 0;
        public double OffValue { get; set; } = 0;
        public int OnDelayMinutes { get; set; } = 0;
        public int OffDelayMinutes { get; set; } = 0;

        public string CustomOnUrl { get; set; } = "";
        public string CustomOffUrl { get; set; } = "";

        public string ManualOverride { get; set; } = "auto";
        public bool IsOn { get; set; } = false;

        // Kaskaden-Steuerung (3 Phasen)
        public string IpAddress2 { get; set; } = "";
        public string IpAddress3 { get; set; } = "";
        public double OnValue2 { get; set; } = 0;
        public double OnValue3 { get; set; } = 0;
        public int CurrentCascadeStep { get; set; } = 0;
    }

    public class DeviceManager
    {
        private static readonly JsonSerializerOptions JsonOpts = new JsonSerializerOptions { WriteIndented = true };

        private readonly object _lock = new object();
        private readonly string _devicesFile = "devices.json";
        private readonly string _settingsFile = "settings.json";
        private List<ShellyDevice> _devices = new List<ShellyDevice>();

        public AppSettings Settings { get; private set; } = new AppSettings();

        // Live-Werte
        public double LiveSoc { get; set; }
        public double LiveVoltage { get; set; }
        public double LiveGrid { get; set; }
        public double LivePv { get; set; }
        public double LiveAcLoad { get; set; }
        public double LiveBatteryPower { get; set; }

        public double LiveGridL1 { get; set; }
        public double LiveGridL2 { get; set; }
        public double LiveGridL3 { get; set; }

        public double LiveAcL1 { get; set; }
        public double LiveAcL2 { get; set; }
        public double LiveAcL3 { get; set; }

        // Zeitpunkt (UTC) der letzten erfolgreichen Victron-Messung
        public DateTime LastLiveUpdate { get; set; } = DateTime.MinValue;

        public DeviceManager()
        {
            LoadSettings();
            LoadDevices();
        }

        private static void WriteAtomic(string path, string content)
        {
            string tmp = path + ".tmp";
            File.WriteAllText(tmp, content);
            File.Move(tmp, path, overwrite: true);
        }

        private static void BackupCorrupt(string path)
        {
            try { File.Copy(path, path + ".corrupt", true); } catch { }
        }

        public void LoadSettings()
        {
            lock (_lock)
            {
                if (!File.Exists(_settingsFile)) return;
                try { Settings = JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(_settingsFile)) ?? new AppSettings(); }
                catch
                {
                    BackupCorrupt(_settingsFile);
                    Settings = new AppSettings();
                }
            }
        }

        public void SaveSettings()
        {
            lock (_lock)
            {
                WriteAtomic(_settingsFile, JsonSerializer.Serialize(Settings, JsonOpts));
            }
        }

        public void LoadDevices()
        {
            lock (_lock)
            {
                if (!File.Exists(_devicesFile)) return;
                try { _devices = JsonSerializer.Deserialize<List<ShellyDevice>>(File.ReadAllText(_devicesFile)) ?? new List<ShellyDevice>(); }
                catch
                {
                    // Backup statt stillem Datenverlust
                    BackupCorrupt(_devicesFile);
                    _devices = new List<ShellyDevice>();
                }
            }
        }

        public void SaveDevices()
        {
            lock (_lock)
            {
                WriteAtomic(_devicesFile, JsonSerializer.Serialize(_devices, JsonOpts));
            }
        }

        // Snapshot: gleiche Objekte, aber eigene Liste -> sicher zu iterieren
        public List<ShellyDevice> GetAllDevices()
        {
            lock (_lock) { return _devices.ToList(); }
        }

        public void AddDevice(ShellyDevice device)
        {
            lock (_lock)
            {
                _devices.Add(device);
                SaveDevices();
            }
        }

        public void DeleteDevice(string id)
        {
            lock (_lock)
            {
                _devices.RemoveAll(d => d.Id == id);
                SaveDevices();
            }
        }
    }
}
