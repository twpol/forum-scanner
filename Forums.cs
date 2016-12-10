using System;
using System.Collections.Generic;
using System.IO;
using System.Net.Http;
using System.Threading.Tasks;
using HtmlAgilityPack;
using Microsoft.Extensions.Configuration;

namespace ForumScanner
{
    public static class Forums
    {
        public static async Task Scan(IConfigurationSection configuration, Storage storage, HttpClient client)
        {
            Console.WriteLine($"Scanning forum {configuration.Key}...");

            // For some reason the HtmlAgilityPack default is CanOverlap | Empty which means <form> elements never contain anything!
            HtmlNode.ElementsFlags["form"] = HtmlElementFlag.CanOverlap;

            if (configuration["LoginForm:Url"] != null)
            {
                await Forms.LoadAndSubmit(configuration.GetSection("LoginForm"), client);
            }

            var forumQueue = new Queue<string>();
            var forumSet = new HashSet<string>();
            forumQueue.Enqueue(configuration["RootUrl"]);
            forumSet.Add(configuration["RootUrl"]);

            var topicSet = new HashSet<string>();

            while (forumQueue.Count > 0)
            {
                var result = await ScanForum(configuration, storage, client, forumQueue.Dequeue());
                foreach (var forum in result.Forums)
                {
                    if (!forumSet.Contains(forum))
                    {
                        forumQueue.Enqueue(forum);
                        forumSet.Add(forum);
                    }
                }
                foreach (var topic in result.Topics)
                {
                    if (!topicSet.Contains(topic))
                    {
                        topicSet.Add(topic);
                    }
                }
            }

            foreach (var topic in topicSet)
            {
                await ScanTopic(configuration, storage, client, topic);
                break;
            }
        }

        private static async Task<ForumScanResult> ScanForum(IConfigurationSection configuration, Storage storage, HttpClient client, string forumUrl)
        {
            Console.WriteLine(forumUrl);

            var result = new ForumScanResult();

            var response = await client.GetAsync(forumUrl);

            var document = new HtmlDocument();
            document.Load(await response.Content.ReadAsStreamAsync());

            var forumItems = document.DocumentNode.SelectNodes(configuration["Forums:Item"]);
            if (forumItems != null)
            {
                foreach (var forumItem in forumItems)
                {
                    var lastUpdated = GetHtmlValue(forumItem, configuration.GetSection("Forums:LastUpdated"));
                    result.Forums.Add(GetHtmlValue(forumItem, configuration.GetSection("Forums:Link")));
                }
            }

            var topicItems = document.DocumentNode.SelectNodes(configuration["Topics:Item"]);
            if (topicItems != null)
            {
                foreach (var topicItem in topicItems)
                {
                    var lastUpdated = GetHtmlValue(topicItem, configuration.GetSection("Topics:LastUpdated"));
                    result.Topics.Add(GetHtmlValue(topicItem, configuration.GetSection("Topics:Link")));
                }
            }

            return result;
        }

        private static async Task ScanTopic(IConfigurationSection configuration, Storage storage, HttpClient client, string topicUrl)
        {
            Console.WriteLine(topicUrl);

            var response = await client.GetAsync(topicUrl);

            var document = new HtmlDocument();
            document.Load(await response.Content.ReadAsStreamAsync());

            var messages = document.DocumentNode.SelectNodes(configuration["Messages:Item"]);
            foreach (var message in messages)
            {
                var messageIndex = GetHtmlValue(message, configuration.GetSection("Messages:Index"));
                var messageLink = GetHtmlValue(message, configuration.GetSection("Messages:Link"));
                var messageReplyLink = GetHtmlValue(message, configuration.GetSection("Messages:ReplyLink"));
                var messageDate = GetHtmlValue(message, configuration.GetSection("Messages:Date"));
                var messageAuthor = GetHtmlValue(message, configuration.GetSection("Messages:Author"));
                var messageBody = GetHtmlValue(message, configuration.GetSection("Messages:Body"));
                Console.WriteLine($"Message: {messageIndex} {messageDate} {messageAuthor} {messageLink} {messageBody.Length}");
            }
        }

        private static string GetHtmlValue(HtmlNode node, IConfigurationSection configuration)
        {
            foreach (var type in configuration.GetChildren())
            {
                switch (type.Key)
                {
                    case "InnerText":
                        return node.SelectSingleNode(type.Value).InnerText;
                    case "InnerHtml":
                        return node.SelectSingleNode(type.Value).InnerHtml;
                    case "Attribute":
                        foreach (var attribute in type.GetChildren())
                        {
                            return node.SelectSingleNode(attribute.Value).Attributes[attribute.Key]?.DValue() ?? $"<default:{attribute.Path}>";
                        }
                        goto default;
                    default:
                        throw new InvalidDataException($"Invalid value type for GetHtmlValue: {type.Path}");
                }
            }
            throw new InvalidDataException($"Missing value type for GetHtmlValue: {configuration.Path}");
        }
    }

    public class ForumScanResult
    {
        public List<string> Forums;
        public List<string> Topics;

        public ForumScanResult()
        {
            Forums = new List<string>();
            Topics = new List<string>();
        }
    }
}
