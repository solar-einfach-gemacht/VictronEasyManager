using System;
using System.Collections.Generic;
using System.IO;
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
        
        // --- NEU: Priorität für die intelligente Steuerung ---
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

        public double LiveGridL1 { get; set; }
        public double LiveGridL2 { get; set; }
        public double LiveGridL3 { get; set; }
        
        public double LiveAcL1 { get; set; }
        public double LiveAcL2 { get; set; }
        public double LiveAcL3 { get; set; }

        public DeviceManager()
        {
            LoadSettings();
            LoadDevices();
        }

        public void LoadSettings()
        {
            if (File.Exists(_settingsFile))
            {
                try { Settings = JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(_settingsFile)) ?? new AppSettings(); }
                catch { Settings = new AppSettings(); }
            }
        }

        public void SaveSettings()
        {
            File.WriteAllText(_settingsFile, JsonSerializer.Serialize(Settings, new JsonSerializerOptions { WriteIndented = true }));
        }

        public void LoadDevices()
        {
            if (File.Exists(_devicesFile))
            {
                try { _devices = JsonSerializer.Deserialize<List<ShellyDevice>>(File.ReadAllText(_devicesFile)) ?? new List<ShellyDevice>(); }
                catch { _devices = new List<ShellyDevice>(); }
            }
        }

        public void SaveDevices()
        {
            File.WriteAllText(_devicesFile, JsonSerializer.Serialize(_devices, new JsonSerializerOptions { WriteIndented = true }));
        }

        public List<ShellyDevice> GetAllDevices() => _devices;

        public void AddDevice(ShellyDevice device)
        {
            _devices.Add(device);
            SaveDevices();
        }

        public void DeleteDevice(string id)
        {
            _devices.RemoveAll(d => d.Id == id);
            SaveDevices();
        }
    }
}
