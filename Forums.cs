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

        public async Task Process()
        {
            Console.WriteLine($"Processing {Configuration.Key}...");

            // For some reason the HtmlAgilityPack default is CanOverlap | Empty which means <form> elements never contain anything!
            HtmlNode.ElementsFlags["form"] = HtmlElementFlag.CanOverlap;

            if (Configuration["LoginForm:Url"] != null)
            {
                await Forms.LoadAndSubmit(Configuration.GetSection("LoginForm"), Client);
            }

            await ProcessForum(new ForumItem(ForumItemType.Forum, 0, Configuration["RootUrl"], ""));
        }

        private async Task ProcessForum(ForumItem forum)
        {
            if (forum == null)
            {
                return;
            }

            Console.WriteLine($"  Processing {forum}...");
            var document = await LoadItem(forum);

            var forumItems = document.DocumentNode.SelectNodes(Configuration["Forums:Item"]);
            if (forumItems != null)
            {
                foreach (var forumItem in forumItems)
                {
                    await ProcessForum(await CheckItemIsUpdated(ForumItemType.Forum, forumItem));
                }
            }

            var topicItems = document.DocumentNode.SelectNodes(Configuration["Topics:Item"]);
            if (topicItems != null)
            {
                foreach (var topicItem in topicItems)
                {
                    await ProcessTopic(await CheckItemIsUpdated(ForumItemType.Topic, topicItem));
                    break; // TODO
                }
            }

            await SetItemUpdated(forum);
        }

        private async Task ProcessTopic(ForumItem topic)
        {
            if (topic == null)
            {
                return;
            }

            Console.WriteLine($"    Processing {topic}...");
            var document = await LoadItem(topic);

            var postItems = document.DocumentNode.SelectNodes(Configuration["Posts:Item"]);
            if (postItems != null)
            {
                foreach (var postItem in postItems)
                {
                    await ProcessPost(await CheckItemIsUpdated(ForumItemType.Post, postItem));
                }
            }

            await SetItemUpdated(topic);
        }

        private async Task ProcessPost(ForumItem item)
        {
            if (item == null || !(item is ForumPostItem))
            {
                return;
            }
            var post = item as ForumPostItem;

            Console.WriteLine($"      Processing {post}...");

            // Console.WriteLine(GetEmailBody(post));

            await SetItemUpdated(post);
        }

        private async Task<HtmlDocument> LoadItem(ForumItem item)
        {
            var response = await Client.GetAsync(item.Link);
            var document = new HtmlDocument();
            document.Load(await response.Content.ReadAsStreamAsync());
            return document;
        }

        private async Task<ForumItem> CheckItemIsUpdated(ForumItemType type, HtmlNode htmlItem)
        {
            var link = GetHtmlValue(htmlItem, Configuration.GetSection($"{type}s:Link"));
            var updated = GetHtmlValue(htmlItem, Configuration.GetSection($"{type}s:Updated"));
            var idString = IdUrlPattern[type].Match(type == ForumItemType.Post ? htmlItem.Attributes["id"].Value : link)?.Groups?[1]?.Value;
            if (idString == null)
            {
                return null;
            }

            int id;
            if (!int.TryParse(idString, out id))
            {
                return null;
            }

            var lastUpdated = await Storage.ExecuteScalarAsync($"SELECT Updated FROM {type}s WHERE {type}Id = @Param0", id);
            if (updated == lastUpdated as string)
            {
                return null;
            }

            if (type == ForumItemType.Post)
            {
                return new ForumPostItem(
                    GetHtmlValue(htmlItem.OwnerDocument.DocumentNode, Configuration.GetSection("Posts:ForumName")),
                    id,
                    GetHtmlValue(htmlItem, Configuration.GetSection("Posts:Link")),
                    GetHtmlValue<int>(htmlItem, Configuration.GetSection("Posts:Index")),
                    GetHtmlValue(htmlItem, Configuration.GetSection("Posts:ReplyLink")),
                    GetHtmlValue<DateTimeOffset>(htmlItem, Configuration.GetSection("Posts:Date")),
                    GetHtmlValue(htmlItem, Configuration.GetSection("Posts:Author")),
                    GetHtmlValue<HtmlNode>(htmlItem, Configuration.GetSection("Posts:Body"))
                );
            }
            return new ForumItem(type, id, link, updated);
        }

        private async Task SetItemUpdated(ForumItem item)
        {
            await Storage.ExecuteNonQueryAsync($"INSERT OR REPLACE INTO {item.Type}s ({item.Type}Id, Updated) VALUES (@Param0, @Param1)", item.Id, item.Updated);
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
                    case "Constant":
                        return type.Value;
                    case "Attribute":
                        foreach (var attribute in type.GetChildren())
                        {
                            return WebUtility.HtmlDecode(node.SelectSingleNode(attribute.Value)?.Attributes?[attribute.Key]?.Value ?? $"<default:{configuration.Path}>");
                        }
                        goto default;
                    case "InnerHtml":
                        return node.SelectSingleNode(type.Value).InnerHtml;
                    case "InnerText":
                        return WhitespacePattern.Replace(WebUtility.HtmlDecode(node.SelectSingleNode(type.Value)?.InnerText ?? $"<default:{configuration.Path}>"), " ").Trim();
                    default:
                        throw new InvalidDataException($"Invalid value type for GetHtmlValue: {type.Path}");
                }
            }

            throw new InvalidDataException($"Missing value type for GetHtmlValue: {configuration.Path}");
        }
    }

    class ForumItem
    {
        public ForumItemType Type { get; }
        public int Id { get; }
        public string Link { get; }
        public string Updated { get; }

        public ForumItem(ForumItemType type, int id, string link, string updated)
        {
            Type = type;
            Id = id;
            Link = link;
            Updated = updated;
        }

        override public string ToString()
        {
            return $"{Type} {Id} {Link}";
        }
    }

    class ForumPostItem : ForumItem
    {
        public string ForumName { get; }
        public string ReplyLink { get; }
        public int Index { get; }
        public DateTimeOffset Date { get; }
        public string Author { get; }
        public HtmlNode Body { get; }

        public ForumPostItem(string forumName, int id, string link, int index, string replyLink, DateTimeOffset date, string author, HtmlNode body)
            : base(ForumItemType.Post, id, link, "")
        {
            ForumName = forumName;
            ReplyLink = replyLink;
            Index = index;
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
