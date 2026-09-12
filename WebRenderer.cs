using System;
using System.Net;
using System.Text;
using System.Threading.Tasks;
using System.Linq; 
using System.Diagnostics;
using System.Collections.Generic; 

namespace VictronEasyManager
{
    public class WebRenderer
    {
        private readonly DeviceManager _deviceManager;

        public WebRenderer(DeviceManager deviceManager)
        {
            _deviceManager = deviceManager;
        }

        public async Task StartAsync()
        {
            int port = 80; 
            string url = $"http://+:{port}/";
            string browserUrl = "http://localhost/"; 
            HttpListener listener = new HttpListener();
            
            try
            {
                listener.Prefixes.Add(url);
                listener.Start();
                Console.WriteLine("==================================================");
                Console.WriteLine("  PVManager Webserver läuft auf http://localhost");
                Console.WriteLine("==================================================");
            }
            catch
            {
                port = 5000;
                url = $"http://localhost:{port}/";
                browserUrl = url; 
                listener = new HttpListener();
                listener.Prefixes.Add(url);
                listener.Start();
                Console.WriteLine("==================================================");
                Console.WriteLine($"  PVManager Webserver (Test) läuft auf {url}");
                Console.WriteLine("==================================================");
            }

            try
            {
                Process.Start(new ProcessStartInfo(browserUrl) { UseShellExecute = true });
            }
            catch { }

            while (true)
            {
                try 
                {
                    var context = await listener.GetContextAsync();
                    _ = HandleRequestAsync(context); 
                }
                catch { break; }
            }
        }

        private async Task HandleRequestAsync(HttpListenerContext context)
        {
            var request = context.Request;
            var response = context.Response;
            string path = request.Url?.AbsolutePath ?? "/";
            string htmlResponse = "";

            // --- NEU: Hilfsfunktion für das dynamische Prioritäten-Dropdown ---
            string GetPriorityDropdown(int? currentPriority = null, string excludeDeviceId = null)
            {
                var usedPriorities = _deviceManager.GetAllDevices()
                    .Where(d => excludeDeviceId == null || d.Id != excludeDeviceId)
                    .Select(d => d.Priority).ToList();

                var sb = new StringBuilder();
                sb.Append("<label>Priorität (1 = Höchste, wird zuerst bedient):</label><select name='priority' required>");
                for (int i = 1; i <= 10; i++) // Erlaubt max 10 Geräte
                {
                    bool isUsed = usedPriorities.Contains(i);
                    string disabled = isUsed ? "disabled" : "";
                    string selected = (currentPriority.HasValue && currentPriority.Value == i) ? "selected" : "";
                    string usedText = isUsed ? " (Bereits vergeben)" : "";
                    
                    sb.Append($"<option value='{i}' {disabled} {selected}>Priorität {i}{usedText}</option>");
                }
                sb.Append("</select>");
                return sb.ToString();
            }
            // ------------------------------------------------------------------

            try
            {
                if (path == "/")
                {
                    var devices = _deviceManager.GetAllDevices()
                        .OrderBy(d => d.Priority) // Geräte auf dem Dashboard nach Prio sortieren
                        .ToList();
                        
                    StringBuilder deviceListHtml = new StringBuilder();

                    if (devices.Count == 0)
                    {
                        deviceListHtml.Append("<p style='color: #666; margin: 30px 0;'>Noch keine Geräte angelegt.</p>");
                    }
                    else
                    {
                        foreach (var d in devices)
                        {
                            string status = d.IsOn ? "<span style='color: green;'>🟢 AKTIV</span>" : "<span style='color: red;'>🔴 INAKTIV</span>";
                            
                            string modeName = "";
                            string ruleText = "";

                            if (d.SystemType == "cascade")
                            {
                                status = $"<span style='color: #0078D7; font-weight: bold;'>🌊 Stufe {d.CurrentCascadeStep} aktiv</span>";
                                modeName = "🌡️ 3-Phasen Kaskade";
                                string unit = d.ControlMode == "soc" ? "%" : "W";
                                string modeStr = d.ControlMode == "soc" ? "Batterie (SOC)" : "Netz-Überschuss";
                                ruleText = $"<b>Regelung nach:</b> {modeStr}<br><b>Stufe 1 ab:</b> {d.OnValue} {unit} <br> <b>Stufe 2 ab:</b> {d.OnValue2} {unit} <br> <b>Stufe 3 ab:</b> {d.OnValue3} {unit} <br> <b>Aus (Stufe runter) &lt;</b> {d.OffValue} {unit}";
                            }
                            else if (d.SystemType == "wallbox")
                            {
                                string wBoxName = d.DeviceModel == "goe" ? "go-e Charger" : (d.DeviceModel == "keba" ? "KEBA KeContact" : (d.DeviceModel == "openwb" ? "OpenWB Serie 1" : (d.DeviceModel == "openwb2" ? "OpenWB Serie 2" : "Heidelberg Energy Control")));
                                modeName = "🚗 " + wBoxName;
                                
                                string rText = d.ControlMode == "soc" ? "Batteriestand (Feste Stufen)" : (d.ControlMode == "soc_pv" ? "Batteriestand + PV-Überschuss" : "Netz-Überschuss");
                                string delayText = d.ControlMode == "grid" ? $"<br><small>Verzögerung: {d.OnDelayMinutes} Min Ein / {d.OffDelayMinutes} Min Aus</small>" : "";
                                ruleText = $"<b>Regelung nach:</b> {rText}<br><i>Lädt stufenlos basierend auf Vorgabe.</i>{delayText}";
                            }
                            else if (d.SystemType == "heater")
                            {
                                modeName = "🔥 " + (d.DeviceModel == "mypv" ? "my-PV Heizstab" : "Heizstab");
                                string rText = d.ControlMode == "soc" ? "Batteriestand (Feste Stufen)" : (d.ControlMode == "soc_pv" ? "Batteriestand + PV-Überschuss" : "Netz-Überschuss");
                                string delayText = d.ControlMode == "grid" ? $"<br><small>Verzögerung: {d.OnDelayMinutes} Min Ein / {d.OffDelayMinutes} Min Aus</small>" : "";
                                ruleText = $"<b>Regelung nach:</b> {rText}<br><i>Wandelt Energie stufenlos in Wärme um.</i>{delayText}";
                            }
                            else
                            {
                                string typeName = d.DeviceModel == "custom" ? " (Benutzerdefiniert)" : "";
                                modeName = (d.SystemType == "grid" ? "⚡ Überschuss-Regel" : "🔋 Batterie-Regel") + typeName;
                                
                                ruleText = d.SystemType == "grid" 
                                    ? $"<b>Ein:</b> Einspeisung > {d.OnValue} W ({d.OnDelayMinutes} Min) <br> <b>Aus:</b> Einspeisung &lt; {d.OffValue} W ({d.OffDelayMinutes} Min)"
                                    : $"<b>Ein:</b> SOC > {d.OnValue} % <br> <b>Aus:</b> SOC &lt; {d.OffValue} %"; 
                            }

                            string btnAuto = d.ManualOverride == "auto" ? "active" : "";
                            string btnOn = d.ManualOverride == "on" ? "active" : "";
                            string btnOff = d.ManualOverride == "off" ? "active" : "";

                            // NEU: Anzeige der Priorität im Header
                            string headerInfo = $"<b>[Prio {d.Priority}]</b> | " + (d.SystemType == "cascade" ? $"Stufen-IPs: {d.IpAddress} / {d.IpAddress2} / {d.IpAddress3}" : d.IpAddress);

                            deviceListHtml.Append($@"
                            <div class='device-item'>
                                <div class='device-header'>
                                    <div>
                                        <h3 style='margin: 0; color: #333;'>{d.Name}</h3>
                                        <small style='color: #666;'>{headerInfo} | {modeName}</small>
                                    </div>
                                    <div style='font-weight: bold; font-size: 18px;'>{status}</div>
                                </div>
                                <div class='rule-box'>
                                    {ruleText}
                                </div>
                                
                                <div style='margin-top: 15px; border-top: 1px solid #eee; padding-top: 10px; display: flex; align-items: center; justify-content: space-between;'>
                                    <div>
                                        <span style='font-size: 12px; color: #666; margin-right: 10px;'>Betriebsmodus:</span>
                                        <a href='/setmode?id={d.Id}&mode=auto' class='btn-mode {btnAuto}'>🤖 Auto</a>
                                        <a href='/setmode?id={d.Id}&mode=on' class='btn-mode {btnOn}'>🟢 Dauer-An</a>
                                        <a href='/setmode?id={d.Id}&mode=off' class='btn-mode {btnOff}'>🔴 Dauer-Aus</a>
                                    </div>
                                    <div style='text-align: right;'>
                                        <a href='/edit?id={d.Id}' class='btn-edit'>✏️ Bearbeiten</a>
                                        <a href='/delete?id={d.Id}' class='btn-delete'>🗑️ Löschen</a>
                                    </div>
                                </div>
                            </div>");
                        }
                    }

                    string warningHtml = string.IsNullOrWhiteSpace(_deviceManager.Settings.VictronIp) 
                        ? "<div style='background-color: #ffcccc; color: #cc0000; padding: 15px; border-radius: 8px; margin-bottom: 20px; font-weight: bold; text-align: center;'>⚠️ Achtung: Es wurde noch keine Victron IP-Adresse hinterlegt. Bitte klicke auf '⚙️ System Einstellungen'.</div>" 
                        : "";

                    htmlResponse = $@"
                    <!DOCTYPE html>
                    <html lang='de'>
                    <head>
                        <meta charset='UTF-8'>
                        <meta http-equiv='refresh' content='5'>
                        <title>PVManager - Dashboard</title>
                        <style>
                            body {{ font-family: Arial, sans-serif; background-color: #f4f6f9; padding: 30px; color: #333; }}
                            .card {{ background: white; max-width: 700px; margin: 0 auto; padding: 30px; border-radius: 12px; box-shadow: 0 4px 15px rgba(0,0,0,0.1); }}
                            .header-bar {{ display: flex; justify-content: space-between; align-items: center; border-bottom: 2px solid #eee; padding-bottom: 10px; margin-bottom: 20px; }}
                            h1 {{ color: #0078D7; margin: 0; }}
                            
                            .metric-container {{ display: flex; gap: 10px; margin-bottom: 25px; flex-wrap: wrap; }}
                            .metric-box {{ background: #f8f9fa; padding: 15px; border-radius: 8px; flex: 1; min-width: 100px; text-align: center; border: 1px solid #e3e6f0; }}
                            .metric-box span {{ display: block; font-size: 12px; color: #666; margin-bottom: 5px; text-transform: uppercase; letter-spacing: 0.5px; font-weight: bold; }}
                            .metric-box b {{ font-size: 20px; color: #0078D7; }}
                            .phase-info {{ font-size: 10px; color: #888; margin-top: 5px; }}
                            
                            .device-item {{ border: 1px solid #ddd; border-radius: 8px; padding: 15px; margin-bottom: 15px; background: #fff; box-shadow: 0 2px 5px rgba(0,0,0,0.05); }}
                            .device-header {{ display: flex; justify-content: space-between; align-items: center; }}
                            .rule-box {{ background: #f8f9fa; padding: 10px; border-radius: 5px; margin-top: 10px; font-size: 14px; border-left: 4px solid #0078D7; }}
                            
                            .btn-mode {{ padding: 5px 10px; border: 1px solid #ccc; border-radius: 5px; text-decoration: none; color: #333; font-size: 12px; margin-right: 5px; background: #fff; }}
                            .btn-mode.active {{ background: #0078D7; color: white; border-color: #0078D7; font-weight: bold; }}
                            
                            .btn-add {{ display: block; background-color: #28a745; color: white; padding: 15px; text-align: center; border-radius: 8px; text-decoration: none; font-weight: bold; margin-top: 30px; }}
                            .btn-settings {{ background-color: #6c757d; color: white; padding: 8px 15px; border-radius: 5px; text-decoration: none; font-size: 14px; font-weight: bold; }}
                            .btn-delete {{ color: #dc3545; text-decoration: none; font-weight: bold; font-size: 14px; }}
                            .btn-edit {{ color: #e0a800; text-decoration: none; font-weight: bold; font-size: 14px; margin-right: 15px; }}
                        </style>
                    </head>
                    <body>
                        <div class='card'>
                            {warningHtml}
                            <div class='header-bar'>
                                <h1>🏠 System Übersicht</h1>
                                <a href='/settings' class='btn-settings'>⚙️ System Einstellungen</a>
                            </div>
                            
                            <div class='metric-container'>
                                <div class='metric-box'><span>🔋 SOC</span><b>{_deviceManager.LiveSoc} %</b></div>
                                <div class='metric-box'><span>☀️ PV</span><b>{_deviceManager.LivePv} W</b></div>
                                <div class='metric-box'>
                                    <span>🔌 AC-Last</span><b>{_deviceManager.LiveAcLoad} W</b>
                                    <div class='phase-info'>L1:{_deviceManager.LiveAcL1} | L2:{_deviceManager.LiveAcL2} | L3:{_deviceManager.LiveAcL3}</div>
                                </div>
                                <div class='metric-box'>
                                    <span>🏭 Netz</span><b>{_deviceManager.LiveGrid} W</b>
                                    <div class='phase-info'>L1:{_deviceManager.LiveGridL1} | L2:{_deviceManager.LiveGridL2} | L3:{_deviceManager.LiveGridL3}</div>
                                </div>
                            </div>

                            <h2 style='color: #333; font-size: 18px; border-bottom: 1px solid #eee; padding-bottom: 10px;'>Gesteuerte Geräte</h2>
                            {deviceListHtml}
                            <a href='/setup' class='btn-add'>➕ Neues Gerät hinzufügen</a>
                        </div>
                    </body>
                    </html>";
                }
                else if (path == "/setmode")
                {
                    string? id = request.QueryString["id"];
                    string? mode = request.QueryString["mode"];
                    if (!string.IsNullOrEmpty(id) && !string.IsNullOrEmpty(mode))
                    {
                        var target = _deviceManager.GetAllDevices().FirstOrDefault(d => d.Id == id);
                        if (target != null) { target.ManualOverride = mode; _deviceManager.SaveDevices(); }
                    }
                    response.Redirect("/"); response.Close(); return;
                }
                else if (path == "/settings")
                {
                    htmlResponse = $@"
                    <!DOCTYPE html><html lang='de'><head><meta charset='UTF-8'><title>PVManager - Einstellungen</title>
                    <style>body {{ font-family: Arial, sans-serif; background-color: #f4f6f9; padding: 30px; color: #333; }} .card {{ background: white; max-width: 500px; margin: 0 auto; padding: 30px; border-radius: 12px; }} h2 {{ color: #6c757d; border-bottom: 2px solid #eee; padding-bottom: 10px; margin-top: 0; }} label {{ font-weight: bold; display: block; margin-top: 15px; margin-bottom: 5px; }} input {{ width: 100%; padding: 10px; border: 1px solid #ccc; border-radius: 5px; box-sizing: border-box; }} .btn-save {{ background-color: #0078D7; color: white; padding: 15px; width: 100%; border: none; border-radius: 8px; font-size: 18px; font-weight: bold; margin-top: 20px; cursor: pointer; }} .back-link {{ display: block; margin-top: 20px; text-align: center; color: #666; text-decoration: none; }}</style></head>
                    <body><div class='card'><h2>⚙️ Allgemeine Einstellungen</h2><form action='/savesettings' method='GET'><label>IP-Adresse des Victron Cerbo:</label><input type='text' name='victron_ip' value='{_deviceManager.Settings.VictronIp}' placeholder='z.B. 192.168.178.57' required><button type='submit' class='btn-save'>💾 Speichern</button></form><a href='/' class='back-link'>Abbrechen & Zurück</a></div></body></html>";
                }
                else if (path == "/setup")
                {
                    htmlResponse = @"
                    <!DOCTYPE html><html lang='de'><head><meta charset='UTF-8'><title>PVManager - Setup</title>
                    <style>body { font-family: Arial, sans-serif; background-color: #f4f6f9; text-align: center; padding-top: 50px; color: #333; } .card { background: white; max-width: 500px; margin: 0 auto; padding: 40px; border-radius: 12px; box-shadow: 0 4px 15px rgba(0,0,0,0.1); } h1 { color: #0078D7; margin-bottom: 10px; } .btn { display: block; width: 100%; padding: 15px; margin: 15px 0; font-size: 18px; font-weight: bold; color: white; border: none; border-radius: 8px; cursor: pointer; text-decoration: none; box-sizing: border-box; } .btn-cascade { background-color: #fd7e14; } .btn-grid { background-color: #28a745; } .btn-island { background-color: #ffc107; color: #333; } .btn-wallbox { background-color: #17a2b8; } .btn-heater { background-color: #dc3545; } .back-link { display: block; margin-top: 20px; color: #666; text-decoration: none; }</style></head>
                    <body><div class='card'><h1>Neues Gerät</h1><a href='/mode?type=wallbox' class='btn btn-wallbox'>🚗 Wallbox</a><a href='/mode?type=heater' class='btn btn-heater'>🔥 my-PV Heizstab (Stufenlos)</a><a href='/mode?type=cascade' class='btn btn-cascade'>🌡️ 3-Phasen Kaskaden-Heizstab</a><a href='/mode?type=grid' class='btn btn-grid'>⚡ Smart-Gerät (Netz-Regel)</a><a href='/mode?type=island' class='btn btn-island'>🔋 Smart-Gerät (Batterie-Regel)</a><a href='/' class='back-link'>Abbrechen & Zurück</a></div></body></html>";
                }
                else if (path == "/mode" && request.QueryString["type"] == "cascade")
                {
                    string priorityDropdown = GetPriorityDropdown();
                    htmlResponse = $@"
                    <!DOCTYPE html><html lang='de'><head><meta charset='UTF-8'><title>Kaskade Setup</title>
                    <style>body {{ font-family: Arial, sans-serif; background-color: #f4f6f9; padding: 30px; color: #333; }} .card {{ background: white; max-width: 700px; margin: 0 auto; padding: 30px; border-radius: 12px; }} h2 {{ color: #fd7e14; margin-top: 0; }} label {{ font-weight: bold; display: block; margin-top: 15px; margin-bottom: 5px; }} input, select {{ width: 100%; padding: 10px; border: 1px solid #ccc; border-radius: 5px; box-sizing: border-box; }} .row {{ display: flex; gap: 15px; }} .col {{ flex: 1; }} .btn-save {{ background-color: #fd7e14; color: white; padding: 15px; width: 100%; border: none; border-radius: 8px; font-size: 18px; font-weight: bold; margin-top: 20px; cursor: pointer; }} .info-box {{ background-color: #fff3cd; border-left: 4px solid #ffc107; padding: 15px; margin: 15px 0; font-size: 14px; border-radius: 4px; line-height: 1.5; }}</style>
                    <script>
                        function toggleCascade() {{
                            var mode = document.getElementById('cm_casc').value;
                            var unit = mode === 'soc' ? '%' : 'W';
                            document.getElementById('lbl1').innerText = 'Ein ab (' + unit + ')';
                            document.getElementById('lbl2').innerText = 'Ein ab (' + unit + ')';
                            document.getElementById('lbl3').innerText = 'Ein ab (' + unit + ')';
                            document.getElementById('lbl_off').innerText = 'Aus (Stufen runterschalten) < (' + unit + ')';
                            
                            var minVal = mode === 'soc' ? '0' : '1';
                            var maxVal = mode === 'soc' ? '100' : '99999';
                            
                            ['ow1','ow2','ow3','owoff'].forEach(id => {{
                                document.getElementById(id).min = minVal;
                                document.getElementById(id).max = maxVal;
                            }});
                        }}
                    </script>
                    </head>
                    <body><div class='card'><h2>🌡️ 3-Phasen Kaskade Hinzufügen</h2>
                    <div class='info-box'>
                        <b>ℹ️ So stellst du die Kaskade ein:</b><br><br>
                        <b>Option A: Netz-Regelung (Überschuss in Watt)</b><br>
                        Trage <b>etwas mehr</b> ein, als deine Phase verbraucht! (z.B. Heizstab zieht 1000W ➔ trage <b>1100</b> ein).<br>
                        <i>Warum?</i> Zähler hat >1100W ➔ Stufe 1 AN. Der Stab zieht 1000W weg. Der Zähler zeigt danach noch 100W an (alles bleibt stabil!).<br><br>
                        <b>Option B: Batterie-Regelung (SOC in %)</b><br>
                        Trage einfach feste Stufen ein (z.B. Ein ab <b>90</b>, Stufe 2 ab <b>95</b>, Stufe 3 ab <b>98</b>).<br><br>
                        <b>⏱️ Schaltzeiten:</b> Die Zeiten sind aktuell fest im Hintergrund einprogrammiert: <b>1 Minute</b> warten beim Hochschalten auf die nächste Stufe, <b>2 Minuten</b> warten beim Runterschalten (Wolkenschutz).
                    </div>
                    <form action='/save' method='GET'><input type='hidden' name='type' value='cascade'><input type='hidden' name='model' value='switch'>
                    <label>Regelung nach:</label><select name='control_mode' id='cm_casc' onchange='toggleCascade()'><option value='grid'>PV-Überschuss (Einspeisung ins Netz)</option><option value='soc'>Batteriestand (SOC %)</option></select>
                    {priorityDropdown}
                    <label>Name der Kaskade:</label><input type='text' name='name' required>
                    <div class='row' style='margin-top: 20px; padding: 15px; background: #f8f9fa; border-radius: 8px;'><div class='col'><label>Stufe 1 IP:</label><input type='text' name='ip' required></div><div class='col'><label id='lbl1'>Ein ab (W)</label><input type='number' name='on_watt' id='ow1' value='1100' min='1' required></div></div>
                    <div class='row' style='margin-top: 20px; padding: 15px; background: #f8f9fa; border-radius: 8px;'><div class='col'><label>Stufe 2 IP:</label><input type='text' name='ip2' required></div><div class='col'><label id='lbl2'>Ein ab (W)</label><input type='number' name='on_watt2' id='ow2' value='1100' min='1' required></div></div>
                    <div class='row' style='margin-top: 20px; padding: 15px; background: #f8f9fa; border-radius: 8px;'><div class='col'><label>Stufe 3 IP:</label><input type='text' name='ip3' required></div><div class='col'><label id='lbl3'>Ein ab (W)</label><input type='number' name='on_watt3' id='ow3' value='1100' min='1' required></div></div>
                    <div class='row' style='margin-top: 20px;'><div class='col'><label id='lbl_off'>Aus (Stufen runterschalten) &lt; (W)</label><input type='number' name='off_watt' id='owoff' value='10' min='1' required></div></div>
                    <button type='submit' class='btn-save'>💾 Kaskade Speichern</button></form><a href='/' style='display:block; text-align:center; margin-top:20px; color:#666; text-decoration:none;'>Abbrechen & Zurück</a></div></body></html>";
                }
                else if (path == "/mode" && request.QueryString["type"] == "wallbox")
                {
                    string priorityDropdown = GetPriorityDropdown();
                    htmlResponse = $@"
                    <!DOCTYPE html><html lang='de'><head><meta charset='UTF-8'><title>Wallbox Setup</title>
                    <style>body {{ font-family: Arial, sans-serif; background-color: #f4f6f9; padding: 30px; color: #333; }} .card {{ background: white; max-width: 600px; margin: 0 auto; padding: 30px; border-radius: 12px; }} h2 {{ color: #17a2b8; }} label {{ font-weight: bold; display: block; margin-top: 15px; margin-bottom: 5px; }} input, select {{ width: 100%; padding: 10px; border: 1px solid #ccc; border-radius: 5px; box-sizing: border-box; }} .btn-save {{ background-color: #0078D7; color: white; padding: 15px; width: 100%; border: none; border-radius: 8px; font-size: 18px; font-weight: bold; margin-top: 20px; cursor: pointer; }} .row {{ display: flex; gap: 15px; }} .col {{ flex: 1; }} .info-box {{ background-color: #fff3cd; border-left: 4px solid #ffc107; padding: 15px; margin: 15px 0; font-size: 14px; border-radius: 4px; line-height: 1.5; }}</style>
                    <script>
                    function t(){{ 
                        var m=document.getElementById('cm').value; 
                        document.getElementById('sf').style.display = (m==='grid')?'none':'flex'; 
                        document.getElementById('tf').style.display = (m==='grid')?'flex':'none'; 
                    }} 
                    window.onload=t;
                    </script></head>
                    <body><div class='card'><h2>🚗 Wallbox Hinzufügen</h2>
                    <div class='info-box'><b>ℹ️ Info zur Ladeleistung:</b> Die Wallbox regelt aktuell aus Sicherheitsgründen bis <b>maximal 16 Ampere</b> (ca. 11 kW). Das technische Minimum liegt bei 6A. Im reinen Batterie-Modus (feste Stufen) lädt das Skript mit 16A (Voll) oder 10A (Gedrosselt).</div>
                    <form action='/save' method='GET'><input type='hidden' name='type' value='wallbox'>
                    <label>Hersteller / Modell:</label><select name='model'><option value='goe'>go-e Charger (HTTP API v2)</option><option value='keba'>KEBA KeContact P30/P40 (UDP)</option><option value='heidelberg'>Heidelberg Energy Control (Modbus TCP)</option><option value='openwb'>OpenWB Serie 1 (MQTT)</option><option value='openwb2'>OpenWB Serie 2 (MQTT)</option></select>
                    <label>Regelung nach:</label><select name='control_mode' id='cm' onchange='t()'><option value='grid'>PV-Überschuss (Einspeisung ins Netz)</option><option value='soc_pv'>Batteriestand + PV-Überschuss (Stufenlos)</option><option value='soc'>Batteriestand (Feste Stufen)</option></select>
                    <div class='row' id='sf'><div class='col'><label>Start ab SOC (%)</label><input type='number' name='on_soc' value='95' min='0' max='100'></div><div class='col'><label>Stopp ab SOC (%)</label><input type='number' name='off_soc' value='90' min='0' max='100'></div></div>
                    
                    <div class='row' id='tf' style='margin-top:20px;'><div class='col'><label>Start-Verzögerung (Minuten)</label><select name='on_time'><option value='0'>0</option><option value='1'>1</option><option value='3'>3</option><option value='5' selected>5</option><option value='10'>10</option></select></div><div class='col'><label>Stopp-Verzögerung (Minuten)</label><select name='off_time'><option value='0'>0</option><option value='1'>1</option><option value='3'>3</option><option value='5'>5</option><option value='10' selected>10</option></select></div></div>
                    
                    {priorityDropdown}
                    <label>Name:</label><input type='text' name='name' required><label>IP-Adresse:</label><input type='text' name='ip' required><button type='submit' class='btn-save'>💾 Speichern</button></form>
                    <a href='/' style='display:block; text-align:center; margin-top:20px; color:#666; text-decoration:none;'>Abbrechen & Zurück</a></div></body></html>";
                }
                else if (path == "/mode" && request.QueryString["type"] == "heater")
                {
                    string priorityDropdown = GetPriorityDropdown();
                    htmlResponse = $@"
                    <!DOCTYPE html><html lang='de'><head><meta charset='UTF-8'><title>Heizstab Setup</title>
                    <style>body {{ font-family: Arial, sans-serif; background-color: #f4f6f9; padding: 30px; color: #333; }} .card {{ background: white; max-width: 600px; margin: 0 auto; padding: 30px; border-radius: 12px; }} h2 {{ color: #dc3545; }} label {{ font-weight: bold; display: block; margin-top: 15px; margin-bottom: 5px; }} input, select {{ width: 100%; padding: 10px; border: 1px solid #ccc; border-radius: 5px; box-sizing: border-box; }} .btn-save {{ background-color: #0078D7; color: white; padding: 15px; width: 100%; border: none; border-radius: 8px; font-size: 18px; font-weight: bold; margin-top: 20px; cursor: pointer; }} .row {{ display: flex; gap: 15px; }} .col {{ flex: 1; }}</style>
                    <script>
                    function t(){{ 
                        var m=document.getElementById('cm').value; 
                        document.getElementById('sf').style.display = (m==='grid')?'none':'flex'; 
                        document.getElementById('tf').style.display = (m==='grid')?'flex':'none'; 
                    }} 
                    window.onload=t;
                    </script></head>
                    <body><div class='card'><h2>🔥 my-PV Heizstab Hinzufügen</h2>
                    <form action='/save' method='GET'><input type='hidden' name='type' value='heater'><input type='hidden' name='model' value='mypv'>
                    <label>Regelung nach:</label><select name='control_mode' id='cm' onchange='t()'><option value='grid'>PV-Überschuss (Einspeisung ins Netz)</option><option value='soc_pv'>Batteriestand + PV-Überschuss (Stufenlos)</option><option value='soc'>Batteriestand (Feste Stufen)</option></select>
                    <div class='row' id='sf'><div class='col'><label>Start ab SOC (%)</label><input type='number' name='on_soc' value='95' min='0' max='100'></div><div class='col'><label>Stopp ab SOC (%)</label><input type='number' name='off_soc' value='90' min='0' max='100'></div></div>
                    
                    <div class='row' id='tf' style='margin-top:20px;'><div class='col'><label>Start-Verzögerung (Minuten)</label><select name='on_time'><option value='0'>0</option><option value='1'>1</option><option value='3'>3</option><option value='5' selected>5</option><option value='10'>10</option></select></div><div class='col'><label>Stopp-Verzögerung (Minuten)</label><select name='off_time'><option value='0'>0</option><option value='1'>1</option><option value='3'>3</option><option value='5'>5</option><option value='10' selected>10</option></select></div></div>
                    
                    {priorityDropdown}
                    <label>Name:</label><input type='text' name='name' required><label>my-PV IP-Adresse:</label><input type='text' name='ip' required><button type='submit' class='btn-save'>💾 Speichern</button></form>
                    <a href='/' style='display:block; text-align:center; margin-top:20px; color:#666; text-decoration:none;'>Abbrechen & Zurück</a></div></body></html>";
                }
                else if (path == "/mode" && request.QueryString["type"] == "grid")
                {
                    string priorityDropdown = GetPriorityDropdown();
                    htmlResponse = $@"
                    <!DOCTYPE html><html lang='de'><head><meta charset='UTF-8'><title>Netz Setup</title>
                    <style>body {{ font-family: Arial, sans-serif; background-color: #f4f6f9; padding: 30px; color: #333; }} .card {{ background: white; max-width: 600px; margin: 0 auto; padding: 30px; border-radius: 12px; }} h2 {{ color: #28a745; }} label {{ font-weight: bold; display: block; margin-top: 15px; margin-bottom: 5px; }} input, select {{ width: 100%; padding: 10px; border: 1px solid #ccc; border-radius: 5px; box-sizing: border-box; }} .btn-save {{ background-color: #0078D7; color: white; padding: 15px; width: 100%; border: none; border-radius: 8px; font-size: 18px; font-weight: bold; margin-top: 20px; cursor: pointer; }} .row {{ display: flex; gap: 15px; }} .col {{ flex: 1; }}</style>
                    <script>function t_mod(){{ var m=document.getElementById('device_model').value; document.getElementById('custom_url_fields').style.display = (m==='custom')?'block':'none'; }} window.onload=t_mod;</script>
                    </head>
                    <body><div class='card'><h2>⚡ Überschuss-Regel (Watt)</h2>
                    <form action='/save' method='GET'><input type='hidden' name='type' value='grid'>
                    <div class='row'><div class='col'><label>Ein wenn Einspeisung > (W)</label><input type='number' name='on_watt' value='200' min='1' required></div><div class='col'><label>Dauer (Minuten)</label><select name='on_time'><option value='0'>0</option><option value='1'>1</option><option value='3'>3</option><option value='5' selected>5</option><option value='10'>10</option></select></div></div>
                    <div class='row' style='margin-top:20px;'><div class='col'><label>Aus wenn Einspeisung &lt; (W)</label><input type='number' name='off_watt' value='10' min='1' required></div><div class='col'><label>Dauer (Minuten)</label><select name='off_time'><option value='0'>0</option><option value='1'>1</option><option value='3'>3</option><option value='5'>5</option><option value='10' selected>10</option></select></div></div>
                    
                    <label>Geräte-Typ (Modell):</label><select name='model' id='device_model' onchange='t_mod()'><option value='switch'>Shelly / Tasmota (Standard)</option><option value='custom'>Benutzerdefiniert (HTTP-URLs)</option></select>
                    {priorityDropdown}
                    <label>Name:</label><input type='text' name='name' required><label>Geräte IP:</label><input type='text' name='ip' value='0.0.0.0' required>
                    
                    <div id='custom_url_fields' style='display:none;'>
                        <label>HTTP URL für EIN (inkl. http://):</label><input type='text' name='custom_on'>
                        <label>HTTP URL für AUS (inkl. http://):</label><input type='text' name='custom_off'>
                    </div>

                    <button type='submit' class='btn-save'>💾 Speichern</button></form><a href='/' style='display:block; text-align:center; margin-top:20px; color:#666; text-decoration:none;'>Abbrechen & Zurück</a></div></body></html>";
                }
                else if (path == "/mode" && request.QueryString["type"] == "island")
                {
                    string priorityDropdown = GetPriorityDropdown();
                    htmlResponse = $@"
                    <!DOCTYPE html><html lang='de'><head><meta charset='UTF-8'><title>Insel Setup</title>
                    <style>body {{ font-family: Arial, sans-serif; background-color: #f4f6f9; padding: 30px; color: #333; }} .card {{ background: white; max-width: 600px; margin: 0 auto; padding: 30px; border-radius: 12px; }} h2 {{ color: #ffc107; }} label {{ font-weight: bold; display: block; margin-top: 15px; margin-bottom: 5px; }} input, select {{ width: 100%; padding: 10px; border: 1px solid #ccc; border-radius: 5px; box-sizing: border-box; }} .btn-save {{ background-color: #0078D7; color: white; padding: 15px; width: 100%; border: none; border-radius: 8px; font-size: 18px; font-weight: bold; margin-top: 20px; cursor: pointer; }} .row {{ display: flex; gap: 15px; }} .col {{ flex: 1; }}</style>
                    <script>function t_mod(){{ var m=document.getElementById('device_model').value; document.getElementById('custom_url_fields').style.display = (m==='custom')?'block':'none'; }} window.onload=t_mod;</script>
                    </head>
                    <body><div class='card'><h2>🔋 Batterie-Regel (SOC %)</h2>
                    <form action='/save' method='GET'><input type='hidden' name='type' value='island'>
                    <div class='row'><div class='col'><label>Ein wenn SOC > (%)</label><input type='number' name='on_soc' value='95' min='0' max='100' required></div></div>
                    <div class='row' style='margin-top:20px;'><div class='col'><label>Aus wenn SOC &lt; (%)</label><input type='number' name='off_soc' value='90' min='0' max='100' required></div></div>
                    
                    <label>Geräte-Typ (Modell):</label><select name='model' id='device_model' onchange='t_mod()'><option value='switch'>Shelly / Tasmota (Standard)</option><option value='custom'>Benutzerdefiniert (HTTP-URLs)</option></select>
                    {priorityDropdown}
                    <label>Name:</label><input type='text' name='name' required><label>Geräte IP:</label><input type='text' name='ip' value='0.0.0.0' required>
                    
                    <div id='custom_url_fields' style='display:none;'>
                        <label>HTTP URL für EIN (inkl. http://):</label><input type='text' name='custom_on'>
                        <label>HTTP URL für AUS (inkl. http://):</label><input type='text' name='custom_off'>
                    </div>

                    <button type='submit' class='btn-save'>💾 Speichern</button></form><a href='/' style='display:block; text-align:center; margin-top:20px; color:#666; text-decoration:none;'>Abbrechen & Zurück</a></div></body></html>";
                }
                else if (path == "/edit")
                {
                    string? id = request.QueryString["id"];
                    var existingDevice = _deviceManager.GetAllDevices().FirstOrDefault(d => d.Id == id);
                    
                    if (existingDevice == null) 
                    {
                        response.Redirect("/"); response.Close(); return;
                    }

                    // --- NEU: Dropdown für das Bearbeiten-Fenster (schließt sich selbst bei den Blockaden aus) ---
                    string priorityDropdown = GetPriorityDropdown(existingDevice.Priority, existingDevice.Id);

                    string GetSelected(string savedValue, string optionValue) => savedValue == optionValue ? "selected" : "";
                    string GetNumSelected(int savedValue, int optionValue) => savedValue == optionValue ? "selected" : "";

                    if (existingDevice.SystemType == "cascade")
                    {
                        string unitLoad = existingDevice.ControlMode == "soc" ? "%" : "W";
                        string minMaxAttr = existingDevice.ControlMode == "soc" ? "min='0' max='100'" : "min='1'";
                        
                        htmlResponse = $@"
                        <!DOCTYPE html><html lang='de'><head><meta charset='UTF-8'><title>Kaskade bearbeiten</title>
                        <style>body {{ font-family: Arial, sans-serif; background-color: #f4f6f9; padding: 30px; color: #333; }} .card {{ background: white; max-width: 700px; margin: 0 auto; padding: 30px; border-radius: 12px; }} h2 {{ color: #e0a800; margin-top: 0; }} label {{ font-weight: bold; display: block; margin-top: 15px; margin-bottom: 5px; }} input, select {{ width: 100%; padding: 10px; border: 1px solid #ccc; border-radius: 5px; box-sizing: border-box; }} .row {{ display: flex; gap: 15px; }} .col {{ flex: 1; }} .btn-save {{ background-color: #e0a800; color: white; padding: 15px; width: 100%; border: none; border-radius: 8px; font-size: 18px; font-weight: bold; margin-top: 20px; cursor: pointer; }} .info-box {{ background-color: #fff3cd; border-left: 4px solid #ffc107; padding: 15px; margin: 15px 0; font-size: 14px; border-radius: 4px; line-height: 1.5; }}</style>
                        <script>
                            function toggleCascadeEdit() {{
                                var mode = document.getElementById('cm_casc_edit').value;
                                var unit = mode === 'soc' ? '%' : 'W';
                                document.getElementById('lbl1_e').innerText = 'Ein ab (' + unit + ')';
                                document.getElementById('lbl2_e').innerText = 'Ein ab (' + unit + ')';
                                document.getElementById('lbl3_e').innerText = 'Ein ab (' + unit + ')';
                                document.getElementById('lbl_off_e').innerText = 'Aus (Stufen runterschalten) < (' + unit + ')';
                                
                                var minVal = mode === 'soc' ? '0' : '1';
                                var maxVal = mode === 'soc' ? '100' : '99999';
                                
                                ['ow1_e','ow2_e','ow3_e','owoff_e'].forEach(id => {{
                                    document.getElementById(id).min = minVal;
                                    document.getElementById(id).max = maxVal;
                                }});
                            }}
                        </script>
                        </head>
                        <body><div class='card'><h2>✏️ 3-Phasen Kaskade bearbeiten</h2>
                        <form action='/save' method='GET'>
                        <input type='hidden' name='id' value='{existingDevice.Id}'><input type='hidden' name='type' value='cascade'><input type='hidden' name='model' value='switch'>
                        
                        <label>Regelung nach:</label>
                        <select name='control_mode' id='cm_casc_edit' onchange='toggleCascadeEdit()'>
                            <option value='grid' {GetSelected(existingDevice.ControlMode, "grid")}>PV-Überschuss (Einspeisung ins Netz)</option>
                            <option value='soc' {GetSelected(existingDevice.ControlMode, "soc")}>Batteriestand (SOC %)</option>
                        </select>

                        {priorityDropdown}
                        <label>Name der Kaskade:</label><input type='text' name='name' value='{existingDevice.Name}' required>
                        <div class='row' style='margin-top: 20px; padding: 15px; background: #f8f9fa; border-radius: 8px;'><div class='col'><label>Stufe 1 IP:</label><input type='text' name='ip' value='{existingDevice.IpAddress}' required></div><div class='col'><label id='lbl1_e'>Ein ab ({unitLoad})</label><input type='number' name='on_watt' id='ow1_e' value='{existingDevice.OnValue}' {minMaxAttr} required></div></div>
                        <div class='row' style='margin-top: 20px; padding: 15px; background: #f8f9fa; border-radius: 8px;'><div class='col'><label>Stufe 2 IP:</label><input type='text' name='ip2' value='{existingDevice.IpAddress2}' required></div><div class='col'><label id='lbl2_e'>Ein ab ({unitLoad})</label><input type='number' name='on_watt2' id='ow2_e' value='{existingDevice.OnValue2}' {minMaxAttr} required></div></div>
                        <div class='row' style='margin-top: 20px; padding: 15px; background: #f8f9fa; border-radius: 8px;'><div class='col'><label>Stufe 3 IP:</label><input type='text' name='ip3' value='{existingDevice.IpAddress3}' required></div><div class='col'><label id='lbl3_e'>Ein ab ({unitLoad})</label><input type='number' name='on_watt3' id='ow3_e' value='{existingDevice.OnValue3}' {minMaxAttr} required></div></div>
                        <div class='row' style='margin-top: 20px;'><div class='col'><label id='lbl_off_e'>Aus (Runterschalten) &lt; ({unitLoad})</label><input type='number' name='off_watt' id='owoff_e' value='{existingDevice.OffValue}' {minMaxAttr} required></div></div>
                        <button type='submit' class='btn-save'>💾 Änderungen speichern</button></form>
                        <a href='/' style='display:block; text-align:center; margin-top:20px; color:#666; text-decoration:none;'>Abbrechen & Zurück</a></div></body></html>";
                    }
                    else if (existingDevice.SystemType == "wallbox" || existingDevice.SystemType == "heater")
                    {
                        string emoji = existingDevice.SystemType == "wallbox" ? "🚗" : "🔥";
                        string titleText = existingDevice.SystemType == "wallbox" ? "Wallbox" : "my-PV Heizstab";
                        
                        htmlResponse = $@"
                        <!DOCTYPE html>
                        <html lang='de'><head><meta charset='UTF-8'><title>PVManager - Bearbeiten</title>
                        <style>body {{ font-family: Arial, sans-serif; background-color: #f4f6f9; padding: 30px; color: #333; }} .card {{ background: white; max-width: 600px; margin: 0 auto; padding: 30px; border-radius: 12px; }} h2 {{ color: #e0a800; border-bottom: 2px solid #eee; padding-bottom: 10px; margin-top: 0; }} label {{ font-weight: bold; display: block; margin-top: 15px; margin-bottom: 5px; }} input, select {{ width: 100%; padding: 10px; border: 1px solid #ccc; border-radius: 5px; box-sizing: border-box; }} .row {{ display: flex; gap: 15px; }} .col {{ flex: 1; }} .btn-save {{ background-color: #e0a800; color: white; padding: 15px; width: 100%; border: none; border-radius: 8px; font-size: 18px; font-weight: bold; margin-top: 20px; cursor: pointer; }} .info-box {{ background-color: #fff3cd; border-left: 4px solid #ffc107; padding: 15px; margin: 15px 0; font-size: 14px; border-radius: 4px; line-height: 1.5; }}</style>
                        <script>
                            function toggleFields() {{
                                var mode = document.getElementById('control_mode').value;
                                var socRow = document.getElementById('soc_fields');
                                var timeRow = document.getElementById('time_fields');
                                
                                if (mode === 'grid') {{ 
                                    socRow.style.display = 'none'; 
                                    timeRow.style.display = 'flex';
                                }} else {{ 
                                    socRow.style.display = 'flex'; 
                                    timeRow.style.display = 'none';
                                }}
                            }}
                            window.onload = toggleFields;
                        </script>
                        </head>
                        <body><div class='card'><h2>✏️ {emoji} {titleText} bearbeiten</h2>
                        <form action='/save' method='GET'>
                        <input type='hidden' name='id' value='{existingDevice.Id}'>
                        <input type='hidden' name='type' value='{existingDevice.SystemType}'>
                        <input type='hidden' name='model' value='{existingDevice.DeviceModel}'>
                        
                        <label>Regelung nach:</label>
                        <select name='control_mode' id='control_mode' onchange='toggleFields()'>
                            <option value='grid' {GetSelected(existingDevice.ControlMode, "grid")}>PV-Überschuss (Einspeisung ins Netz)</option>
                            <option value='soc_pv' {GetSelected(existingDevice.ControlMode, "soc_pv")}>Batteriestand + PV-Überschuss (Stufenlos)</option>
                            <option value='soc' {GetSelected(existingDevice.ControlMode, "soc")}>Batteriestand (Feste Stufen)</option>
                        </select>

                        <div class='row' id='soc_fields'>
                            <div class='col'><label>Start ab SOC (%)</label><input type='number' name='on_soc' value='{existingDevice.OnValue}' min='0' max='100'></div>
                            <div class='col'><label>Stopp ab SOC (%)</label><input type='number' name='off_soc' value='{existingDevice.OffValue}' min='0' max='100'></div>
                        </div>

                        <div class='row' id='time_fields' style='margin-top:20px;'>
                            <div class='col'><label>Start-Verzögerung (Minuten)</label>
                                <select name='on_time'>
                                    <option value='0' {GetNumSelected(existingDevice.OnDelayMinutes, 0)}>0</option>
                                    <option value='1' {GetNumSelected(existingDevice.OnDelayMinutes, 1)}>1</option>
                                    <option value='3' {GetNumSelected(existingDevice.OnDelayMinutes, 3)}>3</option>
                                    <option value='5' {GetNumSelected(existingDevice.OnDelayMinutes, 5)}>5</option>
                                    <option value='10' {GetNumSelected(existingDevice.OnDelayMinutes, 10)}>10</option>
                                </select>
                            </div>
                            <div class='col'><label>Stopp-Verzögerung (Minuten)</label>
                                <select name='off_time'>
                                    <option value='0' {GetNumSelected(existingDevice.OffDelayMinutes, 0)}>0</option>
                                    <option value='1' {GetNumSelected(existingDevice.OffDelayMinutes, 1)}>1</option>
                                    <option value='3' {GetNumSelected(existingDevice.OffDelayMinutes, 3)}>3</option>
                                    <option value='5' {GetNumSelected(existingDevice.OffDelayMinutes, 5)}>5</option>
                                    <option value='10' {GetNumSelected(existingDevice.OffDelayMinutes, 10)}>10</option>
                                </select>
                            </div>
                        </div>

                        {priorityDropdown}
                        <label>Name:</label><input type='text' name='name' value='{existingDevice.Name}' required>
                        <label>Geräte IP:</label><input type='text' name='ip' value='{existingDevice.IpAddress}' required>
                        <button type='submit' class='btn-save'>💾 Änderungen speichern</button></form>
                        <a href='/' style='display:block; text-align:center; margin-top:20px; color:#666; text-decoration:none;'>Abbrechen & Zurück</a></div></body></html>";
                    }
                    else
                    {
                        string title = existingDevice.SystemType == "grid" ? "⚡ Überschuss-Regel bearbeiten" : "🔋 Batterie-Regel bearbeiten";
                        string labelOn = existingDevice.SystemType == "grid" ? "Ein wenn Einspeisung > (W)" : "Ein wenn SOC > (%)";
                        string labelOff = existingDevice.SystemType == "grid" ? "Aus wenn Einspeisung < (W)" : "Aus wenn SOC < (%)";
                        string nameOnInput = existingDevice.SystemType == "grid" ? "on_watt" : "on_soc";
                        string nameOffInput = existingDevice.SystemType == "grid" ? "off_watt" : "off_soc";
                        string minMaxAttr = existingDevice.SystemType == "grid" ? "min='1'" : "min='0' max='100'";
                        string customStyle = existingDevice.DeviceModel == "custom" ? "block" : "none";
                        
                        string timeFieldsHtml = "";
                        if (existingDevice.SystemType == "grid")
                        {
                            timeFieldsHtml = $@"
                            <div class='col'><label>Dauer (Minuten)</label>
                                <select name='on_time'>
                                    <option value='0' {GetNumSelected(existingDevice.OnDelayMinutes, 0)}>0</option>
                                    <option value='1' {GetNumSelected(existingDevice.OnDelayMinutes, 1)}>1</option>
                                    <option value='5' {GetNumSelected(existingDevice.OnDelayMinutes, 5)}>5</option>
                                    <option value='10' {GetNumSelected(existingDevice.OnDelayMinutes, 10)}>10</option>
                                </select>
                            </div>";
                        }

                        htmlResponse = $@"
                        <!DOCTYPE html>
                        <html lang='de'><head><meta charset='UTF-8'><title>PVManager - Bearbeiten</title>
                        <style>body {{ font-family: Arial, sans-serif; background-color: #f4f6f9; padding: 30px; color: #333; }} .card {{ background: white; max-width: 600px; margin: 0 auto; padding: 30px; border-radius: 12px; }} h2 {{ color: #e0a800; border-bottom: 2px solid #eee; padding-bottom: 10px; margin-top: 0; }} label {{ font-weight: bold; display: block; margin-top: 15px; margin-bottom: 5px; }} input, select {{ width: 100%; padding: 10px; border: 1px solid #ccc; border-radius: 5px; box-sizing: border-box; }} .row {{ display: flex; gap: 15px; }} .col {{ flex: 1; }} .btn-save {{ background-color: #e0a800; color: white; padding: 15px; width: 100%; border: none; border-radius: 8px; font-size: 18px; font-weight: bold; margin-top: 20px; cursor: pointer; }}</style>
                        <script>function t_mod(){{ var m=document.getElementById('device_model').value; document.getElementById('custom_url_fields').style.display = (m==='custom')?'block':'none'; }} window.onload=t_mod;</script>
                        </head>
                        <body><div class='card'><h2>✏️ {title}</h2>
                        <form action='/save' method='GET'>
                        
                        <input type='hidden' name='id' value='{existingDevice.Id}'>
                        <input type='hidden' name='type' value='{existingDevice.SystemType}'>
                        
                        <div class='row'>
                            <div class='col'><label>{labelOn}</label><input type='number' name='{nameOnInput}' value='{existingDevice.OnValue}' {minMaxAttr} required></div>
                            {timeFieldsHtml}
                        </div>
                        <div class='row' style='margin-top:20px;'>
                            <div class='col'><label>{labelOff}</label><input type='number' name='{nameOffInput}' value='{existingDevice.OffValue}' {minMaxAttr} required></div>
                        </div>
                        <label>Geräte-Typ (Modell):</label>
                        <select name='model' id='device_model' onchange='t_mod()'>
                            <option value='switch' {GetSelected(existingDevice.DeviceModel, "switch")}>Shelly / Tasmota (Standard)</option>
                            <option value='custom' {GetSelected(existingDevice.DeviceModel, "custom")}>Benutzerdefiniert (Eigene HTTP-URLs)</option>
                        </select>
                        
                        {priorityDropdown}
                        
                        <label>Name:</label><input type='text' name='name' value='{existingDevice.Name}' required>
                        <label>Geräte IP:</label><input type='text' name='ip' value='{existingDevice.IpAddress}' required>

                        <div id='custom_url_fields' style='display:{customStyle};'>
                            <label>HTTP URL für EIN:</label><input type='text' name='custom_on' value='{existingDevice.CustomOnUrl}'>
                            <label>HTTP URL für AUS:</label><input type='text' name='custom_off' value='{existingDevice.CustomOffUrl}'>
                        </div>

                        <button type='submit' class='btn-save'>💾 Änderungen speichern</button></form>
                        <a href='/' style='display:block; text-align:center; margin-top:20px; color:#666; text-decoration:none;'>Abbrechen & Zurück</a></div></body></html>";
                    }
                }
                else if (path == "/savesettings")
                {
                    string? newIp = request.QueryString["victron_ip"];
                    if (!string.IsNullOrWhiteSpace(newIp)) { _deviceManager.Settings.VictronIp = newIp; _deviceManager.SaveSettings(); }
                    response.Redirect("/"); response.Close(); return;
                }
                else if (path == "/save")
                {
                    string type = request.QueryString["type"] ?? "grid";
                    string? id = request.QueryString["id"];
                    string model = request.QueryString["model"] ?? "switch";
                    
                    var targetDevice = _deviceManager.GetAllDevices().FirstOrDefault(d => d.Id == id);
                    bool isNewDevice = false;
                    
                    // Annahme: Dein Model heißt ShellyDevice. (Gegebenenfalls anpassen)
                    if (targetDevice == null) { targetDevice = new ShellyDevice(); isNewDevice = true; }

                    targetDevice.Name = request.QueryString["name"] ?? "Unbekannt";
                    targetDevice.IpAddress = request.QueryString["ip"] ?? "0.0.0.0";
                    targetDevice.SystemType = type;
                    targetDevice.DeviceModel = model;
                    targetDevice.ControlMode = request.QueryString["control_mode"] ?? "grid";
                    targetDevice.CustomOnUrl = request.QueryString["custom_on"] ?? "";
                    targetDevice.CustomOffUrl = request.QueryString["custom_off"] ?? "";

                    // --- NEU: Priorität speichern ---
                    if (int.TryParse(request.QueryString["priority"], out int prio))
                    {
                        targetDevice.Priority = prio;
                    }
                    else if (isNewDevice)
                    {
                        // Fallback: Sucht die erste freie Nummer zwischen 1 und 10
                        var used = _deviceManager.GetAllDevices().Select(d => d.Priority).ToList();
                        targetDevice.Priority = Enumerable.Range(1, 10).Except(used).FirstOrDefault();
                        if (targetDevice.Priority == 0) targetDevice.Priority = 99;
                    }

                    if (type == "cascade")
                    {
                        targetDevice.IpAddress2 = request.QueryString["ip2"] ?? "";
                        targetDevice.IpAddress3 = request.QueryString["ip3"] ?? "";
                        double.TryParse(request.QueryString["on_watt"], out double onVal);
                        double.TryParse(request.QueryString["on_watt2"], out double onVal2);
                        double.TryParse(request.QueryString["on_watt3"], out double onVal3);
                        double.TryParse(request.QueryString["off_watt"], out double offVal);
                        
                        if (onVal2 <= onVal) { onVal2 = onVal + 1; }
                        if (onVal3 <= onVal2) { onVal3 = onVal2 + 1; }
                        if (offVal >= onVal) { offVal = onVal - 1; }
                        
                        targetDevice.OnValue = onVal;
                        targetDevice.OnValue2 = onVal2;
                        targetDevice.OnValue3 = onVal3;
                        targetDevice.OffValue = offVal;
                        
                        targetDevice.OnDelayMinutes = 1; 
                        targetDevice.OffDelayMinutes = 2; 
                    }
                    else if (type == "grid")
                    {
                        double.TryParse(request.QueryString["on_watt"], out double onVal);
                        double.TryParse(request.QueryString["off_watt"], out double offVal);
                        if (offVal >= onVal) { offVal = onVal - 1; }
                        targetDevice.OnValue = onVal; targetDevice.OffValue = offVal;
                    }
                    else if (type == "island" || type == "wallbox" || type == "heater")
                    {
                        double.TryParse(request.QueryString["on_soc"], out double onSoc);
                        double.TryParse(request.QueryString["off_soc"], out double offSoc);
                        if (offSoc >= onSoc) { offSoc = onSoc - 1; }
                        targetDevice.OnValue = onSoc; targetDevice.OffValue = offSoc;
                    }
                    
                    if (type != "cascade") 
                    {
                        int.TryParse(request.QueryString["on_time"], out int onTime);
                        int.TryParse(request.QueryString["off_time"], out int offTime);
                        targetDevice.OnDelayMinutes = onTime;
                        targetDevice.OffDelayMinutes = offTime;
                    }

                    if (isNewDevice) _deviceManager.AddDevice(targetDevice);
                    else _deviceManager.SaveDevices();

                    response.Redirect("/"); response.Close(); return; 
                }
                else if (path == "/delete")
                {
                    string? id = request.QueryString["id"];
                    if (!string.IsNullOrEmpty(id)) { _deviceManager.DeleteDevice(id); }
                    response.Redirect("/"); response.Close(); return;
                }
                else { response.Redirect("/"); response.Close(); return; }

                byte[] buffer = Encoding.UTF8.GetBytes(htmlResponse);
                response.ContentLength64 = buffer.Length;
                var output = response.OutputStream;
                await output.WriteAsync(buffer, 0, buffer.Length);
                output.Close();
            }
            catch { }
        }
    }
}
