using System;
using System.Threading.Tasks;

namespace VictronEasyManager
{
    class Program
    {
        static async Task Main(string[] args)
        {
            Console.WriteLine("Starte PVManager...");

            // 1. Das Gedächtnis (Speicher) starten
            DeviceManager deviceManager = new DeviceManager();

            // 2. Den Webserver starten (und ihm das Gedächtnis mitgeben)
            WebRenderer webServer = new WebRenderer(deviceManager);
            _ = webServer.StartAsync(); 

            // 3. Das Gehirn (die Logik-Schleife) starten (und ihm auch das Gedächtnis mitgeben)
            AutomationEngine engine = new AutomationEngine(deviceManager);
            _ = engine.StartAsync();

            Console.WriteLine("Drücke ENTER, um das Programm zu beenden...");
            Console.ReadLine();
        }
    }
}
