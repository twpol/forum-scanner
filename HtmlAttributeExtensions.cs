using System.Net;
using HtmlAgilityPack;

namespace ForumScanner
{
    public static class HtmlAttributeExtensions
    {
        public static string DValue(this HtmlAttribute attribute)
        {
            return WebUtility.HtmlDecode(attribute.Value);
        }

        public static string DUValue(this HtmlAttribute attribute)
        {
            return attribute.DValue().ToUpperInvariant();
        }

        public static string DLValue(this HtmlAttribute attribute)
        {
            return attribute.DValue().ToLowerInvariant();
        }

    }
}
