// See https://aka.ms/new-console-template for more information

using System.Net;
using System.Text;
using System.Text.Json.Nodes;

using NuGet.Common;
using NuGet.Packaging;
using NuGet.Packaging.Core;
using NuGet.Protocol;
using NuGet.Protocol.Core.Types;
using NuGet.Versioning;

using Octokit;

using Zx;

using LicenseMetadata = NuGet.Packaging.LicenseMetadata;
using Repository = NuGet.Protocol.Core.Types.Repository;

var jsonString = await "dotnet list package --include-transitive --format json";
var json = JsonNode.Parse(jsonString);
var projects = json!["projects"];
var packageIds = new HashSet<string>();

foreach (var project in projects!.AsArray())
{
    foreach (var fx in project!["frameworks"]!.AsArray())
    {
        var topLevelPackages = fx!["topLevelPackages"]!.AsArray();
        var transitivePackages = fx!["transitivePackages"]!.AsArray();
        foreach (var package in topLevelPackages)
        {
            packageIds.Add(package!["id"]!.AsValue().GetValue<string>());
        }

        foreach (var package in transitivePackages)
        {
            packageIds.Add(package!["id"]!.AsValue().GetValue<string>());
        }
    }
}

ILogger logger = NullLogger.Instance;
CancellationToken cancellationToken = CancellationToken.None;

var cache = new SourceCacheContext();
SourceRepository repository = Repository.Factory.GetCoreV3("https://api.nuget.org/v3/index.json");
FindPackageByIdResource resource = await repository.GetResourceAsync<FindPackageByIdResource>();

var list = new List<(string Id, string Url, LicenseMetadata License, RepositoryMetadata Repository)>();
foreach (string id in packageIds)
{
    NuGetVersion version = (await resource.GetAllVersionsAsync(
            id, cache, logger, cancellationToken))
        .First();

    using var stream = new MemoryStream();
    if (!await resource.CopyNupkgToStreamAsync(id, version, stream, cache, logger, cancellationToken))
        continue;

    stream.Position = 0;
    using var packageReader = new PackageArchiveReader(stream);
    var nuspec = packageReader.NuspecReader;
    var licenseMetadata = nuspec.GetLicenseMetadata();
    var projectUrl = nuspec.GetProjectUrl();
    var repositoryMetadata = nuspec.GetRepositoryMetadata();
    if (projectUrl.StartsWith("https://dot.net") && string.IsNullOrEmpty(repositoryMetadata.Url))
        projectUrl = "https://github.com/dotnet/runtime";

    list.Add((id, projectUrl, licenseMetadata, repositoryMetadata));
}

using var httpClient = new HttpClient();
var github = new GitHubClient(new ProductHeaderValue("ThirdPartyNoticesGenerator"));
var sb = new StringBuilder();
foreach (var item in list.GroupBy(i => i.Url))
{
    if (!string.IsNullOrEmpty(item.Key))
    {
        var typical = item.Aggregate((x, y)
            => x.Id.Count(z => z == '.') < y.Id.Count(z => z == '.') ? x : y);

        await AppendLicenseInfo(typical.Id, typical.Url, typical.License, typical.Repository);
    }
    else
    {
        foreach (var inner in item)
        {
            await AppendLicenseInfo(inner.Id, inner.Url, inner.License, inner.Repository);
        }
    }
}

Console.WriteLine(sb.ToString());

async Task AppendLicenseInfo(string id, string url, LicenseMetadata license, RepositoryMetadata repositoryMetadata)
{
    var reposUrl = await GetRepositoryUrl(httpClient, url, repositoryMetadata.Url);
    if (url.StartsWith("https://github.com/dotnet/runtime"))
    {
        id = ".NET Runtime";
    }

    string prefix = "https://github.com/";
    if (reposUrl.StartsWith(prefix))
    {
        var repos = reposUrl.Substring(prefix.Length).Split(['/', '?']);
        var owner = repos[0];
        var repo = repos[1];

        var licenseContents = await github.Repository.GetLicenseContents(owner, repo);
        var body = await httpClient.GetStringAsync(licenseContents.DownloadUrl);
        sb.Append($"## [{id}]({url})\n\n```\n{body}\n```\n\n");
    }
    else
    {
        if (string.IsNullOrEmpty(url))
        {
            sb.Append($"## {id}\n\n");
        }
        else
        {
            sb.Append($"## [{id}]({url})\n\n");
        }

        if (!string.IsNullOrEmpty(license?.License))
        {
            sb.Append($"{license?.License}\n\n");
        }
    }
}

static async Task<string> GetRepositoryUrl(HttpClient httpClient, string str1, string str2)
{
    try
    {
        if (!string.IsNullOrEmpty(str1))
            str1 = await GetFinalRedirectUrlAsync(httpClient, str1);
    }
    catch
    {
        str1 = "";
    }

    try
    {
        if (!string.IsNullOrEmpty(str2))
            str2 = await GetFinalRedirectUrlAsync(httpClient, str2);
    }
    catch
    {
        str2 = "";
    }

    return string.IsNullOrEmpty(str2) ? str1 : str2;
}

static async Task<string> GetFinalRedirectUrlAsync(HttpClient httpClient, string url)
{
    var request = new HttpRequestMessage(HttpMethod.Head, url);
    var response = await httpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead);
    if (response.StatusCode != HttpStatusCode.Redirect)
    {
        return url;
    }

    return response.RequestMessage!.RequestUri!.ToString();
}