using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Timers;
using HomeAutio.Mqtt.Core;
using I8Beef.Neato;
using I8Beef.Neato.Nucleo.Protocol;
using I8Beef.Neato.Nucleo.Protocol.Services.HouseCleaning;
using Microsoft.Extensions.Logging;
using MQTTnet;
using MQTTnet.Extensions.ManagedClient;
using Newtonsoft.Json;

namespace HomeAutio.Mqtt.Neato
{
    /// <summary>
    /// Neato MQTT Service.
    /// </summary>
    public class NeatoMqttService : ServiceBase
    {
        private readonly ILogger<NeatoMqttService> _log;
        private readonly IRobot _client;
        private readonly int _refreshInterval;
        private System.Timers.Timer _refresh;

        private bool _disposed = false;

        private IDictionary<string, string> _topicMap;

        /// <summary>
        /// Initializes a new instance of the <see cref="NeatoMqttService"/> class.
        /// </summary>
        /// <param name="logger">Logging instance.</param>
        /// <param name="neatoClient">Neato client.</param>
        /// <param name="neatoName">Neato name.</param>
        /// <param name="refreshInterval">Refresh interval.</param>
        /// <param name="brokerSettings">MQTT broker settings.</param>
        public NeatoMqttService(
            ILogger<NeatoMqttService> logger,
            IRobot neatoClient,
            string neatoName,
            int refreshInterval,
            BrokerSettings brokerSettings)
            : base(logger, brokerSettings, "neato/" + neatoName)
        {
            _log = logger;
            _refreshInterval = refreshInterval * 1000;

            SubscribedTopics.Add(TopicRoot + "/+/set");

            _client = neatoClient;
        }

        #region Service implementation

        /// <inheritdoc />
        protected override async Task StartServiceAsync(CancellationToken cancellationToken = default)
        {
            await GetConfigAsync(cancellationToken)
                .ConfigureAwait(false);

            // Enable refresh
            if (_refresh != null)
                _refresh.Dispose();

            _refresh = new System.Timers.Timer(_refreshInterval);
            _refresh.Elapsed += RefreshAsync;
            _refresh.Start();
        }

        /// <inheritdoc />
        protected override Task StopServiceAsync(CancellationToken cancellationToken = default)
        {
            return Task.CompletedTask;
        }

        #endregion

        #region MQTT Implementation

        /// <summary>
        /// Handles commands for the Harmony published to MQTT.
        /// </summary>
        /// <param name="e">Event args.</param>
        protected override async void Mqtt_MqttMsgPublishReceived(MqttApplicationMessageReceivedEventArgs e)
        {
            var message = Encoding.UTF8.GetString(e.ApplicationMessage.Payload);
            _log.LogInformation("MQTT message received for topic " + e.ApplicationMessage.Topic + ": " + message);

            switch (e.ApplicationMessage.Topic.Substring(TopicRoot.Length))
            {
                case "/dock/set":
                    await _client.SendToBaseAsync()
                        .ConfigureAwait(false);
                    break;
                case "/findMe/set":
                    await _client.FindMeAsync()
                        .ConfigureAwait(false);
                    break;
                case "/start/set":
                    var cleaningSettings = !string.IsNullOrEmpty(message) && message.Contains("{")
                        ? JsonConvert.DeserializeObject<StartCleaningParameters>(message)
                        : new StartCleaningParameters
                            {
                                Category = Enum.Parse<CleaningCategory>(_topicMap[$"{TopicRoot}/cleaning/category"]),
                                Mode = Enum.Parse<CleaningMode>(_topicMap[$"{TopicRoot}/cleaning/mode"]),
                                Modifier = CleaningFrequency.Normal,
                                NavigationMode = Enum.Parse<NavigationMode>(_topicMap[$"{TopicRoot}/cleaning/navigationMode"])
                            };

                    await _client.StartCleaningAsync(cleaningSettings)
                        .ConfigureAwait(false);
                    break;
                case "/stop/set":
                    await _client.StopCleaningAsync()
                        .ConfigureAwait(false);
                    break;
                case "/pause/set":
                    await _client.PauseCleaningAsync()
                        .ConfigureAwait(false);
                    break;
                case "/resume/set":
                    await _client.ResumeCleaningAsync()
                        .ConfigureAwait(false);
                    break;
                case "/startPersistentMapExploration/set":
                    await _client.StartPersistentMapExplorationAsync()
                        .ConfigureAwait(false);
                    break;
                case "/enableSchedule/set":
                    if (message == "true")
                    {
                        await _client.EnableScheduleAsync()
                            .ConfigureAwait(false);
                    }
                    else
                    {
                        await _client.DisableScheduleAsync()
                            .ConfigureAwait(false);
                    }

                    break;
                case "/dismissCurrentAlert/set":
                    await _client.DismissCurrentAlertAsync()
                        .ConfigureAwait(false);
                    break;
            }

            await RefreshStateAsync()
                .ConfigureAwait(false);
        }

        #endregion

        #region Neato implementation

        /// <summary>
        /// Refreshes current state and publishes changes.
        /// </summary>
        /// <param name="sender">Event sender.</param>
        /// <param name="e">Event args.</param>
        private async void RefreshAsync(object sender, ElapsedEventArgs e)
        {
            await RefreshStateAsync()
                .ConfigureAwait(false);
        }

        /// <summary>
        /// Compares twomaster state objects.
        /// </summary>
        /// <param name="status1">First status.</param>
        /// <param name="status2">Second status.</param>
        /// <returns>List of changes.</returns>
        private IDictionary<string, string> CompareStatusObjects(IDictionary<string, string> status1, IDictionary<string, string> status2)
        {
            return new Dictionary<string, string>(status2.Where(x => x.Value != status1[x.Key]));
        }

        /// <summary>
        /// Maps Apex device actions to subscription topics.
        /// </summary>
        /// <param name="cancellationToken">Cancellation token.</param>
        /// <returns>Awaitable <see cref="Task" />.</returns>
        private async Task GetConfigAsync(CancellationToken cancellationToken = default)
        {
            var state = await _client.GetRobotStateAsync(cancellationToken)
                .ConfigureAwait(false);

            _topicMap = GetTopicMap(state);

            foreach (var stateTopic in _topicMap)
            {
                // Publish initial value
                await MqttClient.PublishAsync(
                    new MqttApplicationMessageBuilder()
                        .WithTopic(stateTopic.Key)
                        .WithPayload(stateTopic.Value)
                        .WithAtLeastOnceQoS()
                        .WithRetainFlag()
                        .Build(),
                    cancellationToken).ConfigureAwait(false);
            }
        }

        /// <summary>
        /// Refreshes current state and publishes changes.
        /// </summary>
        /// <returns>Awaitable <see cref="Task" />.</returns>
        private async Task RefreshStateAsync()
        {
            // Make all of the calls to get current status
            var state = await _client.GetRobotStateAsync()
                .ConfigureAwait(false);

            var topicMap = GetTopicMap(state);

            // Compare to current cached status
            var updates = CompareStatusObjects(_topicMap, topicMap);

            // If updated, publish changes
            if (updates.Count > 0)
            {
                foreach (var update in updates)
                {
                    await MqttClient.PublishAsync(new MqttApplicationMessageBuilder()
                        .WithTopic(update.Key)
                        .WithPayload(update.Value.Trim())
                        .WithAtLeastOnceQoS()
                        .WithRetainFlag()
                        .Build())
                        .ConfigureAwait(false);
                }

                _topicMap = topicMap;
            }
        }

        /// <summary>
        /// Maps state to a topic.
        /// </summary>
        /// <param name="state">State object.</param>
        /// <returns>A topic map.</returns>
        private IDictionary<string, string> GetTopicMap(RobotState state)
        {
            return new Dictionary<string, string>
            {
                { $"{TopicRoot}/state", Enum.GetName(typeof(StateType), state.State) },
                { $"{TopicRoot}/action", Enum.GetName(typeof(ActionType), state.Action) },
                { $"{TopicRoot}/alert", state.Alert },
                { $"{TopicRoot}/error", state.Error.HasValue ? state.Error.Value.ToString() : string.Empty },
                { $"{TopicRoot}/cleaning/category", Enum.GetName(typeof(CleaningCategory), state.Cleaning.Category) },
                { $"{TopicRoot}/cleaning/mode", Enum.GetName(typeof(CleaningMode), state.Cleaning.Mode) },
                { $"{TopicRoot}/cleaning/navigationMode", Enum.GetName(typeof(NavigationMode), state.Cleaning.NavigationMode) },
                { $"{TopicRoot}/cleaning/spotWidth", state.Cleaning.SpotWidth.ToString() },
                { $"{TopicRoot}/cleaning/spotHeight", state.Cleaning.SpotHeight.ToString() },
                { $"{TopicRoot}/details/charge", state.Details.Charge.ToString() },
                { $"{TopicRoot}/details/isCharging", state.Details.IsCharging.ToString() },
                { $"{TopicRoot}/details/isDocked", state.Details.IsDocked.ToString() },
                { $"{TopicRoot}/details/isScheduleEnabled", state.Details.IsScheduleEnabled.ToString() }
            };
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
                if (_refresh != null)
                {
                    _refresh.Stop();
                    _refresh.Dispose();
                }
            }

            _disposed = true;
            base.Dispose(disposing);
        }

        #endregion
    }
}
