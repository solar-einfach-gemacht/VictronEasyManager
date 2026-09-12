using System;
using System.Threading;
using System.Threading.Tasks;
using MQTTnet;
using MQTTnet.Client;

namespace VictronEasyManager
{
    // openWB Software-Serie 1.x (aeltere, PHP-basierte Software, bis ca. Version 1.9x)
    public class OpenWbManager
    {
        private readonly string _brokerIp;

        public OpenWbManager(string brokerIp)
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
                
                Console.WriteLine($"[OpenWB v1] Befehl gesendet: {payload} an {topic}");
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[OpenWB v1 Fehler] Konnte Befehl nicht senden: {ex.Message}");
            }
        }

        public async Task StarteLaden()
        {
            // "Sofort laden" muss aktiv sein, sonst wird DirectChargeAmps von der openWB ignoriert.
            await SendeBefehlAsync("openWB/set/ChargeMode", "0");
            await SendeBefehlAsync("openWB/set/lp1/ChargePointEnabled", "1");
        }

        public async Task StoppeLaden()
        {
            await SendeBefehlAsync("openWB/set/lp1/ChargePointEnabled", "0");
        }

        public async Task SetzeAmpere(int ampere)
        {
            await SendeBefehlAsync("openWB/set/lp1/DirectChargeAmps", ampere.ToString());
        }
    }
}
