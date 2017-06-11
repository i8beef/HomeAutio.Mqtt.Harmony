using HarmonyHub;
using System.Configuration;
using Topshelf;

namespace HomeAutio.Mqtt.Harmony
{
    class Program
    {
        static void Main(string[] args)
        {
            var brokerIp = ConfigurationManager.AppSettings["brokerIp"];
            var brokerPort = int.Parse(ConfigurationManager.AppSettings["brokerPort"]);
            var brokerUsername = ConfigurationManager.AppSettings["brokerUsername"];
            var brokerPassword = ConfigurationManager.AppSettings["brokerPassword"];


            var bypassLogitech = false;
            bool.TryParse(ConfigurationManager.AppSettings["bypassLogitechLogin"], out bypassLogitech);

            var harmonyIp = ConfigurationManager.AppSettings["harmonyIp"];
            var harmonyUsername = ConfigurationManager.AppSettings["harmonyUsername"];
            var harmonyPassword = ConfigurationManager.AppSettings["harmonyPassword"];
            var harmonyClient = new Client(harmonyIp, harmonyUsername, harmonyPassword, 2000, bypassLogitech);

            var harmonyName = ConfigurationManager.AppSettings["harmonyName"];

            HostFactory.Run(x =>
            {
                x.UseNLog();

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
