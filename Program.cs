using System;
using System.Threading;
using System.Threading.Tasks;

namespace VictronEasyManager
{
    class Program
    {
        static async Task<int> Main(string[] args)
        {
            Console.WriteLine("Starte PVManager...");

            var deviceManager = new DeviceManager();
            var webTask    = Task.Run(new WebRenderer(deviceManager).StartAsync);
            var engineTask = Task.Run(new AutomationEngine(deviceManager).StartAsync);

            Task exitTask = Console.IsInputRedirected
                ? Task.Delay(Timeout.Infinite)
                : Task.Run(() => { Console.WriteLine("Drücke ENTER, um zu beenden..."); Console.ReadLine(); });

            var finished = await Task.WhenAny(webTask, engineTask, exitTask);
            if (finished == exitTask) return 0;

            // Webserver oder Engine sind gestorben -> laut melden, systemd startet neu
            Console.WriteLine($"[FATAL] {finished.Exception?.GetBaseException().Message ?? "Task unerwartet beendet"}");
            return 1;
        }
    }
}
