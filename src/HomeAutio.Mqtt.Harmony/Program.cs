using System;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Serilog;
using HarmonyHub = Harmony;

namespace HomeAutio.Mqtt.Harmony
{
    /// <summary>
    /// Main program entry point.
    /// </summary>
    public class Program
    {
        /// <summary>
        /// Main program entry point.
        /// </summary>
        /// <param name="args">Arguments.</param>
        public static void Main(string[] args)
        {
            MainAsync(args).GetAwaiter().GetResult();
        }

        /// <summary>
        /// Main program entry point.
        /// </summary>
        /// <param name="args">Arguments.</param>
        /// <returns>Awaitable <see cref="Task" />.</returns>
        public static async Task MainAsync(string[] args)
        {
            var environmentName = Environment.GetEnvironmentVariable("ENVIRONMENT");
            if (string.IsNullOrEmpty(environmentName))
                environmentName = "Development";

            // Setup config
            var config = new ConfigurationBuilder()
                .SetBasePath(Environment.CurrentDirectory)
                .AddJsonFile("appsettings.json", optional: false)
                .AddJsonFile($"appsettings.{environmentName}.json", optional: true)
                .Build();

            // Setup logging
            Log.Logger = new LoggerConfiguration()
                .ReadFrom.Configuration(config)
                .CreateLogger();

            try
            {
                var hubInfo = await DiscoverHubInfoAsync(config.GetValue<string>("harmony:harmonyHost"));

                var hostBuilder = CreateHostBuilder(config, hubInfo);
                await hostBuilder.RunConsoleAsync();
            }
            catch (Exception ex)
            {
                Log.Logger.Fatal(ex, ex.Message);
                throw;
            }
            finally
            {
                Log.CloseAndFlush();
            }
        }

        /// <summary>
        /// Creates an <see cref="IHostBuilder"/>.
        /// </summary>
        /// <param name="config">External configuration.</param>
        /// <param name="hubInfo">Hub info.</param>
        /// <returns>A configured <see cref="IHostBuilder"/>.</returns>
        private static IHostBuilder CreateHostBuilder(IConfiguration config, HarmonyHub.HubInfo hubInfo)
        {
            if (hubInfo == null) throw new ArgumentNullException(nameof(hubInfo));

            return new HostBuilder()
                .ConfigureAppConfiguration((hostContext, configuration) => configuration.AddConfiguration(config))
                .ConfigureLogging((hostingContext, logging) => logging.AddSerilog())
                .ConfigureServices((hostContext, services) =>
                {
                    // Setup client
                    services.AddScoped(serviceProvider => new HarmonyHub.Hub(hubInfo));

                    // Setup service instance
                    services.AddScoped<IHostedService, HarmonyMqttService>(serviceProvider =>
                    {
                        var brokerSettings = new Core.BrokerSettings
                        {
                            BrokerIp = config.GetValue<string>("mqtt:brokerIp"),
                            BrokerPort = config.GetValue<int>("mqtt:brokerPort"),
                            BrokerUsername = config.GetValue<string>("mqtt:brokerUsername"),
                            BrokerPassword = config.GetValue<string>("mqtt:brokerPassword")
                        };

                        return new HarmonyMqttService(
                            serviceProvider.GetRequiredService<ILogger<HarmonyMqttService>>(),
                            serviceProvider.GetRequiredService<HarmonyHub.Hub>(),
                            config.GetValue<string>("harmony:harmonyName"),
                            brokerSettings);
                    });
                });
        }

        /// <summary>
        /// Uses discovery to find the hub on the network.
        /// </summary>
        /// <param name="harmonyIp">Expected IP address of hub.</param>
        /// <returns>A <see cref="HarmonyHub.HubInfo" />.</returns>
        private static async Task<HarmonyHub.HubInfo> DiscoverHubInfoAsync(string harmonyIp)
        {
            var discoveryService = new HarmonyHub.DiscoveryService();
            var waitCancellationToken = new CancellationTokenSource();
            HarmonyHub.HubInfo hubInfo = null;
            discoveryService.HubFound += (sender, e) =>
            {
                if (e.HubInfo.IP == harmonyIp)
                {
                    Log.Logger.Information("Found hub " + e.HubInfo.IP + " (" + e.HubInfo.RemoteId + ")");
                    hubInfo = e.HubInfo;
                    waitCancellationToken.Cancel();
                }
            };

            // Run discovery for 30 seconds
            discoveryService.StartDiscovery();

            try
            {
                await Task.Delay(30000, waitCancellationToken.Token);
            }
            catch (OperationCanceledException)
            {
                // token has been canceled. exit
            }

            discoveryService.StopDiscovery();

            return hubInfo;
        }
    }
}
