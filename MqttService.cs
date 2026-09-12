using System;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using MQTTnet;
using MQTTnet.Client;

public class MqttService
{
    private IMqttClient _mqttClient;
    private string _brokerIp;

    public MqttService(string brokerIp)
    {
        _brokerIp = brokerIp;
        
        // Den MQTT-Client erschaffen
        var mqttFactory = new MqttFactory();
        _mqttClient = mqttFactory.CreateMqttClient();
        
        // Dem Client sagen, was er tun soll, wenn eine Nachricht reinkommt
        _mqttClient.ApplicationMessageReceivedAsync += e =>
        {
            string topic = e.ApplicationMessage.Topic;
            string payload = Encoding.UTF8.GetString(e.ApplicationMessage.PayloadSegment);
            
            Console.WriteLine($"[MQTT] Nachricht empfangen! Topic: {topic} | Wert: {payload}");
            return Task.CompletedTask;
        };
    }

    public async Task ConnectAsync()
    {
        var mqttOptions = new MqttClientOptionsBuilder()
            .WithTcpServer(_brokerIp, 1883) // 1883 ist der Standard-Port für MQTT
            .Build();

        try
        {
            Console.WriteLine($"[MQTT] Verbinde mit {_brokerIp}...");
            await _mqttClient.ConnectAsync(mqttOptions, CancellationToken.None);
            Console.WriteLine("[MQTT] Verbindung ERFOLGREICH hergestellt!");
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[MQTT] Fehler bei der Verbindung: {ex.Message}");
        }
    }
}
