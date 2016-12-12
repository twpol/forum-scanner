using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
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

        static readonly Regex WhitespacePattern = new Regex(@"\s+");

        public Forums(IConfigurationSection configuration, Storage storage, HttpClient client)
        {
            Configuration = configuration;
            Storage = storage;
            Client = client;

            IdUrlPattern = new Dictionary<ForumItemType, Regex>() {
                { ForumItemType.Forum, new Regex(Configuration["Forums:IdUrlPattern"]) },
                { ForumItemType.Topic, new Regex(Configuration["Topics:IdUrlPattern"]) },
                { ForumItemType.Post, new Regex(Configuration["Posts:IdIdPattern"]) },
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

            var posts = new List<ForumPostItem>();
            foreach (var topic in topicSet)
            {
                posts.AddRange(await GetNewPosts(topic));
            }

            foreach (var post in posts)
            {
                await SendEmail(post);
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

            var forumName = GetHtmlValue(document.DocumentNode, Configuration.GetSection("Posts:ForumName"));

            var posts = new List<ForumPostItem>();
            var postItems = document.DocumentNode.SelectNodes(Configuration["Posts:Item"]);
            foreach (var postItem in postItems)
            {
                var idString = IdUrlPattern[ForumItemType.Post].Match(postItem.Attributes["id"].Value)?.Groups?[1]?.Value;
                if (idString == null)
                {
                    continue;
                }

                int id;
                if (!int.TryParse(idString, out id))
                {
                    continue;
                }

                var lastPostId = await Storage.ExecuteScalarAsync("SELECT PostId FROM Posts WHERE PostId = @Param0", id);
                if (lastPostId != null)
                {
                    continue;
                }

                posts.Add(new ForumPostItem(
                    forumName,
                    id,
                    GetHtmlValue<int>(postItem, Configuration.GetSection("Posts:Index")),
                    GetHtmlValue(postItem, Configuration.GetSection("Posts:Link")),
                    GetHtmlValue(postItem, Configuration.GetSection("Posts:ReplyLink")),
                    GetHtmlValue<DateTimeOffset>(postItem, Configuration.GetSection("Posts:Date")),
                    GetHtmlValue(postItem, Configuration.GetSection("Posts:Author")),
                    GetHtmlValue<HtmlNode>(postItem, Configuration.GetSection("Posts:Body"))
                ));
            }

            return posts;
        }

        private async Task SendEmail(ForumPostItem post)
        {
            Console.WriteLine($"  Sending {post.Type} {post.Id} (#{post.Index} at {post.Date.ToString("T")} on {post.Date.ToString("D")} by {post.Author} in {post.ForumName})...");
            // Console.WriteLine(GetEmailBody(post));

            await Storage.ExecuteNonQueryAsync("INSERT INTO Posts (PostId) VALUES (@Param0)", post.Id);
        }

        private static string GetEmailBody(ForumPostItem post)
        {
            return "<!DOCTYPE html>" +
                "<html>" +
                    "<head>" +
                        "<style>" +
                            "html { font-family: sans-serif; }" +
                            "p.citation { background: lightgrey; padding: 0.5ex; }" +
                            "blockquote { border-left: 0.5ex solid lightgrey; padding-left: 1.0ex; }" +
                            ".email-notifications-footer hr { border: 1px solid grey; }" +
                            ".email-notifications-footer a { color: grey; }" +
                        "</style>" +
                    "</head>" +
                    "<body>" +
                        $"{FormatBodyForEmail(post.Body)}" +
                        "<div class='email-notifications-footer'>" +
                            "<hr>" +
                            $"Post #{post.Index} at {post.Date.ToString("T")} on {post.Date.ToString("D")} by {post.Author} in {post.ForumName} (<a href='{post.ReplyLink}'>reply</a>, <a href='{post.Link}'>view in forum</a>)" +
                        "</div>" +
                    "</body>" +
                "</html>";
        }

        private static string FormatBodyForEmail(HtmlNode body)
        {
            // <p class='citation'>
            //   <a class='snapback' rel='citation' href='...'>
            //     <img src='...' alt='View Post'>
            //   </a>
            //   USER, on DATE - TIME, said:
            // </p>
            // <div class="blockquote">
            //   <div class='quote'>
            //     Quote body
            //   </div>
            // </div>
            var citations = body.SelectNodes(".//p[@class='citation']/a[@class='snapback']");
            if (citations != null)
            {
                foreach (var node in citations)
                {
                    node.Remove();
                }
            }
            var blockquotes = body.SelectNodes(".//div[@class='blockquote' and count(*) = 1 and div[@class='quote']]");
            if (blockquotes != null)
            {
                foreach (var node in blockquotes)
                {
                    var blockquote = HtmlNode.CreateNode("<blockquote>");
                    blockquote.AppendChildren(node.SelectSingleNode("./div[@class='quote']").ChildNodes);
                    node.ParentNode.ReplaceChild(blockquote, node);
                }
            }

            return body.OuterHtml;
        }

        private static T GetHtmlValue<T>(HtmlNode node, IConfigurationSection configuration)
        {
            var value = GetHtmlValue(node, configuration);
            switch (typeof(T).FullName)
            {
                case "HtmlAgilityPack.HtmlNode":
                    var document = new HtmlDocument();
                    document.LoadHtml(value);
                    return (T)(object)document.DocumentNode;
                case "System.DateTimeOffset":
                    return (T)(object)DateTimeOffset.Parse(value);
                case "System.Int32":
                    return (T)(object)int.Parse(value.Replace("#", ""));
                case "System.String":
                    return (T)(object)value;
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
                        return WhitespacePattern.Replace(WebUtility.HtmlDecode(node.SelectSingleNode(type.Value)?.InnerText ?? $"<default:{configuration.Path}>"), " ").Trim();
                    case "InnerHtml":
                        return node.SelectSingleNode(type.Value).InnerHtml;
                    case "Attribute":
                        foreach (var attribute in type.GetChildren())
                        {
                            return WebUtility.HtmlDecode(node.SelectSingleNode(attribute.Value)?.Attributes?[attribute.Key]?.Value ?? $"<default:{configuration.Path}>");
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
        public string ForumName { get; }
        public int Id { get; }
        public int Index { get; }
        public string ReplyLink { get; }
        public DateTimeOffset Date { get; }
        public string Author { get; }
        public HtmlNode Body { get; }

        public ForumPostItem(string forumName, int id, int index, string link, string replyLink, DateTimeOffset date, string author, HtmlNode body)
            : base(ForumItemType.Post, link, null)
        {
            ForumName = forumName;
            Id = id;
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
