using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Threading.Tasks;
using Microsoft.Extensions.Configuration;

namespace ForumScanner
{
    public class Program
    {
        public static void Main(string[] args)
        {
            MainAsync(args).Wait();
        }

        private static async Task MainAsync(string[] args)
        {
            var configuration = LoadConfiguration();
            var storage = await LoadStorage(configuration);
            var client = CreateHttpClient();

            foreach (var configurationForum in configuration.GetSection("Forums").GetChildren())
            {
                await Forums.Scan(configurationForum, storage, client);
            }

            storage.Close();
        }

        private static IConfigurationRoot LoadConfiguration()
        {
            return new ConfigurationBuilder()
                .AddJsonFile("appsettings.json", true)
                .Build();
        }

        private static async Task<Storage> LoadStorage(IConfigurationRoot configuration)
        {
            var storage = new Storage(configuration.GetConnectionString("Storage"));
            await storage.Open();
            await storage.ExecuteNonQueryAsync("CREATE TABLE IF NOT EXISTS Forums (ForumId integer NOT NULL UNIQUE, LastModified text)");
            await storage.ExecuteNonQueryAsync("CREATE TABLE IF NOT EXISTS Topics (TopicId integer NOT NULL UNIQUE, LastModified text)");
            await storage.ExecuteNonQueryAsync("CREATE TABLE IF NOT EXISTS Posts (PostId integer NOT NULL UNIQUE)");
            return storage;
        }

        private static HttpClient CreateHttpClient()
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
