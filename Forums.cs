using System;
using System.Net.Http;
using System.Threading.Tasks;
using HtmlAgilityPack;
using Microsoft.Extensions.Configuration;

namespace ForumScanner
{
    public static class Forums
    {
        public static async Task Scan(HttpClient client, IConfigurationSection forum)
        {
            Console.WriteLine($"Scanning forum {forum.Key}...");

            // For some reason the HtmlAgilityPack default is CanOverlap | Empty which means <form> elements never contain anything!
            HtmlNode.ElementsFlags["form"] = HtmlElementFlag.CanOverlap;

            await Forms.LoadAndSubmit(forum.GetSection("login-form"), client);
        }
    }
}
