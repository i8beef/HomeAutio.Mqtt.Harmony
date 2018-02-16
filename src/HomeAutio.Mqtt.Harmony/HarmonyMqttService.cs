using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using HarmonyHub;
using HarmonyHub.Config;
using HarmonyHub.Events;
using HomeAutio.Mqtt.Core;
using HomeAutio.Mqtt.Core.Entities;
using HomeAutio.Mqtt.Core.Utilities;
using Newtonsoft.Json;
using NLog;
using uPLibrary.Networking.M2Mqtt.Messages;

namespace HomeAutio.Mqtt.Harmony
{
    /// <summary>
    /// Harmony MQTT service.
    /// </summary>
    public class HarmonyMqttService : ServiceBase
    {
        private ILogger _log = LogManager.GetCurrentClassLogger();
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
        /// <param name="harmonyClient">The Harmony client.</param>
        /// <param name="harmonyName">The Harmony name.</param>
        /// <param name="brokerIp">MQTT broker IP.</param>
        /// <param name="brokerPort">MQTT broker port.</param>
        /// <param name="brokerUsername">MQTT broker username.</param>
        /// <param name="brokerPassword">MQTT broker password.</param>
        public HarmonyMqttService(Client harmonyClient, string harmonyName, string brokerIp, int brokerPort = 1883, string brokerUsername = null, string brokerPassword = null)
            : base(brokerIp, brokerPort, brokerUsername, brokerPassword, "harmony/" + harmonyName)
        {
            _topicActionMap = new Dictionary<string, string>();
            SubscribedTopics.Add(TopicRoot + "/devices/+/+/+/set");
            SubscribedTopics.Add(TopicRoot + "/activity/set");
            SubscribedTopics.Add(TopicRoot + "/activity/+/set");

            // Setup harmony client
            _client = harmonyClient;
            _harmonyName = harmonyName;
            _client.CurrentActivityUpdated += Harmony_CurrentActivityUpdated;

            // Harmony client logging
            _client.MessageSent += (object sender, HarmonyHub.Events.MessageSentEventArgs e) => { _log.Debug("Harmony Message sent: " + e.Message); };
            _client.MessageReceived += (object sender, HarmonyHub.Events.MessageReceivedEventArgs e) => { _log.Debug("Harmony Message received: " + e.Message); };
            _client.Error += (object sender, System.IO.ErrorEventArgs e) =>
            {
                _log.Error(e.GetException());
                throw new Exception("Harmony connection lost");
            };
        }

        #region Service implementation

        /// <summary>
        /// Service Start action.
        /// </summary>
        protected override void StartService()
        {
            // Connect to Harmony
            _client.Connect();
            GetConfig();
        }

        /// <summary>
        /// Service Stop action.
        /// </summary>
        protected override void StopService()
        {
            Dispose();
        }

        #endregion

        #region MQTT Implementation

        /// <summary>
        /// Handles commands for the Harmony published to MQTT.
        /// </summary>
        /// <param name="sender">Event sender.</param>
        /// <param name="e">Event args.</param>
        protected override void Mqtt_MqttMsgPublishReceived(object sender, MqttMsgPublishEventArgs e)
        {
            var message = Encoding.UTF8.GetString(e.Message);
            _log.Debug("MQTT message received for topic " + e.Topic + ": " + message);

            if (e.Topic == TopicRoot + "/activity/set")
            {
                var activity = _harmonyConfig?.Activity?.FirstOrDefault(x => x.Label == message);
                if (activity != null)
                    _client.StartActivityAsync(int.Parse(activity.Id)).GetAwaiter().GetResult();
            }
            else if (_topicActionMap.ContainsKey(e.Topic))
            {
                var command = _topicActionMap[e.Topic];
                if (command != null)
                    _client.SendKeyPressAsync(command).GetAwaiter().GetResult();
            }
        }

        #endregion

        #region Harmony Implementation

        /// <summary>
        /// Handles publishing updates to the harmony current activity to MQTT.
        /// </summary>
        /// <param name="sender">Event sender.</param>
        /// <param name="e">Event args.</param>
        private void Harmony_CurrentActivityUpdated(object sender, ActivityUpdatedEventArgs e)
        {
            var currentActivity = _harmonyConfig.Activity.FirstOrDefault(x => x.Id == e.Id.ToString())?.Label;
            _log.Debug("Harmony current activity updated: " + currentActivity);

            MqttClient.Publish(TopicRoot + "/activity", Encoding.UTF8.GetBytes(currentActivity), MqttMsgBase.QOS_LEVEL_AT_LEAST_ONCE, true);
        }

        /// <summary>
        /// Maps Harmony device actions to subscription topics.
        /// </summary>
        private void GetConfig()
        {
            _harmonyConfig = _client.GetConfigAsync().GetAwaiter().GetResult();

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
            var currentActivityId = _client.GetCurrentActivityIdAsync().GetAwaiter().GetResult();
            var currentActivity = _harmonyConfig.Activity.FirstOrDefault(x => x.Id == currentActivityId.ToString())?.Label;
            var activityStateTopic = $"{TopicRoot}/activity";
            var activityCommandTopic = $"{TopicRoot}/activity/set";
            _topicActionMap.Add(activityCommandTopic, "Activity");

            hubDevice.Controls.Add(new SensorControl { ValueTopic = activityStateTopic });
            hubDevice.Controls.Add(new SelectorControl { CommandTopic = activityCommandTopic, ValueTopic = activityStateTopic, SelectionLabels = activitySelections });

            hub.Devices.Add(hubDevice);

            // Publish out device information for subscribers
            MqttClient.Publish(TopicRoot + "/homeAutio", Encoding.UTF8.GetBytes(JsonConvert.SerializeObject(hub, new JsonSerializerSettings() { TypeNameHandling = TypeNameHandling.All })), MqttMsgBase.QOS_LEVEL_AT_LEAST_ONCE, true);
            MqttClient.Publish(TopicRoot + "/activity", Encoding.UTF8.GetBytes(currentActivity), MqttMsgBase.QOS_LEVEL_AT_LEAST_ONCE, true);
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
