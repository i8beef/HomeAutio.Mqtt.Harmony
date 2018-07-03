using System;
using System.Threading.Tasks;
using HarmonyHub;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Serilog;

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
            // Setup config
            var config = new ConfigurationBuilder()
                .SetBasePath(Environment.CurrentDirectory)
                .AddJsonFile("appsettings.json", optional: false)
                .Build();

            // Setup logging
            Log.Logger = new LoggerConfiguration()
                .ReadFrom.Configuration(config)
                .CreateLogger();

            try
            {
                var hostBuilder = CreateHostBuilder(config);
                await hostBuilder.RunConsoleAsync();
            }
            catch (Exception ex)
            {
                Log.Logger.Fatal(ex, ex.Message);
                throw;
            }
        }

        /// <summary>
        /// Creates an <see cref="IHostBuilder"/>.
        /// </summary>
        /// <param name="config">External configuration.</param>
        /// <returns>A configured <see cref="IHostBuilder"/>.</returns>
        private static IHostBuilder CreateHostBuilder(IConfiguration config)
        {
            return new HostBuilder()
                .ConfigureAppConfiguration((hostContext, configuration) => configuration.AddConfiguration(config))
                .ConfigureLogging((hostingContext, logging) => logging.AddSerilog())
                .ConfigureServices((hostContext, services) =>
                {
                    // Setup client
                    services.AddScoped<Client>(serviceProvider =>
                    {
                        var configuration = serviceProvider.GetRequiredService<IConfiguration>();
                        return new Client(configuration.GetValue<string>("harmonyHost"));
                    });

                    // Setup service instance
                    services.AddScoped<IHostedService, HarmonyMqttService>(serviceProvider =>
                    {
                        var configuration = serviceProvider.GetRequiredService<IConfiguration>();
                        return new HarmonyMqttService(
                            serviceProvider.GetRequiredService<IApplicationLifetime>(),
                            serviceProvider.GetRequiredService<ILogger<HarmonyMqttService>>(),
                            serviceProvider.GetRequiredService<Client>(),
                            configuration.GetValue<string>("harmonyName"),
                            configuration.GetValue<string>("brokerIp"),
                            configuration.GetValue<int>("brokerPort"),
                            configuration.GetValue<string>("brokerUsername"),
                            configuration.GetValue<string>("brokerPassword"));
                    });
                });
        }
    }
}
