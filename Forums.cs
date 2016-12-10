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

        Dictionary<ForumItemType, Regex> IdUrlPattern { get; }

        public Forums(IConfigurationSection configuration, Storage storage, HttpClient client)
        {
            Configuration = configuration;
            Storage = storage;
            Client = client;

            IdUrlPattern = new Dictionary<ForumItemType, Regex>() {
                { ForumItemType.Forum, new Regex(Configuration["Forums:IdUrlPattern"]) },
                { ForumItemType.Topic, new Regex(Configuration["Topics:IdUrlPattern"]) },
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

            var rootScanItem = new ForumItem(ForumItemType.Forum, Configuration["RootUrl"], "");

            var forumQueue = new Queue<ForumItem>();
            var forumSet = new HashSet<ForumItem>();
            forumQueue.Enqueue(rootScanItem);
            forumSet.Add(rootScanItem);

            var topicSet = new HashSet<ForumItem>();

            while (forumQueue.Count > 0)
            {
                var result = await GetUpdatedSubForumsAndTopics(forumQueue.Dequeue());
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
                var posts = await GetNewPosts(topic);
                break;
            }
        }

        private async Task<ForumItems> GetUpdatedSubForumsAndTopics(ForumItem item)
        {
            var result = new ForumItems();

            Console.WriteLine($"  Scanning {item}...");
            var response = await Client.GetAsync(item.Link);

            var document = new HtmlDocument();
            document.Load(await response.Content.ReadAsStreamAsync());

            var forumItems = document.DocumentNode.SelectNodes(Configuration["Forums:Item"]);
            if (forumItems != null)
            {
                foreach (var forumItem in forumItems)
                {
                    result.Forums.Add(await CheckHtmlItem(ForumItemType.Forum, forumItem));
                }
            }

            var topicItems = document.DocumentNode.SelectNodes(Configuration["Topics:Item"]);
            if (topicItems != null)
            {
                foreach (var topicItem in topicItems)
                {
                    result.Topics.Add(await CheckHtmlItem(ForumItemType.Topic, topicItem));
                }
            }

            return result;
        }

        private async Task<ForumItem> CheckHtmlItem(ForumItemType type, HtmlNode htmlItem)
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

            return new ForumItem(type, link, updated);
        }

        private async Task<List<ForumPostItem>> GetNewPosts(ForumItem item)
        {
            Console.WriteLine($"  Scanning {item}...");
            var response = await Client.GetAsync(item.Link);

            var document = new HtmlDocument();
            document.Load(await response.Content.ReadAsStreamAsync());

            var postItems = document.DocumentNode.SelectNodes(Configuration["Posts:Item"]);
            foreach (var postItem in postItems)
            {
                var post = new ForumPostItem(
                    GetHtmlValue(postItem, Configuration.GetSection("Posts:Index")),
                    GetHtmlValue(postItem, Configuration.GetSection("Posts:Link")),
                    GetHtmlValue(postItem, Configuration.GetSection("Posts:ReplyLink")),
                    GetHtmlValue<DateTimeOffset>(postItem, Configuration.GetSection("Posts:Date")),
                    GetHtmlValue(postItem, Configuration.GetSection("Posts:Author")),
                    GetHtmlValue(postItem, Configuration.GetSection("Posts:Body"))
                );
                Console.WriteLine($"    Message: {post.Index} {post.Date} {post.Author} {post.Link} {post.Body.Length}");
            }

            return new List<ForumPostItem>();
        }

        private static T GetHtmlValue<T>(HtmlNode node, IConfigurationSection configuration)
        {
            var value = GetHtmlValue(node, configuration);
            switch (typeof(T).FullName)
            {
                case "System.String":
                    return (T)(object)value;
                case "System.DateTimeOffset":
                    return (T)(object)DateTimeOffset.Parse(value);
                default:
                    throw new InvalidDataException($"Invalid type {typeof(T).FullName} for GetHtmlValue<T>");
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

    class ForumItems
    {
        public List<ForumItem> Forums { get; }
        public List<ForumItem> Topics { get; }

        public ForumItems()
        {
            Forums = new List<ForumItem>();
            Topics = new List<ForumItem>();
        }
    }

    class ForumItem
    {
        public ForumItemType Type { get; }
        public string Link { get; }
        public string Updated { get; }

        public ForumItem(ForumItemType type, string link, string updated)
        {
            Type = type;
            Link = link;
            Updated = updated;
        }

        override public string ToString()
        {
            return $"{Type} {Link}";
        }
    }

    class ForumPostItem : ForumItem
    {
        public string Index { get; }
        public string ReplyLink { get; }
        public DateTimeOffset Date { get; }
        public string Author { get; }
        public string Body { get; }

        public ForumPostItem(string index, string link, string replyLink, DateTimeOffset date, string author, string body)
            : base(ForumItemType.Post, link, null)
        {
            Index = index;
            ReplyLink = replyLink;
            Date = date;
            Author = author;
            Body = body;
        }
    }

    enum ForumItemType
    {
        Forum,
        Topic,
        Post,
    }
}
