using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Net.Http;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using System.Xml;
using ICSharpCode.SharpZipLib.GZip;
using Newtonsoft.Json.Linq;
using NuGet.Versioning;
using Nuke.Common;
using Nuke.Common.Execution;
using Nuke.Common.IO;
using Nuke.Common.ProjectModel;
using Nuke.Common.Tooling;
using Nuke.Common.Tools.DotNet;
using Nuke.Common.Tools.NuGet;
using Nuke.Common.Utilities.Collections;
using Octokit;
using static Nuke.Common.EnvironmentInfo;
using static Nuke.Common.IO.FileSystemTasks;
using static Nuke.Common.IO.PathConstruction;
using static Nuke.Common.IO.HttpTasks;
using FileMode = System.IO.FileMode;

[UnsetVisualStudioEnvironmentVariables]
class Build : NukeBuild
{
    /// Support plugins are available for:
    ///   - JetBrains ReSharper        https://nuke.build/resharper
    ///   - JetBrains Rider            https://nuke.build/rider
    ///   - Microsoft VisualStudio     https://nuke.build/visualstudio
    ///   - Microsoft VSCode           https://nuke.build/vscode

    public static int Main () => Execute<Build>(x => x.PackLatest);

    [Parameter("Configuration to build - Default is 'Debug' (local) or 'Release' (server)")]
    readonly Configuration Configuration = IsLocalBuild ? Configuration.Debug : Configuration.Release;
    [Parameter] readonly string Source = "https://api.nuget.org/v3/index.json";
    [Parameter("ApiKey for the specified source")] readonly string NugetApiKey;

    AbsolutePath ArtifactsDirectory => RootDirectory / "artifacts";

    [Parameter] string Version;

    Target FindLatestVersion => _ => _
        .OnlyWhenStatic(() => Version == null)
        .Executes(async () =>
        {
            Version = (await GetLatestCliReleaseVersion()).ToString();
        });
    Target PackLatest => _ => _
        .DependsOn(FindLatestVersion)
        .Executes(async () =>
        {
            SwitchWorkingDirectory(TemporaryDirectory / "cli");
            EnsureCleanDirectory(WorkingDirectory);
            EnsureCleanDirectory(ArtifactsDirectory);
            var workingDirectory = (AbsolutePath) WorkingDirectory;
            
            Directory.CreateDirectory(RootDirectory / "artifacts");
//            try
//            {
//                var nugetPackageInfo = JObject.Parse(HttpDownloadString("https://api-v2v3search-0.nuget.org/query?q=id:CloudFoundry.Buildpack.V2"));
//                var latestNugetVersion = SemanticVersion.Parse(nugetPackageInfo.SelectToken("data[0].version").ToString());
//                if (latestCliVersion <= latestNugetVersion) // nuget is up to date with official package
//                    return;
//            }
//            catch(HttpRequestException e) when (e.Message.Contains("404")) 
//            {
//            }
//            
            
            
            DeleteFile((AbsolutePath)WorkingDirectory / "cf.exe");

            var ridToReleaseArchitecture = new Dictionary<string, string>
            {
                {"win-x64", "windows64-exe"},
                {"win-x32", "windows32-exe"}, 
                {"linux-x64", "linux64-binary"},
                {"linux-x32", "linux32-binary"},
                {"osx-x64", "macosx64-binary"}
            };


            foreach (var (rid,releaseName) in ridToReleaseArchitecture)
            {
                var nuspecName = $"CloudFoundry.CommandLine.{rid}.nuspec";
                CopyFile(RootDirectory / nuspecName, workingDirectory / nuspecName, FileExistsPolicy.Overwrite);
                var ext = rid.StartsWith("win") ? "zip" : "tgz";
                var archive = TemporaryDirectory / $"cf-{rid}-{Version}.{ext}";
                
                var binaryLocation = workingDirectory / rid / (ext == "zip" ? "cf.exe" : "cf");
                var ridDir = workingDirectory / rid;
                if (!FileExists(archive))
                {
                    await HttpTasks.HttpDownloadFileAsync($"https://packages.cloudfoundry.org/stable?release={releaseName}&version={Version}", archive);
                }
                Directory.CreateDirectory(ridDir);
                if (ext == "zip")
                {
                    ZipFile.ExtractToDirectory(archive, ridDir);
                }
                else
                {
                    CompressionTasks.UncompressTarGZip(archive, ridDir);
                    if (!FileExists(binaryLocation))
                    {
                        RenameFile(Directory.GetFiles(ridDir).First(x => Regex.IsMatch(x,"cf[0-9]+")), "cf");
                    }
                    
                    // GZip.Decompress(File.Open(archive, FileMode.Open), File.OpenWrite(binaryLocation),true);
                }
//                var doc = new XmlDocument();
//                doc.Load(RootDirectory / "CloudFoundry.CommandLine.nuspec");
//                var ns = new XmlNamespaceManager(doc.NameTable);
//                ns.AddNamespace("d", doc.DocumentElement.NamespaceURI);
//                var files = doc.SelectSingleNode("/d:package/d:files", ns);
//                var cf = doc.CreateElement("file", doc.DocumentElement.NamespaceURI);
//                var binaryExt = ext == "zip" ? ".exe" : string.Empty;
//                cf.SetAttribute("src",$"{rid}/cf{binaryExt}");
//                cf.SetAttribute("target",$@"tools\cf{binaryExt}");
//                files.AppendChild(cf);
//                var nuspec = workingDirectory / $"CloudFoundry.CommandLine.{rid}.nuspec";
//                doc.Save(nuspec);
                
                NuGetTasks.NuGetPack(opt => opt
                    .SetTargetPath(workingDirectory / nuspecName)
                    .SetVersion(Version)
                    .SetWorkingDirectory(ArtifactsDirectory));
                
                
            }
            
        });

    Target NugetPushLatest => _ => _
        .DependsOn(PackLatest, FindLatestVersion)
        .OnlyWhenDynamic(() => !IsPublished().Result)
        .Executes(() =>
        {
            NuGetTasks.NuGetPush(s => s
                    .SetSource(Source)
                    .SetApiKey(NugetApiKey)
                    .CombineWith(
                        ArtifactsDirectory.GlobFiles("*.nupkg").NotEmpty(), (cs, v) => cs
                            .SetTargetPath(v)),
                degreeOfParallelism: 5,
                completeOnFailure: true);
        });
    
    async Task<bool> IsPublished()
    {
        var client = new HttpClient();
        var json = JObject.Parse(await client.GetStringAsync("https://api.nuget.org/v3/index.json"));
        var queryEndpoint = json.SelectTokens($"..resources[?(@.@type == 'RegistrationsBaseUrl')].@id").First().Value<string>();
        json = JObject.Parse(await client.GetStringAsync(queryEndpoint + "CloudFoundry.CommandLine.linux-x64".ToLower() + "/index.json"));
        var versions = json.SelectTokens($"..version").Select(x => x.Value<string>()).ToList();
        var isPublished = versions.Contains(Version);
        return isPublished;
    }

    struct CfReleaseInfo
    {
        public string Rid { get; set; }
        public string ReleaseName { get; set; }
        public string ArchiveName { get; set; }
    }
    static async Task<SemanticVersion> GetLatestCliReleaseVersion()
    {
        var client = new GitHubClient(new ProductHeaderValue("Nuke"));
        var latestGithubRelease = await client.Repository.Release.GetLatest("cloudfoundry", "cli");
        var latestCliVersion = SemanticVersion.Parse(latestGithubRelease.Name.TrimStart('v'));
        return latestCliVersion;
    }
    

}
