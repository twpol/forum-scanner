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

            var client = CreateHttpClient();

            foreach (var forum in configuration.GetSection("forums").GetChildren())
            {
                await Forums.Scan(client, forum);
            }
        }

        private static IConfigurationRoot LoadConfiguration()
        {
            return new ConfigurationBuilder()
                .AddJsonFile("appsettings.json", true)
                .Build();
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
