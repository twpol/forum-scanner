using System;
using System.Collections.Generic;
using System.IO;
using System.Net.Http;
using System.Text.RegularExpressions;
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

        Dictionary<ScanItemType, Regex> IdUrlPattern { get; }

        public Forums(IConfigurationSection configuration, Storage storage, HttpClient client)
        {
            Configuration = configuration;
            Storage = storage;
            Client = client;

            IdUrlPattern = new Dictionary<ScanItemType, Regex>() {
                { ScanItemType.Forum, new Regex(Configuration["Forums:IdUrlPattern"]) },
                { ScanItemType.Topic, new Regex(Configuration["Topics:IdUrlPattern"]) },
            };
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

            var rootScanItem = new ScanItem(ScanItemType.Forum, Configuration["RootUrl"], "");

            var forumQueue = new Queue<ScanItem>();
            var forumSet = new HashSet<ScanItem>();
            forumQueue.Enqueue(rootScanItem);
            forumSet.Add(rootScanItem);

            var topicSet = new HashSet<ScanItem>();

            while (forumQueue.Count > 0)
            {
                var result = await ScanForum(forumQueue.Dequeue());
                foreach (var forum in result.Forums)
                {
                    if (forum != null && !forumSet.Contains(forum))
                    {
                        forumQueue.Enqueue(forum);
                        forumSet.Add(forum);
                    }
                }
                foreach (var topic in result.Topics)
                {
                    if (topic != null && !topicSet.Contains(topic))
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

        private async Task<ScanResult> ScanForum(ScanItem item)
        {
            var result = new ScanResult();

            var response = await Client.GetAsync(item.Url);

            var document = new HtmlDocument();
            document.Load(await response.Content.ReadAsStreamAsync());

            var forumItems = document.DocumentNode.SelectNodes(Configuration["Forums:Item"]);
            if (forumItems != null)
            {
                foreach (var forumItem in forumItems)
                {
                    result.Forums.Add(await CheckHtmlItem(ScanItemType.Forum, forumItem));
                }
            }

            var topicItems = document.DocumentNode.SelectNodes(Configuration["Topics:Item"]);
            if (topicItems != null)
            {
                foreach (var topicItem in topicItems)
                {
                    result.Forums.Add(await CheckHtmlItem(ScanItemType.Topic, topicItem));
                }
            }

            return result;
        }

        private async Task<ScanItem> CheckHtmlItem(ScanItemType type, HtmlNode htmlItem)
        {
            var link = GetHtmlValue(htmlItem, Configuration.GetSection($"{type}s:Link"));
            var updated = GetHtmlValue(htmlItem, Configuration.GetSection($"{type}s:Updated"));
            var id = IdUrlPattern[type].Match(link)?.Groups?[1]?.Value;
            if (id == null)
            {
                return null;
            }

            var lastUpdated = await Storage.ExecuteScalarAsync($"SELECT Updated FROM {type}s WHERE {type}Id = @Param0", id);
            if (updated == lastUpdated as string)
            {
                return null;
            }

            return new ScanItem(type, link, updated);
        }

        private async Task ScanTopic(ScanItem item)
        {
            var response = await Client.GetAsync(item.Url);

            var document = new HtmlDocument();
            document.Load(await response.Content.ReadAsStreamAsync());

            var posts = document.DocumentNode.SelectNodes(Configuration["Posts:Item"]);
            foreach (var post in posts)
            {
                var postIndex = GetHtmlValue(post, Configuration.GetSection("Posts:Index"));
                var postLink = GetHtmlValue(post, Configuration.GetSection("Posts:Link"));
                var postReplyLink = GetHtmlValue(post, Configuration.GetSection("Posts:ReplyLink"));
                var postDate = GetHtmlValue(post, Configuration.GetSection("Posts:Date"));
                var postAuthor = GetHtmlValue(post, Configuration.GetSection("Posts:Author"));
                var postBody = GetHtmlValue(post, Configuration.GetSection("Posts:Body"));
                Console.WriteLine($"Message: {postIndex} {postDate} {postAuthor} {postLink} {postBody.Length}");
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

    class ScanResult
    {
        public List<ScanItem> Forums { get; }
        public List<ScanItem> Topics { get; }

        public ScanResult()
        {
            Forums = new List<ScanItem>();
            Topics = new List<ScanItem>();
        }
    }

    class ScanItem
    {
        public ScanItemType Type { get; }
        public string Url { get; }
        public string Updated { get; }

        public ScanItem(ScanItemType type, string url, string updated)
        {
            Type = type;
            Url = url;
            Updated = updated;
        }
    }

    enum ScanItemType
    {
        Forum,
        Topic,
        Post,
    }
}
