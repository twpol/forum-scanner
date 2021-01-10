using System;
using System.IO;
using System.Linq;
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
                ForcedDefaultValue = new FileInfo("config.json")
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
            await storage.ExecuteNonQueryAsync("CREATE TABLE IF NOT EXISTS Forums (ForumId text NOT NULL UNIQUE, Updated text)");
            await storage.ExecuteNonQueryAsync("CREATE TABLE IF NOT EXISTS Topics (TopicId text NOT NULL UNIQUE, Updated text)");
            await storage.ExecuteNonQueryAsync("CREATE TABLE IF NOT EXISTS Posts (PostId text NOT NULL UNIQUE, Updated text)");
            await storage.ExecuteNonQueryAsync("CREATE TABLE IF NOT EXISTS Errors (Source text, Date datetime, Error text)");
            await ApplyMigrationToTextIDs(configuration, storage);
            return storage;
        }

        static async Task ApplyMigrationToTextIDs(IConfigurationRoot configuration, Storage storage)
        {
            // Schema migration: convert IDs from integer to text, in the format $"{ForumName}/{OriginalValue}"
            var forumNames = configuration.GetSection("Forums").GetChildren().ToArray();
            Func<Task> forumNamePreCheck = () =>
            {
                if (forumNames.Length > 1) throw new InvalidOperationException("Unable to do schema migration from `integer` to `text` IDs because multiple forum configurations exist");
                return Task.CompletedTask;
            };
            await storage.AlterTableAlterColumnType("Forums", "ForumId", "text", forumNamePreCheck, async () => await storage.ExecuteNonQueryAsync($"UPDATE Forums SET ForumId = @Param0 || '/' || ForumId", forumNames[0].Key));
            await storage.AlterTableAlterColumnType("Topics", "TopicId", "text", forumNamePreCheck, async () => await storage.ExecuteNonQueryAsync($"UPDATE Topics SET TopicId = @Param0 || '/' || TopicId", forumNames[0].Key));
            await storage.AlterTableAlterColumnType("Posts", "PostId", "text", forumNamePreCheck, async () => await storage.ExecuteNonQueryAsync($"UPDATE Posts SET PostId = @Param0 || '/' || PostId", forumNames[0].Key));
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
