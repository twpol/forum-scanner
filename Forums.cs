using System;
using System.Collections.Generic;
using System.IO;
using System.Net.Http;
using System.Threading.Tasks;
using HtmlAgilityPack;
using Microsoft.Extensions.Configuration;

namespace ForumScanner
{
    public class Forums
    {
        IConfigurationSection Configuration { get; }
        Storage Storage { get; }
        HttpClient Client { get; }

        public Forums(IConfigurationSection configuration, Storage storage, HttpClient client)
        {
            Configuration = configuration;
            Storage = storage;
            Client = client;
        }

        public async Task Scan()
        {
            Console.WriteLine($"Scanning forum {Configuration.Key}...");

            // For some reason the HtmlAgilityPack default is CanOverlap | Empty which means <form> elements never contain anything!
            HtmlNode.ElementsFlags["form"] = HtmlElementFlag.CanOverlap;

            if (Configuration["LoginForm:Url"] != null)
            {
                await Forms.LoadAndSubmit(Configuration.GetSection("LoginForm"), Client);
            }

            var forumQueue = new Queue<string>();
            var forumSet = new HashSet<string>();
            forumQueue.Enqueue(Configuration["RootUrl"]);
            forumSet.Add(Configuration["RootUrl"]);

            var topicSet = new HashSet<string>();

            while (forumQueue.Count > 0)
            {
                var result = await ScanForum(forumQueue.Dequeue());
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
                await ScanTopic(topic);
                break;
            }
        }

        private async Task<ForumScanResult> ScanForum(string forumUrl)
        {
            Console.WriteLine(forumUrl);

            var result = new ForumScanResult();

            var response = await Client.GetAsync(forumUrl);

            var document = new HtmlDocument();
            document.Load(await response.Content.ReadAsStreamAsync());

            var forumItems = document.DocumentNode.SelectNodes(Configuration["Forums:Item"]);
            if (forumItems != null)
            {
                foreach (var forumItem in forumItems)
                {
                    var lastUpdated = GetHtmlValue(forumItem, Configuration.GetSection("Forums:LastUpdated"));
                    result.Forums.Add(GetHtmlValue(forumItem, Configuration.GetSection("Forums:Link")));
                }
            }

            var topicItems = document.DocumentNode.SelectNodes(Configuration["Topics:Item"]);
            if (topicItems != null)
            {
                foreach (var topicItem in topicItems)
                {
                    var lastUpdated = GetHtmlValue(topicItem, Configuration.GetSection("Topics:LastUpdated"));
                    result.Topics.Add(GetHtmlValue(topicItem, Configuration.GetSection("Topics:Link")));
                }
            }

            return result;
        }

        private async Task ScanTopic(string topicUrl)
        {
            Console.WriteLine(topicUrl);

            var response = await Client.GetAsync(topicUrl);

            var document = new HtmlDocument();
            document.Load(await response.Content.ReadAsStreamAsync());

            var messages = document.DocumentNode.SelectNodes(Configuration["Messages:Item"]);
            foreach (var message in messages)
            {
                var messageIndex = GetHtmlValue(message, Configuration.GetSection("Messages:Index"));
                var messageLink = GetHtmlValue(message, Configuration.GetSection("Messages:Link"));
                var messageReplyLink = GetHtmlValue(message, Configuration.GetSection("Messages:ReplyLink"));
                var messageDate = GetHtmlValue(message, Configuration.GetSection("Messages:Date"));
                var messageAuthor = GetHtmlValue(message, Configuration.GetSection("Messages:Author"));
                var messageBody = GetHtmlValue(message, Configuration.GetSection("Messages:Body"));
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
