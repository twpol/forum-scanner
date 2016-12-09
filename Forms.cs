using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Threading.Tasks;
using HtmlAgilityPack;
using Microsoft.Extensions.Configuration;

namespace ForumScanner
{
    public static class Forms
    {
        public static async Task LoadAndSubmit(IConfigurationSection configuration, HttpClient client)
        {
            var url = configuration["url"];
            var response = await client.GetAsync(url);
            Console.WriteLine(response);

            var document = new HtmlDocument();
            document.Load(await response.Content.ReadAsStreamAsync());

            foreach (var parseError in document.ParseErrors)
            {
                Console.WriteLine($"ParseError: {parseError.Code} l={parseError.Line} lp={parseError.LinePosition} sp={parseError.StreamPosition} reason={parseError.Reason}");
            }

            var form = document.DocumentNode.SelectSingleNode(configuration["form"]);
            var formAction = form.Attributes["action"]?.DValue() ?? configuration["url"];
            var formMethod = form.Attributes["method"]?.DUValue() ?? "GET";
            var formEncType = form.Attributes["enctype"]?.DLValue() ?? "application/x-www-form-urlencoded";

            var formData = GetFormData(configuration, form);
            var formBody = EncodeFormData(formMethod, formEncType, formData);

            Console.WriteLine($"URL:     {url}");
            Console.WriteLine($"Action:  {formAction}");
            Console.WriteLine($"Method:  {formMethod}");
            Console.WriteLine($"EncType: {formEncType}");
            foreach (var kvp in formData)
            {
                Console.WriteLine($"Data:    {kvp.Key}={kvp.Value}");
            }
            Console.WriteLine($"Body:    {formBody}");

            switch (formMethod)
            {
                case "GET":
                    formAction += (formAction.Contains("?") ? "&" : "?") + formBody;
                    Console.WriteLine(await client.GetAsync(formAction));
                    break;
                case "POST":
                    // Do nothing.
                    Console.WriteLine(await client.PostAsync(formAction, new StringContent(formBody, Encoding.UTF8, formEncType)));
                    break;
                default:
                    Debug.Fail($"Unsupported form method: {formMethod}");
                    break;
            }
        }

        private static Dictionary<string, string> GetFormData(IConfigurationSection configuration, HtmlNode form)
        {
            var configurationInput = configuration.GetSection("input");
            var formData = new Dictionary<string, string>();

            foreach (var input in form.SelectNodes(".//input[@name] | .//select[@name] | .//textarea[@name]"))
            {
                var name = input.Attributes["name"].DValue();
                var type = input.Attributes["type"]?.DValue();
                var value = input.Attributes["value"]?.DValue();
                if (type == "checkbox" || type == "radio")
                {
                    value = input.Attributes["checked"] != null ? value ?? "on" : null;
                }
                formData[name] = configurationInput[name] ?? value;
            }

            var submit = form.SelectSingleNode(configuration["submit"]);
            if (submit.Attributes["name"] != null && submit.Attributes["value"] != null)
            {
                formData[submit.Attributes["name"].DValue()] = submit.Attributes["value"].DValue();
            }

            return formData;
        }

        private static string EncodeFormData(string formMethod, string formEncType, Dictionary<string, string> formData)
        {
            var formBody = new StringBuilder();

            switch (formEncType)
            {
                case "application/x-www-form-urlencoded":
                    foreach (var kvp in formData)
                    {
                        if (formBody.Length > 0)
                        {
                            formBody.Append("&");
                        }
                        formBody.Append(WebUtility.UrlEncode(kvp.Key));
                        formBody.Append("=");
                        formBody.Append(WebUtility.UrlEncode(kvp.Value));
                    }
                    break;
                // case "multipart/form-data":
                //     break;
                default:
                    Debug.Fail($"Unsupported form enctype for {formMethod}: {formEncType}");
                    break;
            }

            return formBody.ToString();
        }
    }
}
