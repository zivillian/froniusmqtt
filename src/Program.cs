using FroniusSolarClient;
using Mono.Options;
using MQTTnet;
using MQTTnet.Client;
using MQTTnet.Client.Options;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

string mqttHost = null;
string mqttUsername = null;
string mqttPassword = null;
string mqttPrefix = "fronius";
bool showHelp = false;
string host = null;
int interval = 30;
bool debug = false;
var options = new OptionSet
{
    {"m|mqttServer=", "MQTT Server", x => mqttHost = x},
    {"mqttuser=", "MQTT username", x => mqttUsername = x},
    {"mqttpass=", "MQTT password", x => mqttPassword = x},
    {"host=", "Inverter hostname or ip", x => host = x},
    {"p|prefix=", $"MQTT topic prefix - defaults to {mqttPrefix}", x => mqttPrefix = x.TrimEnd('/')},
    {"i|interval=", $"Polling interval in seconds - defaults to {interval}",x => showHelp = !Int32.TryParse(x, out interval)},
    {"d|debug", "enable debug logging", x => debug = x != null},
    {"h|help", "show help", x => showHelp = x != null},
};
try
{
    if (options.Parse(args).Count > 0)
    {
        showHelp = true;
    }
}
catch (OptionException ex)
{
    Console.Error.Write("froniusmqtt: ");
    Console.Error.WriteLine(ex.Message);
    Console.Error.WriteLine("Try 'froniusmqtt --help' for more information");
    return;
}
if (showHelp || mqttHost is null || host is null)
{
    options.WriteOptionDescriptions(Console.Out);
    return;
}

using (var cts = new CancellationTokenSource())
{
    using (var mqttClient = new MqttFactory().CreateMqttClient())
    {
        Console.CancelKeyPress += (s, e) =>
        {
            mqttClient.DisconnectAsync();
            cts.Cancel();
        };
        var mqttOptionBuilder = new MqttClientOptionsBuilder()
            .WithTcpServer(mqttHost)
            .WithClientId($"froniusmqtt");
        if (!String.IsNullOrEmpty(mqttUsername) || !String.IsNullOrEmpty(mqttPassword))
        {
            mqttOptionBuilder = mqttOptionBuilder.WithCredentials(mqttUsername, mqttPassword);
        }
        var mqttOptions = mqttOptionBuilder.Build();
        mqttClient.UseDisconnectedHandler(async e =>
        {
            if (cts.IsCancellationRequested) return;
            Console.Error.WriteLine("mqtt disconnected - reconnecting in 5 seconds");
            await Task.Delay(TimeSpan.FromSeconds(5), cts.Token);
            try
            {
                await mqttClient.ConnectAsync(mqttOptions, cts.Token);
            }
            catch
            {
                Console.Error.WriteLine("reconnect failed");
            }
        });
        await mqttClient.ConnectAsync(mqttOptions, cts.Token);
        var fronius = new SolarClient(host, 1, new ConsoleLogger(debug));
        await PollAsync(fronius, mqttClient, cts.Token);
    }
}

async Task PollAsync(SolarClient client, IMqttClient mqtt, CancellationToken cancellationToken)
{
    while (!cancellationToken.IsCancellationRequested)
    {
        var p3 = await client.GetP3InverterDataAsync(cancellationToken: cancellationToken);
        if (p3.Head.Status.Code != 0)
        {
            await PublishMessageAsync(mqtt, $"{mqttPrefix}/Error", p3.Head.Status.UserMessage, cancellationToken);
        }
        else
        {
            var payload = new JObject
            {
                ["AmbientTemperature"] = p3.Body.Data.AmbientTemperature?.Value,
                ["FanBackLeftSpeed"] = p3.Body.Data.FanBackLeftSpeed?.Value,
                ["FanBackRightSpeed"] = p3.Body.Data.FanBackRightSpeed?.Value,
                ["FanFrontLeftSpeed"] = p3.Body.Data.FanFrontLeftSpeed?.Value,
                ["FanFrontRightSpeed"] = p3.Body.Data.FanFrontRightSpeed?.Value,
                ["L1AcCurrent"] = p3.Body.Data.L1AcCurrent?.Value,
                ["L1AcVoltage"] = p3.Body.Data.L1AcVoltage?.Value,
                ["L2AcCurrent"] = p3.Body.Data.L2AcCurrent?.Value,
                ["L2AcVoltage"] = p3.Body.Data.L2AcVoltage?.Value,
                ["L3AcCurrent"] = p3.Body.Data.L3AcCurrent?.Value,
                ["L3AcVoltage"] = p3.Body.Data.L3AcVoltage?.Value
            };
            await PublishAsync(mqtt, $"{mqttPrefix}/P3Inverter", payload, cancellationToken);
        }
        var powerFlow = await client.GetPowerFlowRealtimeDataAsync(cancellationToken);
        
        if (p3.Head.Status.Code != 0)
        {
            await PublishMessageAsync(mqtt, $"{mqttPrefix}/Error", p3.Head.Status.UserMessage, cancellationToken);
        }
        else
        {
            var payload = new JObject
            {
                ["Version"] = powerFlow.Body.Data.Version,
                ["Site"] = new JObject
                {
                    ["BackupMode"] = powerFlow.Body.Data.Site.BackupMode,
                    ["BatteryStandby"] = powerFlow.Body.Data.Site.BatteryStandby,
                    ["EDay"] = powerFlow.Body.Data.Site.EDay,
                    ["ETotal"] = powerFlow.Body.Data.Site.ETotal,
                    ["EYear"] = powerFlow.Body.Data.Site.EYear,
                    ["MeterLocation"] = powerFlow.Body.Data.Site.MeterLocation,
                    ["Mode"] = powerFlow.Body.Data.Site.Mode,
                    ["PAkku"] = powerFlow.Body.Data.Site.PAkku,
                    ["PGrid"] = powerFlow.Body.Data.Site.PGrid,
                    ["PLoad"] = powerFlow.Body.Data.Site.PLoad,
                    ["PPV"] = powerFlow.Body.Data.Site.PPV,
                    ["RelAutonomy"] = powerFlow.Body.Data.Site.RelAutonomy,
                    ["RelSelfConsumption"] = powerFlow.Body.Data.Site.RelSelfConsumption,
                },
                ["Inverter"] = new JObject()
            };
            foreach (var inverter in powerFlow.Body.Data.Inverters)
            {
                payload["Inverter"][inverter.Key] = new JObject
                {
                    ["EDay"] = inverter.Value.EDay,
                    ["ETotal"] = inverter.Value.ETotal,
                    ["EYear"] = inverter.Value.EYear,
                    ["BatteryMode"] = inverter.Value.BatteryMode,
                    ["DT"] = inverter.Value.DT,
                    ["P"] = inverter.Value.P,
                    ["SOC"] = inverter.Value.SOC,
                };
            }
            await PublishAsync(mqtt, $"{mqttPrefix}/PowerFlowRealtime", payload, cancellationToken);
        }
        var commonInverter = await client.GetCommonInverterDataAsync(cancellationToken: cancellationToken);
        
        if (p3.Head.Status.Code != 0)
        {
            await PublishMessageAsync(mqtt, $"{mqttPrefix}/Error", p3.Head.Status.UserMessage, cancellationToken);
        }
        else
        {
            var payload = new JObject
            {
                ["AcCurrent"] = commonInverter.Body.Data.AcCurrent?.Value,
                ["AcFrequency"] = commonInverter.Body.Data.AcFrequency?.Value,
                ["AcPower"] = commonInverter.Body.Data.AcPower?.Value,
                ["AcVoltage"] = commonInverter.Body.Data.AcVoltage?.Value,
                ["CurrentDayEnergy"] = commonInverter.Body.Data.CurrentDayEnergy?.Value,
                ["CurrentYearEnergy"] = commonInverter.Body.Data.CurrentYearEnergy?.Value,
                ["DcCurrent"] = commonInverter.Body.Data.DcCurrent?.Value,
                ["DcVoltage"] = commonInverter.Body.Data.DcVoltage?.Value,
                ["TotalEnergy"] = commonInverter.Body.Data.TotalEnergy?.Value
            };
            if (commonInverter.Body.Data.DeviceStatus is not null)
            {
                payload["DeviceStatus"] = new JObject
                {
                    ["ErrorCode"] = commonInverter.Body.Data.DeviceStatus?.ErrorCode,
                    ["LedColor"] = commonInverter.Body.Data.DeviceStatus?.LedColor,
                    ["LedState"] = commonInverter.Body.Data.DeviceStatus?.LedState,
                    ["MgmtTimerRemainingTime"] = commonInverter.Body.Data.DeviceStatus?.MgmtTimerRemainingTime,
                    ["StateToReset"] = commonInverter.Body.Data.DeviceStatus?.StateToReset,
                    ["StatusCode"] = commonInverter.Body.Data.DeviceStatus?.StatusCode,
                };
            }
            await PublishAsync(mqtt, $"{mqttPrefix}/CommonInverter", payload, cancellationToken);
        }
        var minMaxInverter = await client.GetMinMaxInverterDataAsync(cancellationToken: cancellationToken);
        
        if (p3.Head.Status.Code != 0)
        {
            await PublishMessageAsync(mqtt, $"{mqttPrefix}/Error", p3.Head.Status.UserMessage, cancellationToken);
        }
        else
        {
            var payload = new JObject
            {
                ["MaxCurrentDayAcPower"] = minMaxInverter.Body.Data.MaxCurrentDayAcPower?.Value,
                ["MaxCurrentDayAcVoltage"] = minMaxInverter.Body.Data.MaxCurrentDayAcVoltage?.Value,
                ["MaxCurrentDayDcVoltage"] = minMaxInverter.Body.Data.MaxCurrentDayDcVoltage?.Value,
                ["MaxCurrentYearAcPower"] = minMaxInverter.Body.Data.MaxCurrentYearAcPower?.Value,
                ["MaxCurrentYearAcVoltage"] = minMaxInverter.Body.Data.MaxCurrentYearAcVoltage?.Value,
                ["MaxCurrentYearDcVoltage"] = minMaxInverter.Body.Data.MaxCurrentYearDcVoltage?.Value,
                ["MaxTotalAcPower"] = minMaxInverter.Body.Data.MaxTotalAcPower?.Value,
                ["MaxTotalAcVoltage"] = minMaxInverter.Body.Data.MaxTotalAcVoltage?.Value,
                ["MaxTotalDcVoltage"] = minMaxInverter.Body.Data.MaxTotalDcVoltage?.Value,
                ["MinCurrentDayAcVoltage"] = minMaxInverter.Body.Data.MinCurrentDayAcVoltage?.Value,
                ["MinCurrentYearAcVoltage"] = minMaxInverter.Body.Data.MinCurrentYearAcVoltage?.Value,
                ["MinTotalAcVoltage"] = minMaxInverter.Body.Data.MinTotalAcVoltage?.Value,
            };
            await PublishAsync(mqtt, $"{mqttPrefix}/MinMaxInverter", payload, cancellationToken);
        }
        await Task.Delay(TimeSpan.FromSeconds(interval), cancellationToken);
    }
}

static Task PublishAsync(IMqttClient client, string topic, JObject json, CancellationToken cancellationToken)
{
    foreach (var property in json.Properties().ToList())
    {
        if (property.Value.Type == JTokenType.Null) json.Remove(property.Name);
    }
    if (json.Count == 0) return Task.CompletedTask;
    var message = JsonConvert.SerializeObject(json);
    return PublishMessageAsync(client, topic, message, cancellationToken);
}

static Task PublishMessageAsync(IMqttClient client, string topic, string message, CancellationToken cancellationToken)
{
    if (!client.IsConnected)
    {
        Console.Error.WriteLine($"MQTT disconnected - dropping '{topic}': '{message}'");
        return Task.CompletedTask;
    }
    var payload = new MqttApplicationMessageBuilder()
        .WithTopic(topic)
        .WithPayload(message)
        .WithContentType("text/plain")
        .Build();
    return client.PublishAsync(payload, cancellationToken);
}