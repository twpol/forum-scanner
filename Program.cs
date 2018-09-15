using System;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Threading.Tasks;
using Microsoft.Extensions.Configuration;

namespace ForumScanner
{
    class Program
    {
        static void Main(string[] args)
        {
            MainAsync(args).Wait();
        }

        static async Task MainAsync(string[] args)
        {
            try
            {
                ParseCommandLine(args, out var config, out var debug);
                var configuration = LoadConfiguration(config);
                var storage = await LoadStorage(configuration, debug.Value);
                var client = CreateHttpClient();

                foreach (var configurationForum in configuration.GetSection("Forums").GetChildren())
                {
                    var forums = new Forums(configurationForum, storage, client, debug.Value);
                    await forums.Process();
                }

                storage.Close();
            }
            catch (CommandLineParser.Exceptions.CommandLineException e)
            {
                Console.WriteLine(e.Message);
            }
        }

        static void ParseCommandLine(string[] args, out CommandLineParser.Arguments.FileArgument config, out CommandLineParser.Arguments.SwitchArgument debug)
        {
            config = new CommandLineParser.Arguments.FileArgument('c', "config")
            {
                DefaultValue = new FileInfo("config.json")
            };

            debug = new CommandLineParser.Arguments.SwitchArgument('d', "debug", false);

            var commandLineParser = new CommandLineParser.CommandLineParser()
            {
                Arguments = {
                    config,
                    debug,
                }
            };

            commandLineParser.ParseCommandLine(args);
        }

        static IConfigurationRoot LoadConfiguration(CommandLineParser.Arguments.FileArgument config)
        {
            return new ConfigurationBuilder()
                .AddJsonFile(config.Value.FullName, true)
                .Build();
        }

        static async Task<Storage> LoadStorage(IConfigurationRoot configuration, bool debug)
        {
            var storage = new Storage(configuration.GetConnectionString("Storage"), debug);
            await storage.Open();
            await storage.ExecuteNonQueryAsync("CREATE TABLE IF NOT EXISTS Forums (ForumId integer NOT NULL UNIQUE, Updated text)");
            await storage.ExecuteNonQueryAsync("CREATE TABLE IF NOT EXISTS Topics (TopicId integer NOT NULL UNIQUE, Updated text)");
            await storage.ExecuteNonQueryAsync("CREATE TABLE IF NOT EXISTS Posts (PostId integer NOT NULL UNIQUE, Updated text)");
            return storage;
        }

        static HttpClient CreateHttpClient()
        {
            var clientHandler = new HttpClientHandler()
            {
                CookieContainer = new CookieContainer(),
            };
            var client = new HttpClient(clientHandler);
            client.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue("JGR-ForumScanner", "1.0"));
            return client;
        }
    }
}
