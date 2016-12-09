using System;
using Microsoft.Extensions.Configuration;

namespace ConsoleApplication
{
    public class Program
    {
        public static void Main(string[] args)
        {
            var configuration = new ConfigurationBuilder()
                .AddJsonFile("appsettings.json", true)
                .Build();

            foreach (var forum in configuration.GetSection("forums").GetChildren()) {
                Console.WriteLine($"Scanning forum {forum.Key}...");
            }
            Console.WriteLine($"Hello World {configuration["name"]}!");
        }
    }
}
