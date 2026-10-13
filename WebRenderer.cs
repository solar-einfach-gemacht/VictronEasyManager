using System;
using System.Collections.Specialized;
using System.Diagnostics;
using System.Globalization;
using System.Linq;
using System.Net;
using System.Text;
using System.Threading.Tasks;

namespace VictronEasyManager
{
    public class WebRenderer
    {
        private readonly DeviceManager _deviceManager;

        private static readonly string[] ValidTypes = { "cascade", "grid", "island", "wallbox", "heater" };
        private static readonly string[] ValidModels = { "switch", "custom", "goe", "keba", "heidelberg", "openwb", "openwb2", "mypv" };
        private static readonly string[] ValidModes = { "grid", "soc", "soc_pv" };

        public WebRenderer(DeviceManager deviceManager)
        {
            _deviceManager = deviceManager;
        }

        // =====================================================================
        // SERVER
        // =====================================================================
        public async Task StartAsync()
        {
            HttpListener? listener = null;
            string usedPrefix = "";

            foreach (var prefix in new[] { "http://+:80/", "http://+:5000/", "http://localhost:5000/" })
            {
                try
                {
                    var l = new HttpListener();
                    l.Prefixes.Add(prefix);
                    l.Start();
                    listener = l;
                    usedPrefix = prefix;
                    break;
                }
                catch (Exception ex) { Console.WriteLine($"[WEB] {prefix} nicht möglich: {ex.Message}"); }
            }

            if (listener == null) throw new InvalidOperationException("Webserver konnte nicht gestartet werden.");
            Console.WriteLine($"[WEB] PVManager Webserver läuft auf {usedPrefix}");

            if (OperatingSystem.IsWindows())
            {
                try { Process.Start(new ProcessStartInfo(usedPrefix.Replace("+", "localhost")) { UseShellExecute = true }); } catch { }
            }

            while (listener.IsListening)
            {
                try
                {
                    var context = await listener.GetContextAsync();
                    _ = HandleRequestAsync(context);
                }
                catch (ObjectDisposedException) { break; }
                catch (Exception ex)
                {
                    Console.WriteLine($"[WEB] Accept-Fehler: {ex.Message}");
                    await Task.Delay(200);
                }
            }
        }

        private async Task HandleRequestAsync(HttpListenerContext context)
        {
            var request = context.Request;
            var response = context.Response;
            string path = request.Url?.AbsolutePath ?? "/";

            try
            {
                string? html;

                switch (path)
                {
                    case "/": html = RenderDashboard(); break;
                    case "/settings": html = RenderSettings(); break;
                    case "/setup": html = RenderSetup(); break;
                    case "/mode": html = RenderAddForm(request.QueryString["type"]); break;
                    case "/edit": html = RenderEdit(request.QueryString["id"]); break;

                    case "/setmode":
                    case "/savesettings":
                    case "/save":
                    case "/delete":
                        if (IsForeignReferer(request))
                        {
                            response.StatusCode = 403;
                            html = ErrorPage("Anfrage abgelehnt (fremde Herkunft).");
                            break;
                        }
                        html = path switch
                        {
                            "/setmode" => SetMode(request.QueryString),
                            "/savesettings" => SaveSettings(request.QueryString),
                            "/save" => Save(request.QueryString),
                            _ => Delete(request.QueryString)
                        };
                        break;

                    default: html = null; break;
                }

                if (html == null)
                {
                    response.Redirect("/");
                    return;
                }

                byte[] buffer = Encoding.UTF8.GetBytes(html);
                response.ContentType = "text/html; charset=utf-8";
                response.ContentLength64 = buffer.Length;
                await response.OutputStream.WriteAsync(buffer, 0, buffer.Length);
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[WEB-FEHLER] {path}: {ex}");
                try { response.StatusCode = 500; } catch { }
            }
            finally
            {
                try { response.Close(); } catch { }
            }
        }

        // =====================================================================
        // HELFER
        // =====================================================================
        private static string H(string? s) => WebUtility.HtmlEncode(s ?? "");

        private static string N(double v) => v.ToString(CultureInfo.InvariantCulture);

        private static double Num(string? s, double fallback = 0)
            => double.TryParse((s ?? "").Replace(',', '.'), NumberStyles.Float, CultureInfo.InvariantCulture, out var v) ? v : fallback;

        private static bool IsValidHost(string? s)
            => !string.IsNullOrWhiteSpace(s) && Uri.CheckHostName(s.Trim()) != UriHostNameType.Unknown;

        private static bool IsHttpUrl(string s)
            => Uri.TryCreate(s, UriKind.Absolute, out var u) && (u.Scheme == "http" || u.Scheme == "https");

        // Blockt Aktionen, die erkennbar von einer fremden Webseite ausgelöst wurden
        private static bool IsForeignReferer(HttpListenerRequest r)
        {
            var referer = r.UrlReferrer;
            if (referer == null || r.Url == null) return false;
            return !string.Equals(referer.Authority, r.Url.Authority, StringComparison.OrdinalIgnoreCase);
        }

        private static string Opt(string value, string text, string current)
            => $"<option value='{value}' {(value == current ? "selected" : "")}>{text}</option>";

        private static string DelaySelect(string name, int current)
        {
            var sb = new StringBuilder($"<select name='{name}'>");
            foreach (var v in new[] { 0, 1, 3, 5, 10 }.Union(new[] { current }).OrderBy(x => x))
                sb.Append($"<option value='{v}' {(v == current ? "selected" : "")}>{v}</option>");
            return sb.Append("</select>").ToString();
        }

        private string PriorityDropdown(ShellyDevice? d)
        {
            var used = _deviceManager.GetAllDevices()
                .Where(x => d == null || x.Id != d.Id)
                .Select(x => x.Priority).ToHashSet();

            int selected = d?.Priority ?? Enumerable.Range(1, 10).FirstOrDefault(i => !used.Contains(i));

            var sb = new StringBuilder();
            sb.Append("<label>Priorität (1 = Höchste, wird zuerst bedient):</label><select name='priority' required>");
            for (int i = 1; i <= 10; i++)
            {
                bool isUsed = used.Contains(i);
                sb.Append($"<option value='{i}' {(isUsed ? "disabled" : "")} {(i == selected ? "selected" : "")}>Priorität {i}{(isUsed ? " (Bereits vergeben)" : "")}</option>");
            }
            sb.Append("</select>");
            return sb.ToString();
        }

        private static string Page(string title, string accent, int maxWidth, string body, string script = "")
        {
            return $@"<!DOCTYPE html>
<html lang='de'><head><meta charset='UTF-8'><meta name='viewport' content='width=device-width, initial-scale=1'>
<title>{title}</title>
<style>
body {{ font-family: Arial, sans-serif; background-color: #f4f6f9; padding: 30px; color: #333; }}
.card {{ background: white; max-width: {maxWidth}px; margin: 0 auto; padding: 30px; border-radius: 12px; box-shadow: 0 4px 15px rgba(0,0,0,0.1); }}
h1, h2 {{ color: {accent}; margin-top: 0; }}
label {{ font-weight: bold; display: block; margin-top: 15px; margin-bottom: 5px; }}
input, select {{ width: 100%; padding: 10px; border: 1px solid #ccc; border-radius: 5px; box-sizing: border-box; }}
.row {{ display: flex; gap: 15px; }}
.col {{ flex: 1; }}
.group {{ margin-top: 20px; padding: 15px; background: #f8f9fa; border-radius: 8px; }}
.btn-save {{ background-color: {accent}; color: white; padding: 15px; width: 100%; border: none; border-radius: 8px; font-size: 18px; font-weight: bold; margin-top: 20px; cursor: pointer; }}
.info-box {{ background-color: #fff3cd; border-left: 4px solid #ffc107; padding: 15px; margin: 15px 0; font-size: 14px; border-radius: 4px; line-height: 1.5; }}
.back-link {{ display: block; text-align: center; margin-top: 20px; color: #666; text-decoration: none; }}
.btn {{ display: block; width: 100%; padding: 15px; margin: 15px 0; font-size: 18px; font-weight: bold; color: white; border-radius: 8px; text-decoration: none; box-sizing: border-box; text-align: center; }}
</style>
<script>{script}</script></head>
<body><div class='card'>{body}<a href='/' class='back-link'>Abbrechen &amp; Zurück</a></div></body></html>";
        }

        private static string ErrorPage(string message)
            => Page("Fehler", "#dc3545", 500, $"<h2>⚠️ Fehler</h2><p>{H(message)}</p>");

        // =====================================================================
        // DASHBOARD
        // =====================================================================
        private const string DashboardCss = @"
body { font-family: Arial, sans-serif; background-color: #f4f6f9; padding: 30px; color: #333; }
.card { background: white; max-width: 700px; margin: 0 auto; padding: 30px; border-radius: 12px; box-shadow: 0 4px 15px rgba(0,0,0,0.1); }
.header-bar { display: flex; justify-content: space-between; align-items: center; border-bottom: 2px solid #eee; padding-bottom: 10px; margin-bottom: 20px; }
h1 { color: #0078D7; margin: 0; }
.metric-container { display: flex; gap: 10px; margin-bottom: 25px; flex-wrap: wrap; }
.metric-box { background: #f8f9fa; padding: 15px; border-radius: 8px; flex: 1; min-width: 100px; text-align: center; border: 1px solid #e3e6f0; }
.metric-box span { display: block; font-size: 12px; color: #666; margin-bottom: 5px; text-transform: uppercase; letter-spacing: 0.5px; font-weight: bold; }
.metric-box b { font-size: 20px; color: #0078D7; }
.phase-info { font-size: 10px; color: #888; margin-top: 5px; }
.device-item { border: 1px solid #ddd; border-radius: 8px; padding: 15px; margin-bottom: 15px; background: #fff; box-shadow: 0 2px 5px rgba(0,0,0,0.05); }
.device-header { display: flex; justify-content: space-between; align-items: center; }
.rule-box { background: #f8f9fa; padding: 10px; border-radius: 5px; margin-top: 10px; font-size: 14px; border-left: 4px solid #0078D7; }
.btn-mode { padding: 5px 10px; border: 1px solid #ccc; border-radius: 5px; text-decoration: none; color: #333; font-size: 12px; margin-right: 5px; background: #fff; }
.btn-mode.active { background: #0078D7; color: white; border-color: #0078D7; font-weight: bold; }
.btn-add { display: block; background-color: #28a745; color: white; padding: 15px; text-align: center; border-radius: 8px; text-decoration: none; font-weight: bold; margin-top: 30px; }
.btn-settings { background-color: #6c757d; color: white; padding: 8px 15px; border-radius: 5px; text-decoration: none; font-size: 14px; font-weight: bold; }
.btn-delete { color: #dc3545; text-decoration: none; font-weight: bold; font-size: 14px; }
.btn-edit { color: #e0a800; text-decoration: none; font-weight: bold; font-size: 14px; margin-right: 15px; }
.banner { padding: 15px; border-radius: 8px; margin-bottom: 20px; font-weight: bold; text-align: center; }
.banner-red { background-color: #ffcccc; color: #cc0000; }
.banner-orange { background-color: #ffe5b4; color: #8a4b00; }
";

        private static string WallboxName(string model) => model switch
        {
            "goe" => "go-e Charger",
            "keba" => "KEBA KeContact",
            "openwb" => "OpenWB Serie 1",
            "openwb2" => "OpenWB Serie 2",
            _ => "Heidelberg Energy Control"
        };

        private static string ControlModeText(string mode) => mode switch
        {
            "soc" => "Batteriestand (Feste Stufen)",
            "soc_pv" => "Batteriestand + PV-Überschuss",
            _ => "Netz-Überschuss"
        };

        private string RenderDeviceCard(ShellyDevice d)
        {
            string status = d.IsOn ? "<span style='color: green;'>🟢 AKTIV</span>" : "<span style='color: red;'>🔴 INAKTIV</span>";
            string modeName;
            string ruleText;

            if (d.SystemType == "cascade")
            {
                status = $"<span style='color: #0078D7; font-weight: bold;'>🌊 Stufe {d.CurrentCascadeStep} aktiv</span>";
                modeName = "🌡️ 3-Phasen Kaskade";
                string unit = d.ControlMode == "soc" ? "%" : "W";
                string modeStr = d.ControlMode == "soc" ? "Batterie (SOC)" : "Netz-Überschuss";
                ruleText = $"<b>Regelung nach:</b> {modeStr}<br><b>Stufe 1 ab:</b> {N(d.OnValue)} {unit}<br><b>Stufe 2 ab:</b> {N(d.OnValue2)} {unit}<br><b>Stufe 3 ab:</b> {N(d.OnValue3)} {unit}<br><b>Aus (Stufe runter) &lt;</b> {N(d.OffValue)} {unit}";
            }
            else if (d.SystemType == "wallbox")
            {
                modeName = "🚗 " + WallboxName(d.DeviceModel);
                string delayText = d.ControlMode == "grid" ? $"<br><small>Verzögerung: {d.OnDelayMinutes} Min Ein / {d.OffDelayMinutes} Min Aus</small>" : "";
                ruleText = $"<b>Regelung nach:</b> {ControlModeText(d.ControlMode)}<br><i>Lädt stufenlos basierend auf Vorgabe.</i>{delayText}";
            }
            else if (d.SystemType == "heater")
            {
                modeName = "🔥 " + (d.DeviceModel == "mypv" ? "my-PV Heizstab" : "Heizstab");
                string delayText = d.ControlMode == "grid" ? $"<br><small>Verzögerung: {d.OnDelayMinutes} Min Ein / {d.OffDelayMinutes} Min Aus</small>" : "";
                ruleText = $"<b>Regelung nach:</b> {ControlModeText(d.ControlMode)}<br><i>Wandelt Energie stufenlos in Wärme um.</i>{delayText}";
            }
            else
            {
                string typeName = d.DeviceModel == "custom" ? " (Benutzerdefiniert)" : "";
                modeName = (d.SystemType == "grid" ? "⚡ Überschuss-Regel" : "🔋 Batterie-Regel") + typeName;
                ruleText = d.SystemType == "grid"
                    ? $"<b>Ein:</b> Einspeisung &gt; {N(d.OnValue)} W ({d.OnDelayMinutes} Min)<br><b>Aus:</b> Einspeisung &lt; {N(d.OffValue)} W ({d.OffDelayMinutes} Min)"
                    : $"<b>Ein:</b> SOC &gt; {N(d.OnValue)} %<br><b>Aus:</b> SOC &lt; {N(d.OffValue)} %";
            }

            string btnAuto = d.ManualOverride == "auto" ? "active" : "";
            string btnOn = d.ManualOverride == "on" ? "active" : "";
            string btnOff = d.ManualOverride == "off" ? "active" : "";

            string ips = d.SystemType == "cascade"
                ? $"Stufen-IPs: {H(d.IpAddress)} / {H(d.IpAddress2)} / {H(d.IpAddress3)}"
                : H(d.IpAddress);
            string headerInfo = $"<b>[Prio {d.Priority}]</b> | {ips}";

            return $@"
<div class='device-item'>
    <div class='device-header'>
        <div>
            <h3 style='margin: 0; color: #333;'>{H(d.Name)}</h3>
            <small style='color: #666;'>{headerInfo} | {modeName}</small>
        </div>
        <div style='font-weight: bold; font-size: 18px;'>{status}</div>
    </div>
    <div class='rule-box'>{ruleText}</div>
    <div style='margin-top: 15px; border-top: 1px solid #eee; padding-top: 10px; display: flex; align-items: center; justify-content: space-between;'>
        <div>
            <span style='font-size: 12px; color: #666; margin-right: 10px;'>Betriebsmodus:</span>
            <a href='/setmode?id={H(d.Id)}&amp;mode=auto' class='btn-mode {btnAuto}'>🤖 Auto</a>
            <a href='/setmode?id={H(d.Id)}&amp;mode=on' class='btn-mode {btnOn}'>🟢 Dauer-An</a>
            <a href='/setmode?id={H(d.Id)}&amp;mode=off' class='btn-mode {btnOff}'>🔴 Dauer-Aus</a>
        </div>
        <div style='text-align: right;'>
            <a href='/edit?id={H(d.Id)}' class='btn-edit'>✏️ Bearbeiten</a>
            <a href='/delete?id={H(d.Id)}' class='btn-delete' onclick='return confirm(&quot;Wirklich löschen?&quot;)'>🗑️ Löschen</a>
        </div>
    </div>
</div>";
        }

        private string RenderDashboard()
        {
            var devices = _deviceManager.GetAllDevices().OrderBy(d => d.Priority).ToList();
            var list = new StringBuilder();

            if (devices.Count == 0)
                list.Append("<p style='color: #666; margin: 30px 0;'>Noch keine Geräte angelegt.</p>");
            else
                foreach (var d in devices) list.Append(RenderDeviceCard(d));

            string banners = "";
            bool hasIp = !string.IsNullOrWhiteSpace(_deviceManager.Settings.VictronIp);

            if (!hasIp)
            {
                banners += "<div class='banner banner-red'>⚠️ Achtung: Es wurde noch keine Victron IP-Adresse hinterlegt. Bitte klicke auf '⚙️ System Einstellungen'.</div>";
            }
            else
            {
                var last = _deviceManager.LastLiveUpdate;
                if (last == DateTime.MinValue)
                    banners += "<div class='banner banner-orange'>⏳ Noch keine Messwerte vom Victron empfangen. Die Geräte werden nicht gesteuert.</div>";
                else
                {
                    int age = (int)(DateTime.UtcNow - last).TotalSeconds;
                    if (age > 30)
                        banners += $"<div class='banner banner-orange'>⚠️ Keine aktuellen Messwerte vom Victron (seit {age} s). Angezeigte Werte sind veraltet, die Steuerung pausiert.</div>";
                }
            }

            return $@"<!DOCTYPE html>
<html lang='de'>
<head>
    <meta charset='UTF-8'>
    <meta name='viewport' content='width=device-width, initial-scale=1'>
    <meta http-equiv='refresh' content='5'>
    <title>PVManager - Dashboard</title>
    <style>{DashboardCss}</style>
</head>
<body>
    <div class='card'>
        {banners}
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
        {list}
        <a href='/setup' class='btn-add'>➕ Neues Gerät hinzufügen</a>
    </div>
</body>
</html>";
        }

        // =====================================================================
        // EINSTELLUNGEN / SETUP
        // =====================================================================
        private string RenderSettings()
        {
            string body = $@"<h2 style='color:#6c757d; border-bottom: 2px solid #eee; padding-bottom: 10px;'>⚙️ Allgemeine Einstellungen</h2>
<form action='/savesettings' method='GET'>
<label>IP-Adresse des Victron Cerbo:</label>
<input type='text' name='victron_ip' value='{H(_deviceManager.Settings.VictronIp)}' placeholder='z.B. 192.168.178.57' required>
<button type='submit' class='btn-save'>💾 Speichern</button>
</form>";
            return Page("PVManager - Einstellungen", "#6c757d", 500, body);
        }

        private string RenderSetup()
        {
            string body = @"<div style='text-align:center'>
<h1>Neues Gerät</h1>
<a href='/mode?type=wallbox' class='btn' style='background-color:#17a2b8'>🚗 Wallbox</a>
<a href='/mode?type=heater' class='btn' style='background-color:#dc3545'>🔥 my-PV Heizstab (Stufenlos)</a>
<a href='/mode?type=cascade' class='btn' style='background-color:#fd7e14'>🌡️ 3-Phasen Kaskaden-Heizstab</a>
<a href='/mode?type=grid' class='btn' style='background-color:#28a745'>⚡ Smart-Gerät (Netz-Regel)</a>
<a href='/mode?type=island' class='btn' style='background-color:#ffc107; color:#333'>🔋 Smart-Gerät (Batterie-Regel)</a>
</div>";
            return Page("PVManager - Setup", "#0078D7", 500, body);
        }

        private string? RenderAddForm(string? type) => type switch
        {
            "cascade" => CascadePage(null),
            "wallbox" => WallboxHeaterPage("wallbox", null),
            "heater" => WallboxHeaterPage("heater", null),
            "grid" => SwitchPage("grid", null),
            "island" => SwitchPage("island", null),
            _ => null
        };

        private string? RenderEdit(string? id)
        {
            var d = _deviceManager.GetAllDevices().FirstOrDefault(x => x.Id == id);
            if (d == null) return null;

            return d.SystemType switch
            {
                "cascade" => CascadePage(d),
                "wallbox" => WallboxHeaterPage("wallbox", d),
                "heater" => WallboxHeaterPage("heater", d),
                _ => SwitchPage(d.SystemType == "island" ? "island" : "grid", d)
            };
        }

        // =====================================================================
        // FORMULARE (Anlegen und Bearbeiten teilen sich den Code; d == null -> neu)
        // =====================================================================
        private string CascadePage(ShellyDevice? d)
        {
            bool soc = d?.ControlMode == "soc";
            string unit = soc ? "%" : "W";
            string minOn = soc ? "min='0' max='100'" : "min='1'";
            string minOff = soc ? "min='0' max='100'" : "";

            string Row(int n, string ipName, string ipVal, string valName, string valId, double val) =>
                $"<div class='row group'><div class='col'><label>Stufe {n} IP:</label><input type='text' name='{ipName}' value='{H(ipVal)}' required></div>" +
                $"<div class='col'><label id='lbl{n}'>Ein ab ({unit})</label><input type='number' step='any' name='{valName}' id='{valId}' value='{N(val)}' {minOn} required></div></div>";

            string info = d != null ? "" : @"<div class='info-box'>
<b>ℹ️ So stellst du die Kaskade ein:</b><br><br>
<b>Option A: Netz-Regelung (Überschuss in Watt)</b><br>
Trage <b>etwas mehr</b> ein, als deine Phase verbraucht (z.B. Heizstab zieht 1000 W ➔ trage <b>1100</b> ein).<br>
<i>Warum?</i> Zähler zeigt mehr als 1100 W Einspeisung ➔ Stufe 1 AN. Der Stab zieht 1000 W weg, der Zähler zeigt danach noch 100 W (alles bleibt stabil).<br>
<b>Aus:</b> Fällt der Überschuss unter den Aus-Wert, wird eine Stufe zurückgeschaltet. Negative Werte bedeuten Netzbezug (z.B. -100 = erst bei 100 W Bezug abschalten).<br><br>
<b>Option B: Batterie-Regelung (SOC in %)</b><br>
Trage feste Stufen ein (z.B. Ein ab <b>90</b>, Stufe 2 ab <b>95</b>, Stufe 3 ab <b>98</b>). Runterschalten erfolgt mit 2 % Hysterese.<br><br>
<b>⏱️ Schaltzeiten:</b> fest einprogrammiert: <b>1 Minute</b> beim Hochschalten, <b>2 Minuten</b> beim Runterschalten (Wolkenschutz).
</div>";

            string body = $@"<h2>🌡️ 3-Phasen Kaskade {(d == null ? "hinzufügen" : "bearbeiten")}</h2>
{info}
<form action='/save' method='GET'>
<input type='hidden' name='id' value='{H(d?.Id)}'><input type='hidden' name='type' value='cascade'><input type='hidden' name='model' value='switch'>
<label>Regelung nach:</label>
<select name='control_mode' id='cm_casc' onchange='toggleCascade(true)'>
{Opt("grid", "PV-Überschuss (Einspeisung ins Netz)", d?.ControlMode ?? "grid")}
{Opt("soc", "Batteriestand (SOC %)", d?.ControlMode ?? "grid")}
</select>
{PriorityDropdown(d)}
<label>Name der Kaskade:</label><input type='text' name='name' value='{H(d?.Name == "Neues Gerät" ? "" : d?.Name)}' required>
{Row(1, "ip", d?.IpAddress ?? "", "on_watt", "ow1", d?.OnValue ?? 1100)}
{Row(2, "ip2", d?.IpAddress2 ?? "", "on_watt2", "ow2", d?.OnValue2 ?? 1100)}
{Row(3, "ip3", d?.IpAddress3 ?? "", "on_watt3", "ow3", d?.OnValue3 ?? 1100)}
<div class='row' style='margin-top: 20px;'><div class='col'>
<label id='lbl_off'>Aus (Stufen runterschalten) &lt; ({unit})</label>
<input type='number' step='any' name='off_watt' id='owoff' value='{N(d?.OffValue ?? 10)}' {minOff} required>
</div></div>
<button type='submit' class='btn-save'>💾 {(d == null ? "Kaskade speichern" : "Änderungen speichern")}</button>
</form>";

            const string script = @"
function toggleCascade(setDefaults) {
    var soc = document.getElementById('cm_casc').value === 'soc';
    var unit = soc ? '%' : 'W';
    ['lbl1','lbl2','lbl3'].forEach(function(id){ document.getElementById(id).innerText = 'Ein ab (' + unit + ')'; });
    document.getElementById('lbl_off').innerText = 'Aus (Stufen runterschalten) < (' + unit + ')';
    ['ow1','ow2','ow3'].forEach(function(id){
        var el = document.getElementById(id);
        el.min = soc ? 0 : 1;
        if (soc) el.max = 100; else el.removeAttribute('max');
    });
    var off = document.getElementById('owoff');
    if (soc) { off.min = 0; off.max = 100; } else { off.removeAttribute('min'); off.removeAttribute('max'); }
    if (setDefaults) {
        var vals = soc ? [90, 95, 98, 85] : [1100, 1100, 1100, 10];
        ['ow1','ow2','ow3','owoff'].forEach(function(id, i){ document.getElementById(id).value = vals[i]; });
    }
}
window.onload = function(){ toggleCascade(false); };
";
            return Page("PVManager - Kaskade", "#fd7e14", 700, body, script);
        }

        private string WallboxHeaterPage(string type, ShellyDevice? d)
        {
            bool wb = type == "wallbox";
            string accent = wb ? "#17a2b8" : "#dc3545";
            string mode = d?.ControlMode ?? "grid";
            string model = d?.DeviceModel ?? "goe";

            string modelHtml = wb
                ? "<label>Hersteller / Modell:</label><select name='model'>" +
                  Opt("goe", "go-e Charger (HTTP API v2)", model) +
                  Opt("keba", "KEBA KeContact P30/P40 (UDP)", model) +
                  Opt("heidelberg", "Heidelberg Energy Control (Modbus TCP)", model) +
                  Opt("openwb", "OpenWB Serie 1 (MQTT)", model) +
                  Opt("openwb2", "OpenWB Serie 2 (MQTT)", model) +
                  "</select>"
                : "<input type='hidden' name='model' value='mypv'>";

            string info = wb ? @"<div class='info-box'><b>ℹ️ Info zur Ladeleistung:</b> Die Wallbox regelt aus Sicherheitsgründen bis <b>maximal 16 Ampere</b> (ca. 11 kW), das technische Minimum liegt bei 6 A. Im reinen Batterie-Modus (feste Stufen) lädt das Programm mit 16 A (voll) oder 10 A (gedrosselt). Die Überschuss-Berechnung geht von 3-phasigem Laden aus (690 W je Ampere).</div>" : "";

            string body = $@"<h2>{(wb ? "🚗 Wallbox" : "🔥 my-PV Heizstab")} {(d == null ? "hinzufügen" : "bearbeiten")}</h2>
{info}
<form action='/save' method='GET'>
<input type='hidden' name='id' value='{H(d?.Id)}'><input type='hidden' name='type' value='{type}'>
{modelHtml}
<label>Regelung nach:</label>
<select name='control_mode' id='cm' onchange='t()'>
{Opt("grid", "PV-Überschuss (Einspeisung ins Netz)", mode)}
{Opt("soc_pv", "Batteriestand + PV-Überschuss (Stufenlos)", mode)}
{Opt("soc", "Batteriestand (Feste Stufen)", mode)}
</select>
<div class='row' id='sf'>
<div class='col'><label>Start ab SOC (%)</label><input type='number' step='any' name='on_soc' value='{N(d?.OnValue ?? 95)}' min='0' max='100'></div>
<div class='col'><label>Stopp ab SOC (%)</label><input type='number' step='any' name='off_soc' value='{N(d?.OffValue ?? 90)}' min='0' max='100'></div>
</div>
<div class='row' id='tf' style='margin-top:20px;'>
<div class='col'><label>Start-Verzögerung (Minuten)</label>{DelaySelect("on_time", d?.OnDelayMinutes ?? 5)}</div>
<div class='col'><label>Stopp-Verzögerung (Minuten)</label>{DelaySelect("off_time", d?.OffDelayMinutes ?? 10)}</div>
</div>
{PriorityDropdown(d)}
<label>Name:</label><input type='text' name='name' value='{H(d?.Name == "Neues Gerät" ? "" : d?.Name)}' required>
<label>{(wb ? "Geräte IP" : "my-PV IP-Adresse")}:</label><input type='text' name='ip' value='{H(d?.IpAddress)}' required>
<button type='submit' class='btn-save'>💾 {(d == null ? "Speichern" : "Änderungen speichern")}</button>
</form>";

            const string script = @"
function t() {
    var m = document.getElementById('cm').value;
    document.getElementById('sf').style.display = (m === 'grid') ? 'none' : 'flex';
    document.getElementById('tf').style.display = (m === 'grid') ? 'flex' : 'none';
}
window.onload = t;
";
            return Page(wb ? "PVManager - Wallbox" : "PVManager - Heizstab", accent, 600, body, script);
        }

        private string SwitchPage(string type, ShellyDevice? d)
        {
            bool grid = type == "grid";
            string accent = grid ? "#28a745" : "#ffc107";
            string model = d?.DeviceModel ?? "switch";
            string customStyle = model == "custom" ? "block" : "none";

            string onField, offField;
            if (grid)
            {
                onField = $@"<div class='row'>
<div class='col'><label>Ein wenn Einspeisung &gt; (W)</label><input type='number' step='any' name='on_watt' value='{N(d?.OnValue ?? 200)}' min='1' required></div>
<div class='col'><label>Dauer (Minuten)</label>{DelaySelect("on_time", d?.OnDelayMinutes ?? 5)}</div></div>";
                offField = $@"<div class='row' style='margin-top:20px;'>
<div class='col'><label>Aus wenn Einspeisung &lt; (W)</label><input type='number' step='any' name='off_watt' value='{N(d?.OffValue ?? 10)}' required></div>
<div class='col'><label>Dauer (Minuten)</label>{DelaySelect("off_time", d?.OffDelayMinutes ?? 10)}</div></div>
<small style='color:#666'>Negative Werte = Netzbezug (z.B. -100: erst bei 100 W Netzbezug ausschalten).</small>";
            }
            else
            {
                onField = $"<div class='row'><div class='col'><label>Ein wenn SOC &gt; (%)</label><input type='number' step='any' name='on_soc' value='{N(d?.OnValue ?? 95)}' min='0' max='100' required></div></div>";
                offField = $"<div class='row' style='margin-top:20px;'><div class='col'><label>Aus wenn SOC &lt; (%)</label><input type='number' step='any' name='off_soc' value='{N(d?.OffValue ?? 90)}' min='0' max='100' required></div></div>";
            }

            string body = $@"<h2>{(grid ? "⚡ Überschuss-Regel (Watt)" : "🔋 Batterie-Regel (SOC %)")} {(d == null ? "" : "bearbeiten")}</h2>
<form action='/save' method='GET'>
<input type='hidden' name='id' value='{H(d?.Id)}'><input type='hidden' name='type' value='{type}'>
{onField}
{offField}
<label>Geräte-Typ (Modell):</label>
<select name='model' id='device_model' onchange='t_mod()'>
{Opt("switch", "Shelly / Tasmota (Standard)", model)}
{Opt("custom", "Benutzerdefiniert (HTTP-URLs)", model)}
</select>
{PriorityDropdown(d)}
<label>Name:</label><input type='text' name='name' value='{H(d?.Name == "Neues Gerät" ? "" : d?.Name)}' required>
<label>Geräte IP:</label><input type='text' name='ip' value='{H(d?.IpAddress ?? "0.0.0.0")}' required>
<div id='custom_url_fields' style='display:{customStyle};'>
<label>HTTP URL für EIN (inkl. http://):</label><input type='text' name='custom_on' value='{H(d?.CustomOnUrl)}'>
<label>HTTP URL für AUS (inkl. http://):</label><input type='text' name='custom_off' value='{H(d?.CustomOffUrl)}'>
</div>
<button type='submit' class='btn-save'>💾 {(d == null ? "Speichern" : "Änderungen speichern")}</button>
</form>";

            const string script = @"
function t_mod() {
    var m = document.getElementById('device_model').value;
    document.getElementById('custom_url_fields').style.display = (m === 'custom') ? 'block' : 'none';
}
window.onload = t_mod;
";
            return Page(grid ? "PVManager - Netz Setup" : "PVManager - Insel Setup", accent, 600, body, script);
        }

        // =====================================================================
        // AKTIONEN (liefern null bei Erfolg -> Redirect, sonst Fehlerseite)
        // =====================================================================
        private string? SetMode(NameValueCollection q)
        {
            string? id = q["id"];
            string? mode = q["mode"];
            if (string.IsNullOrEmpty(id) || !(mode is "auto" or "on" or "off")) return null;

            var target = _deviceManager.GetAllDevices().FirstOrDefault(d => d.Id == id);
            if (target != null) { target.ManualOverride = mode; _deviceManager.SaveDevices(); }
            return null;
        }

        private string? SaveSettings(NameValueCollection q)
        {
            string ip = (q["victron_ip"] ?? "").Trim();
            if (string.IsNullOrEmpty(ip)) return null;
            if (!IsValidHost(ip)) return ErrorPage($"Ungültige IP-Adresse / Host: {ip}");

            _deviceManager.Settings.VictronIp = ip;
            _deviceManager.SaveSettings();
            return null;
        }

        private string? Delete(NameValueCollection q)
        {
            string? id = q["id"];
            if (!string.IsNullOrEmpty(id)) _deviceManager.DeleteDevice(id);
            return null;
        }

        private string? Save(NameValueCollection q)
        {
            string type = q["type"] ?? "grid";
            string model = q["model"] ?? "switch";
            string mode = q["control_mode"] ?? "grid";
            string name = (q["name"] ?? "").Trim();
            string ip = (q["ip"] ?? "").Trim();
            string ip2 = (q["ip2"] ?? "").Trim();
            string ip3 = (q["ip3"] ?? "").Trim();
            string customOn = (q["custom_on"] ?? "").Trim();
            string customOff = (q["custom_off"] ?? "").Trim();
            string? id = q["id"];

            // --- Validierung ---
            if (!ValidTypes.Contains(type)) return ErrorPage("Ungültiger Gerätetyp.");
            if (!ValidModels.Contains(model)) return ErrorPage("Ungültiges Gerätemodell.");
            if (!ValidModes.Contains(mode)) return ErrorPage("Ungültiger Regelungsmodus.");
            if (name.Length == 0) return ErrorPage("Bitte einen Namen angeben.");

            bool custom = model == "custom";
            if (!custom && !IsValidHost(ip)) return ErrorPage($"Ungültige IP-Adresse / Host: {ip}");
            if (type == "cascade" && (!IsValidHost(ip2) || !IsValidHost(ip3)))
                return ErrorPage("Ungültige IP-Adresse bei Stufe 2 oder 3.");
            if (custom && ((customOn.Length > 0 && !IsHttpUrl(customOn)) || (customOff.Length > 0 && !IsHttpUrl(customOff))))
                return ErrorPage("Eigene URLs müssen mit http:// oder https:// beginnen.");

            var devices = _deviceManager.GetAllDevices();
            var existing = string.IsNullOrEmpty(id) ? null : devices.FirstOrDefault(x => x.Id == id);
            bool isNew = existing == null;
            var t = existing ?? new ShellyDevice();

            int prio;
            if (int.TryParse(q["priority"], out int p) && p >= 1 && p <= 10)
            {
                if (devices.Any(x => x.Id != t.Id && x.Priority == p)) return ErrorPage($"Priorität {p} ist bereits vergeben.");
                prio = p;
            }
            else if (!isNew) prio = t.Priority;
            else
            {
                var used = devices.Select(x => x.Priority).ToHashSet();
                prio = Enumerable.Range(1, 10).FirstOrDefault(i => !used.Contains(i));
                if (prio == 0) prio = 99;
            }

            // --- Übernehmen ---
            t.Name = name;
            t.IpAddress = custom && ip.Length == 0 ? "0.0.0.0" : ip;
            t.SystemType = type;
            t.DeviceModel = model;
            t.ControlMode = mode;
            t.CustomOnUrl = customOn;
            t.CustomOffUrl = customOff;
            t.Priority = prio;

            if (type == "cascade")
            {
                bool soc = mode == "soc";
                double on1 = Num(q["on_watt"]), on2 = Num(q["on_watt2"]), on3 = Num(q["on_watt3"]), off = Num(q["off_watt"]);
                if (soc)
                {
                    on1 = Math.Clamp(on1, 0, 100); on2 = Math.Clamp(on2, 0, 100);
                    on3 = Math.Clamp(on3, 0, 100); off = Math.Clamp(off, 0, 100);
                }
                if (on2 <= on1) on2 = on1 + 1;
                if (on3 <= on2) on3 = on2 + 1;
                if (off >= on1) off = on1 - 1;

                t.IpAddress2 = ip2;
                t.IpAddress3 = ip3;
                t.OnValue = on1; t.OnValue2 = on2; t.OnValue3 = on3; t.OffValue = off;
                t.OnDelayMinutes = 1;
                t.OffDelayMinutes = 2;
            }
            else
            {
                if (type == "grid")
                {
                    double on = Num(q["on_watt"]), off = Num(q["off_watt"]);
                    if (off >= on) off = on - 1;
                    t.OnValue = on; t.OffValue = off;
                }
                else // island, wallbox, heater
                {
                    double on = Math.Clamp(Num(q["on_soc"]), 0, 100), off = Math.Clamp(Num(q["off_soc"]), 0, 100);
                    if (off >= on) off = on - 1;
                    t.OnValue = on; t.OffValue = off;
                }

                int.TryParse(q["on_time"], out int onTime);
                int.TryParse(q["off_time"], out int offTime);
                t.OnDelayMinutes = Math.Clamp(onTime, 0, 60);
                t.OffDelayMinutes = Math.Clamp(offTime, 0, 60);
            }

            if (isNew) _deviceManager.AddDevice(t);
            else _deviceManager.SaveDevices();

            return null;
        }
    }
}
