using System.Configuration;
using HarmonyHub;
using NLog;
using Topshelf;

namespace HomeAutio.Mqtt.Harmony
{
    /// <summary>
    /// Main program entrypoint.
    /// </summary>
    public class Program
    {
        /// <summary>
        /// Main method.
        /// </summary>
        /// <param name="args">Command line arguments.</param>
        public static void Main(string[] args)
        {
            var log = LogManager.GetCurrentClassLogger();

            var brokerIp = ConfigurationManager.AppSettings["brokerIp"];
            var brokerPort = int.Parse(ConfigurationManager.AppSettings["brokerPort"]);
            var brokerUsername = ConfigurationManager.AppSettings["brokerUsername"];
            var brokerPassword = ConfigurationManager.AppSettings["brokerPassword"];

            bool.TryParse(ConfigurationManager.AppSettings["bypassLogitechLogin"], out bool bypassLogitech);

            var harmonyIp = ConfigurationManager.AppSettings["harmonyIp"];
            var harmonyUsername = ConfigurationManager.AppSettings["harmonyUsername"];
            var harmonyPassword = ConfigurationManager.AppSettings["harmonyPassword"];
            var harmonyClient = new Client(harmonyIp, harmonyUsername, harmonyPassword, 2000, bypassLogitech);

            var harmonyName = ConfigurationManager.AppSettings["harmonyName"];

            HostFactory.Run(x =>
            {
                x.UseNLog();
                x.OnException(ex => log.Error(ex));

                x.Service<HarmonyMqttService>(s =>
                {
                    s.ConstructUsing(name => new HarmonyMqttService(harmonyClient, harmonyName, brokerIp, brokerPort, brokerUsername, brokerPassword));
                    s.WhenStarted(tc => tc.Start());
                    s.WhenStopped(tc => tc.Stop());
                });

                x.EnableServiceRecovery(r =>
                {
                    r.RestartService(0);
                    r.RestartService(0);
                    r.RestartService(0);
                });

                x.RunAsLocalSystem();
                x.UseAssemblyInfoForServiceInfo();
            });
        }
    }
}
