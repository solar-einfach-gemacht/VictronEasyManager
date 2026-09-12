using System;
using System.Threading;
using System.Threading.Tasks;
using MQTTnet;
using MQTTnet.Client;

namespace VictronEasyManager
{
    // openWB Software-Serie 2.x ("openWB series2") - komplett neue Topic-Struktur
    // gegenueber der alten PHP-basierten Serie 1.x.
    public class OpenWbManagerV2
    {
        private readonly string _brokerIp;

        // Angenommen wird Fahrzeug-/Lade-Profil-ID 1 (Standard bei den meisten Installationen
        // mit nur einem Ladepunkt/Fahrzeug). Falls bei dir eine andere ID verwendet wird
        // (in der openWB-Oberflaeche unter "Ladepunkte"/"Fahrzeuge" sichtbar), hier anpassen.
        private const string ChargeTemplateId = "1";

        public OpenWbManagerV2(string brokerIp)
        {
            _brokerIp = brokerIp;
        }

        private async Task SendeBefehlAsync(string topic, string payload)
        {
            var factory = new MqttFactory();
            using var mqttClient = factory.CreateMqttClient();

            var options = new MqttClientOptionsBuilder()
                .WithTcpServer(_brokerIp, 1883)
                .WithTimeout(TimeSpan.FromSeconds(5))
                .Build();

            try
            {
                await mqttClient.ConnectAsync(options, CancellationToken.None);

                var message = new MqttApplicationMessageBuilder()
                    .WithTopic(topic)
                    .WithPayload(payload)
                    .WithQualityOfServiceLevel(MQTTnet.Protocol.MqttQualityOfServiceLevel.AtLeastOnce)
                    .Build();

                await mqttClient.PublishAsync(message, CancellationToken.None);

                await mqttClient.DisconnectAsync();

                Console.WriteLine($"[OpenWB v2] Befehl gesendet: {payload} an {topic}");
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[OpenWB v2 Fehler] Konnte Befehl nicht senden: {ex.Message}");
            }
        }

        private static string ModeTopic() =>
            $"openWB/set/vehicle/template/charge_template/{ChargeTemplateId}/chargemode/selected";

        private static string CurrentTopic() =>
            $"openWB/set/vehicle/template/charge_template/{ChargeTemplateId}/chargemode/instant_charging/current";

        public async Task StarteLaden()
        {
            // "instant_charging" entspricht dem Lademodus "Sofort laden" in Serie 2.x
            await SendeBefehlAsync(ModeTopic(), "instant_charging");
        }

        public async Task StoppeLaden()
        {
            await SendeBefehlAsync(ModeTopic(), "stop");
        }

        public async Task SetzeAmpere(int ampere)
        {
            await SendeBefehlAsync(CurrentTopic(), ampere.ToString());
        }
    }
}
