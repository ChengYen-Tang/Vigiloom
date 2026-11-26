using System.Net.Http.Headers;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Microsoft.AspNetCore.WebUtilities;
using AngleSharp.Html.Parser;

namespace Vigiloom.Web.Tests;

[TestClass]
public sealed class LocalizationTests(TestContext context)
{
    [TestMethod]
    [DataRow("en", "Public status", "Search public names")]
    [DataRow("zh-Hant", "公開監測狀態", "搜尋公開名稱")]
    [DataRow("zh-Hans", "公开监测状态", "搜索公开名称")]
    public async Task PublicStatusPrerenderUsesSelectedLanguage(string culture, string title, string search)
    {
        using var factory = new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
        {
            builder.UseEnvironment("Development");
            builder.ConfigureAppConfiguration((_, config) => config.AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["ConnectionStrings:Vigiloom"] = "Server=localhost;Database=unused;Integrated Security=true;TrustServerCertificate=true"
            }));
        });
        using var client = factory.CreateClient(new WebApplicationFactoryClientOptions { BaseAddress = new Uri("https://localhost") });
        client.DefaultRequestHeaders.AcceptLanguage.ParseAdd(culture);
        using var response = await client.GetAsync("/", context.CancellationToken);
        response.EnsureSuccessStatusCode();
        var document = await new HtmlParser().ParseDocumentAsync(await response.Content.ReadAsStringAsync(context.CancellationToken), context.CancellationToken);
        Assert.Contains(title, document.Body!.TextContent);
        Assert.Contains(search, document.Body.TextContent);
        Assert.Contains(title, document.Title ?? "");
    }

    [TestMethod]
    [DataRow("en", "en")]
    [DataRow("zh-Hant", "zh-Hant")]
    [DataRow("zh-Hans", "zh-Hans")]
    [DataRow("fr", "zh-Hant")]
    public async Task LoginPageUsesSupportedRequestCulture(string requested, string expected)
    {
        using var factory = new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
        {
            builder.UseEnvironment("Development");
            builder.ConfigureAppConfiguration((_, config) => config.AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["ConnectionStrings:Vigiloom"] = "Server=localhost;Database=unused;Integrated Security=true;TrustServerCertificate=true"
            }));
        });
        using var client = factory.CreateClient(new WebApplicationFactoryClientOptions { BaseAddress = new Uri("https://localhost") });
        client.DefaultRequestHeaders.AcceptLanguage.Add(new StringWithQualityHeaderValue(requested));
        using var response = await client.GetAsync("/account/login", context.CancellationToken);
        response.EnsureSuccessStatusCode();
        var html = await response.Content.ReadAsStringAsync(context.CancellationToken);
        Assert.Contains($"<html lang=\"{expected}\">", html);
        Assert.Contains("autostart=\"false\"", html);
        Assert.Contains("culture-start", html);
        var text = (await new HtmlParser().ParseDocumentAsync(html, context.CancellationToken)).Body!.TextContent;
        if (expected == "en") { Assert.Contains("Language", text); Assert.Contains("Sign in to Vigiloom", text); }
        if (expected == "zh-Hans") { Assert.Contains("语言", text); Assert.Contains("登录 Vigiloom", text); }
    }

    [TestMethod]
    [DataRow("en", "Enter your username.", "Enter your password.")]
    [DataRow("zh-Hant", "請輸入帳號。", "請輸入密碼。")]
    [DataRow("zh-Hans", "请输入账号。", "请输入密码。")]
    public async Task LoginValidationUsesSelectedLanguage(string culture, string usernameMessage, string passwordMessage)
    {
        using var factory = new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
        {
            builder.UseEnvironment("Development");
            builder.ConfigureAppConfiguration((_, config) => config.AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["ConnectionStrings:Vigiloom"] = "Server=localhost;Database=unused;Integrated Security=true;TrustServerCertificate=true"
            }));
        });
        using var client = factory.CreateClient(new WebApplicationFactoryClientOptions { BaseAddress = new Uri("https://localhost"), AllowAutoRedirect = false });
        client.DefaultRequestHeaders.AcceptLanguage.Add(new StringWithQualityHeaderValue(culture));
        using var page = await client.GetAsync("/account/login", context.CancellationToken);
        var document = await new HtmlParser().ParseDocumentAsync(await page.Content.ReadAsStringAsync(context.CancellationToken), context.CancellationToken);
        var form = document.QuerySelector("form")!;
        var fields = form.QuerySelectorAll("input[type=hidden][name]").ToDictionary(element => element.GetAttribute("name")!, element => element.GetAttribute("value") ?? "");
        fields["Input.UserName"] = "";
        fields["Input.Password"] = "";
        using var result = await client.PostAsync("/account/login", new FormUrlEncodedContent(fields), context.CancellationToken);
        var body = (await new HtmlParser().ParseDocumentAsync(await result.Content.ReadAsStringAsync(context.CancellationToken), context.CancellationToken)).Body!.TextContent;
        Assert.Contains(usernameMessage, body);
        Assert.Contains(passwordMessage, body);
    }

    [TestMethod]
    public async Task LanguageChoicePersistsAndRejectsExternalRedirects()
    {
        using var factory = new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
        {
            builder.UseEnvironment("Development");
            builder.ConfigureAppConfiguration((_, config) => config.AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["ConnectionStrings:Vigiloom"] = "Server=localhost;Database=unused;Integrated Security=true;TrustServerCertificate=true"
            }));
        });
        using var client = factory.CreateClient(new WebApplicationFactoryClientOptions { BaseAddress = new Uri("https://localhost"), AllowAutoRedirect = false });
        using var choice = await client.GetAsync("/culture/set?culture=en&returnUrl=%2Faccount%2Flogin", context.CancellationToken);
        Assert.AreEqual("/account/login", choice.Headers.Location?.OriginalString);
        using var page = await client.GetAsync("/account/login", context.CancellationToken);
        Assert.Contains("<html lang=\"en\">", await page.Content.ReadAsStringAsync(context.CancellationToken));
        using var unsafeRedirect = await client.GetAsync("/culture/set?culture=zh-Hans&returnUrl=%2F%2Fevil.example", context.CancellationToken);
        Assert.AreEqual("/", unsafeRedirect.Headers.Location?.OriginalString);
        using var unsupported = await client.GetAsync("/culture/set?culture=fr", context.CancellationToken);
        Assert.AreEqual(System.Net.HttpStatusCode.BadRequest, unsupported.StatusCode);

        using var original = await client.GetAsync("/account/login?culture=zh-Hans&ui-culture=zh-Hans&ReturnUrl=%2Fworkspace", context.CancellationToken);
        var document = await new HtmlParser().ParseDocumentAsync(await original.Content.ReadAsStringAsync(context.CancellationToken), context.CancellationToken);
        var menu = document.QuerySelector("nav[data-enhance-nav='false']");
        Assert.IsNotNull(menu);
        var english = menu.QuerySelector("a[lang='en']")!.GetAttribute("href")!;
        var query = QueryHelpers.ParseQuery(new Uri(new Uri("https://localhost"), english).Query);
        Assert.AreEqual("/account/login?ReturnUrl=%2Fworkspace", query["returnUrl"].ToString());
    }

    [TestMethod]
    public async Task SetupPageUsesEnglishForFormAndValidation()
    {
        using var factory = new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
        {
            builder.UseEnvironment("Development");
            builder.ConfigureAppConfiguration((_, config) => config.AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["ConnectionStrings:Vigiloom"] = "Server=localhost;Database=unused;Integrated Security=true;TrustServerCertificate=true"
            }));
        });
        using var client = factory.CreateClient(new WebApplicationFactoryClientOptions { BaseAddress = new Uri("https://localhost"), AllowAutoRedirect = false });
        client.DefaultRequestHeaders.AcceptLanguage.ParseAdd("en");
        using var page = await client.GetAsync("/account/setup", context.CancellationToken);
        var document = await new HtmlParser().ParseDocumentAsync(await page.Content.ReadAsStringAsync(context.CancellationToken), context.CancellationToken);
        Assert.Contains("Create an administrator", document.Body!.TextContent);
        var fields = document.QuerySelector("form")!.QuerySelectorAll("input[type=hidden][name]")
            .ToDictionary(element => element.GetAttribute("name")!, element => element.GetAttribute("value") ?? "");
        fields["Input.SetupToken"] = "";
        fields["Input.UserName"] = "";
        fields["Input.Password"] = "password";
        fields["Input.ConfirmPassword"] = "different";
        using var response = await client.PostAsync("/account/setup", new FormUrlEncodedContent(fields), context.CancellationToken);
        var text = (await new HtmlParser().ParseDocumentAsync(await response.Content.ReadAsStringAsync(context.CancellationToken), context.CancellationToken)).Body!.TextContent;
        Assert.Contains("Enter the setup key.", text);
        Assert.Contains("Enter your username.", text);
        Assert.Contains("Passwords do not match.", text);
    }

    [TestMethod]
    [DataRow("en", "Enter the one-time code.", "Passwords do not match.")]
    [DataRow("zh-Hant", "請輸入一次性代碼。", "兩次密碼不一致。")]
    [DataRow("zh-Hans", "请输入一次性代码。", "两次密码不一致。")]
    public async Task ResetPasswordValidationUsesSelectedLanguage(string culture, string codeMessage, string mismatchMessage)
    {
        using var factory = new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
        {
            builder.UseEnvironment("Development");
            builder.ConfigureAppConfiguration((_, config) => config.AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["ConnectionStrings:Vigiloom"] = "Server=localhost;Database=unused;Integrated Security=true;TrustServerCertificate=true"
            }));
        });
        using var client = factory.CreateClient(new WebApplicationFactoryClientOptions { BaseAddress = new Uri("https://localhost"), AllowAutoRedirect = false });
        client.DefaultRequestHeaders.AcceptLanguage.ParseAdd(culture);
        using var page = await client.GetAsync("/account/reset-password", context.CancellationToken);
        var document = await new HtmlParser().ParseDocumentAsync(await page.Content.ReadAsStringAsync(context.CancellationToken), context.CancellationToken);
        var fields = document.QuerySelector("form")!.QuerySelectorAll("input[type=hidden][name]")
            .ToDictionary(element => element.GetAttribute("name")!, element => element.GetAttribute("value") ?? "");
        fields["Input.UserName"] = "example";
        fields["Input.Code"] = "";
        fields["Input.Password"] = "password";
        fields["Input.ConfirmPassword"] = "different";
        using var response = await client.PostAsync("/account/reset-password", new FormUrlEncodedContent(fields), context.CancellationToken);
        var text = (await new HtmlParser().ParseDocumentAsync(await response.Content.ReadAsStringAsync(context.CancellationToken), context.CancellationToken)).Body!.TextContent;
        Assert.Contains(codeMessage, text);
        Assert.Contains(mismatchMessage, text);
    }
}
