using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using HarmonyHub;
using HarmonyHub.Config;
using HarmonyHub.Events;
using HomeAutio.Mqtt.Core;
using HomeAutio.Mqtt.Core.Entities;
using HomeAutio.Mqtt.Core.Utilities;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using MQTTnet;
using Newtonsoft.Json;

namespace HomeAutio.Mqtt.Harmony
{
    /// <summary>
    /// Harmony MQTT service.
    /// </summary>
    public class HarmonyMqttService : ServiceBase
    {
        private ILogger<HarmonyMqttService> _log;
        private bool _disposed = false;

        private Client _client;
        private string _harmonyName;
        private HarmonyConfig _harmonyConfig;

        /// <summary>
        /// Holds mapping of possible MQTT topics mapped to Harmony command actions they trigger.
        /// </summary>
        private IDictionary<string, string> _topicActionMap;

        /// <summary>
        /// Initializes a new instance of the <see cref="HarmonyMqttService"/> class.
        /// </summary>
        /// <param name="applicationLifetime">Application lifetime instance.</param>
        /// <param name="logger">Logging instance.</param>
        /// <param name="harmonyClient">The Harmony client.</param>
        /// <param name="harmonyName">The Harmony name.</param>
        /// <param name="brokerIp">MQTT broker IP.</param>
        /// <param name="brokerPort">MQTT broker port.</param>
        /// <param name="brokerUsername">MQTT broker username.</param>
        /// <param name="brokerPassword">MQTT broker password.</param>
        public HarmonyMqttService(
            IApplicationLifetime applicationLifetime,
            ILogger<HarmonyMqttService> logger,
            Client harmonyClient,
            string harmonyName,
            string brokerIp,
            int brokerPort = 1883,
            string brokerUsername = null,
            string brokerPassword = null)
            : base(applicationLifetime, logger, brokerIp, brokerPort, brokerUsername, brokerPassword, "harmony/" + harmonyName)
        {
            _log = logger;
            _topicActionMap = new Dictionary<string, string>();
            SubscribedTopics.Add(TopicRoot + "/devices/+/+/+/set");
            SubscribedTopics.Add(TopicRoot + "/activity/set");
            SubscribedTopics.Add(TopicRoot + "/activity/+/set");

            // Setup harmony client
            _client = harmonyClient;
            _harmonyName = harmonyName;
            _client.CurrentActivityUpdated += Harmony_CurrentActivityUpdated;

            // Harmony client logging
            _client.MessageSent += (object sender, MessageSentEventArgs e) => { _log.LogDebug("Harmony Message sent: " + e.Message); };
            _client.MessageReceived += (object sender, MessageReceivedEventArgs e) => { _log.LogDebug("Harmony Message received: " + e.Message); };
            _client.Error += (object sender, System.IO.ErrorEventArgs e) =>
            {
                var exception = e.GetException();
                _log.LogError(exception, exception.Message);
                throw new Exception("Harmony connection lost");
            };
        }

        #region Service implementation

        /// <inheritdoc />
        protected override async Task StartServiceAsync(CancellationToken cancellationToken = default(CancellationToken))
        {
            // Connect to Harmony
            _client.Connect();
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
            _log.LogDebug("MQTT message received for topic " + e.ApplicationMessage.Topic + ": " + message);

            if (e.ApplicationMessage.Topic == TopicRoot + "/activity/set")
            {
                var activity = _harmonyConfig?.Activity?.FirstOrDefault(x => x.Label == message);
                if (activity != null)
                {
                    await _client.StartActivityAsync(int.Parse(activity.Id))
                        .ConfigureAwait(false);
                }
            }
            else if (_topicActionMap.ContainsKey(e.ApplicationMessage.Topic))
            {
                var command = _topicActionMap[e.ApplicationMessage.Topic];
                if (command != null)
                {
                    await _client.SendCommandAsync(command)
                        .ConfigureAwait(false);
                }
            }
        }

        #endregion

        #region Harmony Implementation

        /// <summary>
        /// Handles publishing updates to the harmony current activity to MQTT.
        /// </summary>
        /// <param name="sender">Event sender.</param>
        /// <param name="e">Event args.</param>
        private async void Harmony_CurrentActivityUpdated(object sender, ActivityUpdatedEventArgs e)
        {
            var currentActivity = _harmonyConfig.Activity.FirstOrDefault(x => x.Id == e.Id.ToString())?.Label;
            _log.LogDebug("Harmony current activity updated: " + currentActivity);

            await MqttClient.PublishAsync(new MqttApplicationMessageBuilder()
                .WithTopic(TopicRoot + "/activity")
                .WithPayload(currentActivity)
                .WithAtLeastOnceQoS()
                .WithRetainFlag()
                .Build())
                .ConfigureAwait(false);
        }

        /// <summary>
        /// Maps Harmony device actions to subscription topics.
        /// </summary>
        /// <returns>An awaitable <see cref="Task"/>.</returns>
        private async Task GetConfigAsync()
        {
            _harmonyConfig = await _client.GetConfigAsync()
                .ConfigureAwait(false);

            // Wipe topic to Harmony action map for reload
            if (_topicActionMap.Count > 0)
                _topicActionMap.Clear();

            var hub = new Hub();

            // Map all devices at {TopicRoot}/devices/{deviceLabel}/{controlGroup}/{controlName}/set
            // Listen at topic {TopicRoot}/devices/+/+/+/set
            foreach (var harmonyDevice in _harmonyConfig.Device)
            {
                var device = new Device { Name = harmonyDevice.Label };
                foreach (var controlGroup in harmonyDevice.ControlGroup)
                {
                    foreach (var control in controlGroup.Function)
                    {
                        var commandTopic = $"{TopicRoot}/devices/{harmonyDevice.Label.Sluggify()}/{controlGroup.Name.Sluggify()}/{control.Name.Sluggify()}/set";
                        device.Controls.Add(new ButtonControl { Name = controlGroup.Name + " " + control.Name, CommandTopic = commandTopic });

                        // Add mapping for subscribed topic to Harmony control action, ignoring duplicates
                        if (!_topicActionMap.ContainsKey(commandTopic))
                            _topicActionMap.Add(commandTopic, control.Action);
                    }
                }

                hub.Devices.Add(device);
            }

            // Add a Device for the hub itself at {TopicRoot}/activity/{activityName}
            // Listen at topic {TopicRoot}/activity/+/set
            var hubDevice = new Device { Name = "Harmony Hub " + _harmonyName };

            // Map activities
            var activitySelections = new Dictionary<string, string>();
            foreach (var activity in _harmonyConfig.Activity)
            {
                var commandTopic = $"{TopicRoot}/activity/{activity.Label.Sluggify()}/set";
                _topicActionMap.Add(commandTopic, activity.Id);
                hubDevice.Controls.Add(new ButtonControl { Name = "Activity: " + activity.Label, CommandTopic = commandTopic });

                activitySelections.Add(activity.Label, activity.Label);
            }

            // Add a Selector for the hub itself at {TopicRoot}/activity/set
            // Listen at topic {TopicRoot}/activity/set
            var currentActivityId = await _client.GetCurrentActivityIdAsync()
                .ConfigureAwait(false);
            var currentActivity = _harmonyConfig.Activity.FirstOrDefault(x => x.Id == currentActivityId.ToString())?.Label;
            var activityStateTopic = $"{TopicRoot}/activity";
            var activityCommandTopic = $"{TopicRoot}/activity/set";
            _topicActionMap.Add(activityCommandTopic, "Activity");

            hubDevice.Controls.Add(new SensorControl { ValueTopic = activityStateTopic });
            hubDevice.Controls.Add(new SelectorControl { CommandTopic = activityCommandTopic, ValueTopic = activityStateTopic, SelectionLabels = activitySelections });

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
                .WithPayload(currentActivity)
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
                    _client.Dispose();
            }

            _disposed = true;
            base.Dispose(disposing);
        }

        #endregion
    }
}
