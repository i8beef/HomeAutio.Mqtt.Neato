using System;
using System.IO;
using System.Linq;
using System.Security.Cryptography.X509Certificates;
using System.Threading.Tasks;
using I8Beef.Neato;
using I8Beef.Neato.BeeHive;
using I8Beef.Neato.Nucleo;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Serilog;

namespace HomeAutio.Mqtt.Neato
{
    /// <summary>
    /// Main program entry point.
    /// </summary>
    public class Program
    {
        /// <summary>
        /// Main program entry point.
        /// </summary>
        /// <returns>Awaitable <see cref="Task" />.</returns>
        public static async Task Main()
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
                // Validates existing or gets new secretKey
                var secretKey = await GetSecretKey(config);
                var hostBuilder = CreateHostBuilder(config, secretKey);
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
        /// <param name="secretKey">SecretKey for the specified robot.</param>
        /// <returns>A configured <see cref="IHostBuilder"/>.</returns>
        private static IHostBuilder CreateHostBuilder(IConfiguration config, string secretKey)
        {
            return new HostBuilder()
                .ConfigureAppConfiguration((hostContext, configuration) => configuration.AddConfiguration(config))
                .ConfigureLogging((hostingContext, logging) => logging.AddSerilog())
                .ConfigureServices((hostContext, services) =>
                {
                    // Setup client
                    services.AddScoped<INucleoClient, NucleoClient>(serviceProvider => new NucleoClient(config.GetValue<string>("neato:serialNumber"), secretKey));
                    services.AddScoped<IRobot, Robot>();

                    // Setup service instance
                    services.AddScoped<IHostedService, NeatoMqttService>(serviceProvider =>
                    {
                        var brokerSettings = new Core.BrokerSettings
                        {
                            BrokerIp = config.GetValue<string>("mqtt:brokerIp"),
                            BrokerPort = config.GetValue<int>("mqtt:brokerPort"),
                            BrokerUsername = config.GetValue<string>("mqtt:brokerUsername"),
                            BrokerPassword = config.GetValue<string>("mqtt:brokerPassword"),
                            BrokerUseTls = config.GetValue<bool>("mqtt:brokerUseTls", false)
                        };

                        // TLS settings
                        if (brokerSettings.BrokerUseTls)
                        {
                            var brokerTlsSettings = new Core.BrokerTlsSettings
                            {
                                AllowUntrustedCertificates = config.GetValue<bool>("mqtt:brokerTlsSettings:allowUntrustedCertificates", false),
                                IgnoreCertificateChainErrors = config.GetValue<bool>("mqtt:brokerTlsSettings:ignoreCertificateChainErrors", false),
                                IgnoreCertificateRevocationErrors = config.GetValue<bool>("mqtt:brokerTlsSettings:ignoreCertificateRevocationErrors", false)
                            };

                            switch (config.GetValue<string>("mqtt:brokerTlsSettings:protocol", "1.2"))
                            {
                                case "1.0":
                                    brokerTlsSettings.SslProtocol = System.Security.Authentication.SslProtocols.Tls;
                                    break;
                                case "1.1":
                                    brokerTlsSettings.SslProtocol = System.Security.Authentication.SslProtocols.Tls11;
                                    break;
                                case "1.2":
                                default:
                                    brokerTlsSettings.SslProtocol = System.Security.Authentication.SslProtocols.Tls12;
                                    break;
                            }

                            var brokerTlsCertificatesSection = config.GetSection("mqtt:brokerTlsSettings:certificates");
                            brokerTlsSettings.Certificates = brokerTlsCertificatesSection.GetChildren()
                                .Select(x =>
                                {
                                    var file = x.GetValue<string>("file");
                                    var passPhrase = x.GetValue<string>("passPhrase");

                                    if (!File.Exists(file))
                                        throw new FileNotFoundException($"Broker Certificate '{file}' is missing!");

                                    return !string.IsNullOrEmpty(passPhrase) ?
                                        new X509Certificate2(file, passPhrase) :
                                        new X509Certificate2(file);
                                }).ToList();

                            brokerSettings.BrokerTlsSettings = brokerTlsSettings;
                        }

                        return new NeatoMqttService(
                            serviceProvider.GetRequiredService<ILogger<NeatoMqttService>>(),
                            serviceProvider.GetRequiredService<IRobot>(),
                            config.GetValue<string>("neato:neatoName"),
                            config.GetValue<int>("neato:refreshInterval"),
                            brokerSettings);
                    });
                });
        }

        /// <summary>
        /// Gets current, or gets new secret key.
        /// </summary>
        /// <param name="config">External configuration.</param>
        /// <returns>The secretKey for the serialNumber specified in config.</returns>
        private static async Task<string> GetSecretKey(IConfiguration config)
        {
            // initialize tokens
            var beeHiveClient = new BeeHiveClient(
                config.GetValue<string>("neato:email"),
                config.GetValue<string>("neato:password"));

            var robots = await beeHiveClient.GetRobotsAsync();
            var robot = robots.FirstOrDefault(x => x.Serial == config.GetValue<string>("neato:serialNumber"));
            if (robot == null)
                throw new Exception("Cannot match serialNumber with account");

            return robot.SecretKey;
        }
    }
}
