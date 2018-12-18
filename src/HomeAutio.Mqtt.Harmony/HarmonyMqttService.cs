using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using HomeAutio.Mqtt.Core;
using HomeAutio.Mqtt.Core.Entities;
using HomeAutio.Mqtt.Core.Utilities;
using Microsoft.Extensions.Logging;
using MQTTnet;
using Newtonsoft.Json;
using HarmonyHub = Harmony;

namespace HomeAutio.Mqtt.Harmony
{
    /// <summary>
    /// Harmony MQTT service.
    /// </summary>
    public class HarmonyMqttService : ServiceBase
    {
        private ILogger<HarmonyMqttService> _log;
        private bool _disposed = false;

        private HarmonyHub.Hub _client;
        private string _harmonyName;
        private HarmonyHub.HubSync _harmonyConfig;

        /// <summary>
        /// Holds mapping of possible MQTT topics mapped to Harmony command actions they trigger.
        /// </summary>
        private IDictionary<string, HarmonyHub.Function> _topicActionMap;

        /// <summary>
        /// Initializes a new instance of the <see cref="HarmonyMqttService"/> class.
        /// </summary>
        /// <param name="logger">Logging instance.</param>
        /// <param name="harmonyClient">The Harmony client.</param>
        /// <param name="harmonyName">The Harmony name.</param>
        /// <param name="brokerSettings">MQTT broker settings.</param>
        public HarmonyMqttService(
            ILogger<HarmonyMqttService> logger,
            HarmonyHub.Hub harmonyClient,
            string harmonyName,
            BrokerSettings brokerSettings)
            : base(logger, brokerSettings, "harmony/" + harmonyName)
        {
            _log = logger;
            _topicActionMap = new Dictionary<string, HarmonyHub.Function>();
            SubscribedTopics.Add(TopicRoot + "/devices/+/+/+/set");
            SubscribedTopics.Add(TopicRoot + "/activity/set");
            SubscribedTopics.Add(TopicRoot + "/activity/+/set");

            // Setup harmony client
            _client = harmonyClient;
            _harmonyName = harmonyName;
            _client.ActivityProgress += Harmony_CurrentActivityUpdated;
        }

        #region Service implementation

        /// <inheritdoc />
        protected override async Task StartServiceAsync(CancellationToken cancellationToken = default(CancellationToken))
        {
            // Connect to Harmony
            await _client.ConnectAsync(HarmonyHub.DeviceID.GetDeviceDefault())
                .ConfigureAwait(false);
            await GetConfigAsync()
                .ConfigureAwait(false);
        }

        /// <inheritdoc />
        protected override Task StopServiceAsync(CancellationToken cancellationToken = default(CancellationToken))
        {
            return Task.CompletedTask;
        }

        #endregion

        #region MQTT Implementation

        /// <summary>
        /// Handles commands for the Harmony published to MQTT.
        /// </summary>
        /// <param name="sender">Event sender.</param>
        /// <param name="e">Event args.</param>
        protected override async void Mqtt_MqttMsgPublishReceived(object sender, MqttApplicationMessageReceivedEventArgs e)
        {
            var message = e.ApplicationMessage.ConvertPayloadToString();
            _log.LogInformation("MQTT message received for topic " + e.ApplicationMessage.Topic + ": " + message);

            if (e.ApplicationMessage.Topic == TopicRoot + "/activity/set")
            {
                if (message.ToUpper() == "POWEROFF")
                {
                    try
                    {
                        await _client.EndActivity()
                            .ConfigureAwait(false);
                    }
                    catch (Exception)
                    {
                        // Swallow possible exception when no running activity
                    }
                }
                else
                {
                    var activity = _harmonyConfig?.Activities?.FirstOrDefault(x => x.Label == message);
                    if (activity != null)
                    {
                        await _client.StartActivity(activity)
                            .ConfigureAwait(false);
                    }
                    else
                    {
                        _log.LogWarning("Could not find activity " + message);
                    }
                }
            }
            else if (_topicActionMap.ContainsKey(e.ApplicationMessage.Topic))
            {
                var button = _topicActionMap[e.ApplicationMessage.Topic];
                if (button != null)
                {
                    await _client.PressButtonAsync(button)
                        .ConfigureAwait(false);
                }
                else
                {
                    _log.LogWarning("Could not route message to button for topic " + e.ApplicationMessage.Topic);
                }
            }
            else
            {
                _log.LogWarning("Could not handle topic " + e.ApplicationMessage.Topic);
            }
        }

        #endregion

        #region Harmony Implementation

        /// <summary>
        /// Handles publishing updates to the harmony current activity to MQTT.
        /// </summary>
        /// <param name="sender">Event sender.</param>
        /// <param name="e">Event args.</param>
        private async void Harmony_CurrentActivityUpdated(object sender, HarmonyHub.ActivityProgressEventArgs e)
        {
            if (e.Progress == 1)
            {
                var currentActivity = e.Activity.Label;
                _log.LogInformation("Harmony current activity updated: " + currentActivity);

                await MqttClient.PublishAsync(new MqttApplicationMessageBuilder()
                    .WithTopic(TopicRoot + "/activity")
                    .WithPayload(currentActivity)
                    .WithAtLeastOnceQoS()
                    .WithRetainFlag()
                    .Build())
                    .ConfigureAwait(false);
            }
        }

        /// <summary>
        /// Maps Harmony device actions to subscription topics.
        /// </summary>
        /// <returns>An awaitable <see cref="Task"/>.</returns>
        private async Task GetConfigAsync()
        {
            await _client.SyncConfigurationAsync()
                .ConfigureAwait(false);

            _harmonyConfig = _client.Sync;

            // Wipe topic to Harmony action map for reload
            if (_topicActionMap.Count > 0)
                _topicActionMap.Clear();

            var hub = new Hub();

            // Map all devices at {TopicRoot}/devices/{deviceLabel}/{controlGroup}/{controlName}/set
            // Listen at topic {TopicRoot}/devices/+/+/+/set
            foreach (var harmonyDevice in _harmonyConfig.Devices)
            {
                var device = new Device { Name = harmonyDevice.Label };
                foreach (var controlGroup in harmonyDevice.ControlGroups)
                {
                    foreach (var function in controlGroup.Functions)
                    {
                        var commandTopic = $"{TopicRoot}/devices/{harmonyDevice.Label.Sluggify()}/{controlGroup.Name.Sluggify()}/{function.Name.Sluggify()}/set";
                        device.Controls.Add(new ButtonControl { Name = controlGroup.Name + " " + function.Name, CommandTopic = commandTopic });

                        // Add mapping for subscribed topic to Harmony control action, ignoring duplicates
                        if (!_topicActionMap.ContainsKey(commandTopic))
                            _topicActionMap.Add(commandTopic, function);
                    }
                }

                hub.Devices.Add(device);
            }

            // Add a Device for the hub itself at {TopicRoot}/activity/{activityName}
            // Listen at topic {TopicRoot}/activity/+/set
            var hubDevice = new Device { Name = "Harmony Hub " + _harmonyName };

            // Map activities
            var activitySelections = new Dictionary<string, string>();
            foreach (var activity in _harmonyConfig.Activities)
            {
                var commandTopic = $"{TopicRoot}/activity/{activity.Label.Sluggify()}/set";
                hubDevice.Controls.Add(new ButtonControl { Name = "Activity: " + activity.Label, CommandTopic = commandTopic });

                activitySelections.Add(activity.Label, activity.Label);
            }

            // Add a Selector for the hub itself at {TopicRoot}/activity/set
            // Listen at topic {TopicRoot}/activity/set
            var currentActivity = await _client.GetRunningActivity()
                .ConfigureAwait(false);
            var activityStateTopic = $"{TopicRoot}/activity";
            var activityCommandTopic = $"{TopicRoot}/activity/set";

            hubDevice.Controls.Add(new SelectorControl { Name = "Activity", CommandTopic = activityCommandTopic, ValueTopic = activityStateTopic, SelectionLabels = activitySelections });

            hub.Devices.Add(hubDevice);

            // Publish out device information for subscribers
            await MqttClient.PublishAsync(new MqttApplicationMessageBuilder()
                .WithTopic(TopicRoot + "/homeAutio")
                .WithPayload(JsonConvert.SerializeObject(hub, new JsonSerializerSettings() { TypeNameHandling = TypeNameHandling.All }))
                .WithAtLeastOnceQoS()
                .WithRetainFlag()
                .Build())
                .ConfigureAwait(false);

            await MqttClient.PublishAsync(new MqttApplicationMessageBuilder()
                .WithTopic(TopicRoot + "/activity")
                .WithPayload(currentActivity.Label)
                .WithAtLeastOnceQoS()
                .WithRetainFlag()
                .Build())
                .ConfigureAwait(false);
        }

        #endregion

        #region IDisposable Support

        /// <summary>
        /// Dispose implementation.
        /// </summary>
        /// <param name="disposing">Indicates if disposing.</param>
        protected override void Dispose(bool disposing)
        {
            if (_disposed)
                return;

            if (disposing)
            {
                if (_client != null)
                    _client.Disconnect();
            }

            _disposed = true;
            base.Dispose(disposing);
        }

        #endregion
    }
}
