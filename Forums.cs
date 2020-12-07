using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using HtmlAgilityPack;
using MailKit;
using MailKit.Net.Smtp;
using Microsoft.Extensions.Configuration;
using MimeKit;

namespace ForumScanner
{
    public class Forums
    {
        IConfigurationSection Configuration { get; }
        Storage Storage { get; }
        HttpClient Client { get; }
        bool Debug { get; }
        int MaxEmailErrors = int.MaxValue;

        Dictionary<ForumItemType, Regex> IdUrlPattern { get; }
        Dictionary<ForumItemType, Regex> IdIdPattern { get; }

        int EmailsSent = 0;

        static readonly Regex WhitespacePattern = new Regex(@"\s+");
        static readonly Regex GetUrlDomainName = new Regex(@"^\w+://([^/]+)/.*");
        static readonly Regex UnsafeCharacters = new Regex("[^a-z0-9]+");

        public Forums(IConfigurationSection configuration, Storage storage, HttpClient client, bool debug)
        {
            Configuration = configuration;
            Storage = storage;
            Client = client;
            Debug = debug;
            int.TryParse(Configuration["Email:MaxErrors"], out MaxEmailErrors);

            IdUrlPattern = new Dictionary<ForumItemType, Regex>() {
                { ForumItemType.Forum, Configuration["Forums:IdUrlPattern"] != null ? new Regex(Configuration["Forums:IdUrlPattern"]) : null },
                { ForumItemType.Topic, Configuration["Topics:IdUrlPattern"] != null ? new Regex(Configuration["Topics:IdUrlPattern"]) : null },
                { ForumItemType.Post, Configuration["Posts:IdUrlPattern"] != null ? new Regex(Configuration["Posts:IdUrlPattern"]) : null },
            };
            IdIdPattern = new Dictionary<ForumItemType, Regex>() {
                { ForumItemType.Forum, Configuration["Forums:IdIdPattern"] != null ? new Regex(Configuration["Forums:IdIdPattern"]) : null },
                { ForumItemType.Topic, Configuration["Topics:IdIdPattern"] != null ? new Regex(Configuration["Topics:IdIdPattern"]) : null },
                { ForumItemType.Post, Configuration["Posts:IdIdPattern"] != null ? new Regex(Configuration["Posts:IdIdPattern"]) : null },
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

            await ProcessForum(new ForumItem(ForumItemType.Forum, Configuration.Key, Configuration["RootUrl"], ""));

            if (Debug)
            {
                Console.WriteLine($"Sending debug email...");

                var rootDomainName = GetUrlDomainName.Replace(Configuration["RootUrl"], "$1");

                var message = new MimeMessage();
                message.MessageId = $"test/{DateTimeOffset.Now.ToString("yyyy-MM-dd/HH-mm-ss")}@{rootDomainName}";
                message.Date = DateTimeOffset.Now;
                message.From.Add(GetMailboxAddress(Configuration.GetSection("Email:From"), "debug-from"));
                message.To.Add(GetMailboxAddress(Configuration.GetSection("Email:To")));
                message.Subject = "Forum Scanner Debug Subject";
                message.Body = new TextPart("html")
                {
                    Text = "<!DOCTYPE html>" +
                    "<html>" +
                        "<body>" +
                            "<h1>Forum Scanner Debug Body" +
                        "</body>" +
                    "</html>",
                };

                await SendEmail(message);
            }
        }

        async Task ProcessForum(ForumItem forum)
        {
            if (forum == null)
            {
                return;
            }

            var forumIndex = 0;
            var topicIndex = 0;
            while (true)
            {
                Console.WriteLine($"  Processing {forum}...");
                var document = await LoadItem(forum);

                var forumItems = document.DocumentNode.SelectNodes(Configuration["Forums:Item"] ?? ".//fake_no_match");
                if (forumItems != null)
                {
                    foreach (var forumItem in forumItems)
                    {
                        forumIndex++;
                        forumItem.SetAttributeValue("__forum_scanner_forum_index__", forumIndex.ToString());
                        await ProcessForum(await CheckItemIsUpdated(ForumItemType.Forum, forumItem));
                    }
                }

                var topicItems = document.DocumentNode.SelectNodes(Configuration["Topics:Item"]);
                if (topicItems != null)
                {
                    foreach (var topicItem in topicItems)
                    {
                        topicIndex++;
                        topicItem.SetAttributeValue("__forum_scanner_topic_index__", topicIndex.ToString());
                        await ProcessTopic(forum, await CheckItemIsUpdated(ForumItemType.Topic, topicItem));
                    }
                }

                var nextLink = GetHtmlValue(document.DocumentNode, Configuration.GetSection("Forums:Next"));
                if (nextLink.StartsWith("<default:"))
                {
                    break;
                }
                forum = new ForumItem(forum.Type, forum.Id, nextLink, forum.Updated);
            }

            await SetItemUpdated(forum);
        }

        async Task ProcessTopic(ForumItem forum, ForumItem topic)
        {
            if (topic == null)
            {
                return;
            }

            var postIndex = 0;
            while (true)
            {
                Console.WriteLine($"    Processing {topic}...");
                var document = await LoadItem(topic);

                var postItems = document.DocumentNode.SelectNodes(Configuration["Posts:Item"]);
                if (postItems != null)
                {
                    foreach (var postItem in postItems)
                    {
                        postIndex++;
                        postItem.SetAttributeValue("__forum_scanner_post_index__", postIndex.ToString());
                        await ProcessPost(forum, topic, await CheckItemIsUpdated(ForumItemType.Post, postItem));
                    }
                }

                var nextLink = GetHtmlValue(document.DocumentNode, Configuration.GetSection("Topics:Next"));
                if (nextLink.StartsWith("<default:"))
                {
                    break;
                }
                topic = new ForumItem(topic.Type, topic.Id, nextLink, topic.Updated);
            }

            await SetItemUpdated(topic);

            if (int.TryParse(Configuration["Email:MaxPerRun"], out var maxPerRun) && EmailsSent >= maxPerRun)
            {
                throw new MaximumEmailLimitException();
            }
        }

        async Task ProcessPost(ForumItem forum, ForumItem topic, ForumItem item)
        {
            if (item == null || !(item is ForumPostItem))
            {
                return;
            }
            var post = item as ForumPostItem;

            Console.WriteLine($"      Processing {post}...");

            if (Configuration["Email:To:Email"] != null)
            {
                var rootDomainName = GetUrlDomainName.Replace(Configuration["RootUrl"], "$1");

                var message = new MimeMessage();
                message.MessageId = $"{topic.Id}/{post.Index}@{rootDomainName}";
                if (post.Index >= 2)
                {
                    message.InReplyTo = $"{topic.Id}/{post.Index - 1}@{rootDomainName}";
                }
                message.Headers["X-ForumScanner-ForumId"] = forum.Id;
                message.Headers["X-ForumScanner-ForumName"] = post.ForumName;
                message.Headers["X-ForumScanner-TopicId"] = topic.Id;
                message.Headers["X-ForumScanner-TopicName"] = post.TopicName;
                message.Headers["X-ForumScanner-PostId"] = post.Id;
                message.Headers["X-ForumScanner-PostIndex"] = post.Index.ToString();
                message.Date = post.Date;
                message.From.Add(GetMailboxAddress(Configuration.GetSection("Email:From"), post.Author));
                message.To.Add(GetMailboxAddress(Configuration.GetSection("Email:To")));
                message.Subject = post.Index == 1 ? post.TopicName : $"Re: {post.TopicName}";
                message.Body = new TextPart("html")
                {
                    Text = GetEmailBody(post)
                };

                var source = $"{post.ForumName} > {post.TopicName} > #{post.Index} ({post.Id}) at {post.Date.ToString("T")} on {post.Date.ToString("D")} by {post.Author}";
                Console.WriteLine($"      Email: {source}");

                var errors = await Storage.ExecuteScalarAsync("SELECT COUNT(*) FROM Errors WHERE Source = @Param0", source) as long?;
                if (errors.Value >= MaxEmailErrors) return;

                try
                {
                    EmailsSent++;
                    if (!Debug) await SendEmail(message);
                }
                catch (Exception error)
                {
                    await Storage.ExecuteNonQueryAsync("INSERT INTO Errors (Source, Date, Error) VALUES (@Param0, @Param1, @Param2)", source, DateTimeOffset.Now, error.ToString());
                    // Do not set the item as updated if we've had an error!
                    return;
                }
            }

            await SetItemUpdated(post);
        }

        public async Task SendEmail(MimeMessage message)
        {
            var logger = Debug ? new ProtocolLogger(Console.OpenStandardOutput()) : (IProtocolLogger)new NullProtocolLogger();
            using (var smtp = new SmtpClient(logger))
            {
                await smtp.ConnectAsync(Configuration["Email:SmtpServer"], int.Parse(Configuration["Email:SmtpPort"] ?? "587"));
                if (Configuration["Email:SmtpUsername"] != null)
                {
                    await smtp.AuthenticateAsync(Configuration["Email:SmtpUsername"], Configuration["Email:SmtpPassword"]);
                }
                await smtp.SendAsync(message);
                await smtp.DisconnectAsync(true);
            }
        }

        const float SecondsToMilliseconds = 1000;
        const float MaxBandwidthBytesPerSec = 100000 / 8;

        async Task<HtmlDocument> LoadItem(ForumItem item)
        {
            var response = await Client.GetAsync(item.Link);

            // Limit maximum throughput to 100 Kbps by delaying based on content length.
            var wait = (int)(SecondsToMilliseconds * response.Content.Headers.ContentLength.Value / MaxBandwidthBytesPerSec);
            await Task.Delay(wait);

            var document = new HtmlDocument();
            document.Load(await response.Content.ReadAsStreamAsync());
            return document;
        }

        async Task<ForumItem> CheckItemIsUpdated(ForumItemType type, HtmlNode htmlItem)
        {
            var link = GetHtmlValue(htmlItem, Configuration.GetSection($"{type}s:Link"));
            var updated = GetHtmlValue(htmlItem, Configuration.GetSection($"{type}s:Updated"));
            var idUrlPattern = IdUrlPattern[type];
            var idIdPattern = IdIdPattern[type];
            var idString = (idUrlPattern != null ? idUrlPattern.Match(link) : idIdPattern.Match(htmlItem.Attributes["id"].Value))?.Groups?[1]?.Value;
            if (idString == null) throw new InvalidDataException($"Cannot find ID for {type}");
            if (!int.TryParse(idString, out var id)) throw new InvalidDataException($"Cannot parse ID for {type}");
            var uniqueId = $"{Configuration.Key}/{id}";

            var lastUpdated = await Storage.ExecuteScalarAsync($"SELECT Updated FROM {type}s WHERE {type}Id = @Param0", uniqueId) as string;
            if (updated == lastUpdated) return null;

            switch (type)
            {
                case ForumItemType.Forum:
                case ForumItemType.Topic:
                    return new ForumItem(type, uniqueId, link, updated);
                case ForumItemType.Post:
                    return new ForumPostItem(
                        GetHtmlValue(htmlItem.OwnerDocument.DocumentNode, Configuration.GetSection("Posts:ForumName")),
                        GetHtmlValue(htmlItem.OwnerDocument.DocumentNode, Configuration.GetSection("Posts:TopicName")),
                        uniqueId,
                        GetHtmlValue(htmlItem, Configuration.GetSection("Posts:Link")),
                        GetHtmlValue<int>(htmlItem, Configuration.GetSection("Posts:Index")),
                        GetHtmlValue(htmlItem, Configuration.GetSection("Posts:ReplyLink")),
                        GetHtmlValue<DateTimeOffset>(htmlItem, Configuration.GetSection("Posts:Date")),
                        GetHtmlValue(htmlItem, Configuration.GetSection("Posts:Author")),
                        GetHtmlValue<HtmlNode>(htmlItem, Configuration.GetSection("Posts:Body"))
                    );
                default:
                    throw new InvalidDataException($"Unknown type {type} in CheckItemIsUpdated");
            }
        }

        async Task SetItemUpdated(ForumItem item)
        {
            await Storage.ExecuteNonQueryAsync($"INSERT OR REPLACE INTO {item.Type}s ({item.Type}Id, Updated) VALUES (@Param0, @Param1)", item.Id, item.Updated);
        }

        static MailboxAddress GetMailboxAddress(IConfigurationSection configuration, string name = null)
        {
            return new MailboxAddress(configuration["Name"].Replace("$name$", name ?? ""), configuration["Email"]);
        }

        static string GetEmailBody(ForumPostItem post)
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

        static string FormatBodyForEmail(HtmlNode body)
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

        static T GetHtmlValue<T>(HtmlNode node, IConfigurationSection configuration)
        {
            var value = GetHtmlValue(node, configuration);
            var format = configuration["Format"];
            switch (typeof(T).FullName)
            {
                case "HtmlAgilityPack.HtmlNode":
                    var document = new HtmlDocument();
                    document.LoadHtml(value);
                    return (T)(object)document.DocumentNode;
                case "System.DateTimeOffset":
                    if (format != null) return (T)(object)DateTimeOffset.ParseExact(value, format, null);
                    return (T)(object)DateTimeOffset.Parse(value);
                case "System.Int32":
                    return (T)(object)int.Parse(value.Replace("#", ""));
                case "System.String":
                    return (T)(object)value;
                default:
                    throw new InvalidDataException($"Invalid type {typeof(T).FullName} for GetHtmlValue<T>");
            }
        }

        static string GetHtmlValue(HtmlNode node, IConfigurationSection configuration)
        {
            foreach (var type in configuration.GetChildren())
            {
                switch (type.Key)
                {
                    case "Format":
                        // Ignored - see GetHtmlValue<T>
                        break;
                    case "Constant":
                        return type.Value;
                    case "Attribute":
                        foreach (var attribute in type.GetChildren())
                        {
                            return WebUtility.HtmlDecode(node.SelectSingleNode(attribute.Value)?.Attributes?[attribute.Key]?.Value ?? $"<default:{attribute.Path}>");
                        }
                        goto default;
                    case "InnerHtml":
                        return node.SelectSingleNode(type.Value).InnerHtml;
                    case "InnerText":
                        return WhitespacePattern.Replace(WebUtility.HtmlDecode(node.SelectSingleNode(type.Value)?.InnerText ?? $"<default:{type.Path}>"), " ").Trim();
                    default:
                        throw new InvalidDataException($"Invalid value type for GetHtmlValue: {type.Path}");
                }
            }

            throw new InvalidDataException($"Missing value type for GetHtmlValue: {configuration.Path}");
        }
    }

    public class MaximumEmailLimitException : Exception
    {
        public MaximumEmailLimitException()
            : base("Maximum number of emails to send reached")
        {
        }
    }

    class ForumItem
    {
        public ForumItemType Type { get; }
        public string Id { get; }
        public string Link { get; }
        public string Updated { get; }

        public ForumItem(ForumItemType type, string id, string link, string updated)
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
        public string TopicName { get; }
        public string ReplyLink { get; }
        public int Index { get; }
        public DateTimeOffset Date { get; }
        public string Author { get; }
        public HtmlNode Body { get; }

        public ForumPostItem(string forumName, string topicName, string id, string link, int index, string replyLink, DateTimeOffset date, string author, HtmlNode body)
            : base(ForumItemType.Post, id, link, "")
        {
            ForumName = forumName;
            TopicName = topicName;
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
