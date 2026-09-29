using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Text.Json;

namespace Rafael.Portfolio.IntegrationTests;

public sealed class PublishedBackendSmokeTests
{
    [Fact]
    public async Task Published_backend_serves_portfolio_endpoints_outside_the_checkout()
    {
        var root = FindRepositoryRoot();
        var publishDir = Path.Combine(Path.GetTempPath(), $"portfolio-publish-{Guid.NewGuid():N}");
        Process? host = null;

        try
        {
            var webProject = Path.Combine(
                root, "backend", "src", "Hosts", "Web", "Rafael.Portfolio.Web", "Rafael.Portfolio.Web.csproj");
            Run("dotnet", $"publish \"{webProject}\" -c Release -o \"{publishDir}\"");

            var bundledManifest = Path.Combine(publishDir, "evidence", "inventory.json");
            Assert.True(File.Exists(bundledManifest), $"Published artifact is missing {bundledManifest}");

            var port = GetFreePort();
            host = Process.Start(new ProcessStartInfo
            {
                FileName = "dotnet",
                Arguments = $"\"{Path.Combine(publishDir, "Rafael.Portfolio.Web.dll")}\"",
                WorkingDirectory = publishDir,
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                Environment = { ["ASPNETCORE_URLS"] = $"http://127.0.0.1:{port}" }
            }) ?? throw new InvalidOperationException("Failed to start the published backend.");

            using var http = new HttpClient { BaseAddress = new Uri($"http://127.0.0.1:{port}") };
            await WaitForHealthAsync(http);

            using var projects = await http.GetAsync(new Uri("/v1/projects", UriKind.Relative));
            Assert.Equal(HttpStatusCode.OK, projects.StatusCode);
            using var projectsJson = JsonDocument.Parse(await projects.Content.ReadAsStringAsync());
            var summaries = projectsJson.RootElement.EnumerateArray().ToArray();
            Assert.Equal(3, summaries.Length);
            Assert.All(summaries, summary =>
                Assert.Equal("pending", summary.GetProperty("evidenceStatus").GetString()));

            using var detail = await http.GetAsync(new Uri("/v1/projects/vextis", UriKind.Relative));
            Assert.Equal(HttpStatusCode.OK, detail.StatusCode);
            using var detailJson = JsonDocument.Parse(await detail.Content.ReadAsStringAsync());
            Assert.Equal("vextis", detailJson.RootElement.GetProperty("slug").GetString());
            Assert.Equal("pending", detailJson.RootElement.GetProperty("evidenceStatus").GetString());

            using var unknown = await http.GetAsync(new Uri("/v1/projects/unknown-project", UriKind.Relative));
            Assert.Equal(HttpStatusCode.NotFound, unknown.StatusCode);

            using var profile = await http.GetAsync(new Uri("/v1/profile", UriKind.Relative));
            Assert.Equal(HttpStatusCode.OK, profile.StatusCode);
            using var profileJson = JsonDocument.Parse(await profile.Content.ReadAsStringAsync());
            Assert.Equal("pending", profileJson.RootElement.GetProperty("evidenceStatus").GetString());
        }
        finally
        {
            if (host is not null)
            {
                host.Kill(entireProcessTree: true);
                host.Dispose();
            }

            try
            {
                Directory.Delete(publishDir, recursive: true);
            }
            catch (IOException)
            {
            }
            catch (UnauthorizedAccessException)
            {
            }
        }
    }

    private static string FindRepositoryRoot()
    {
        var current = new DirectoryInfo(AppContext.BaseDirectory);
        while (current != null)
        {
            if (File.Exists(Path.Combine(current.FullName, "backend", "Rafael.Portfolio.sln")))
            {
                return current.FullName;
            }
            current = current.Parent;
        }

        throw new InvalidOperationException("Could not locate repository root directory.");
    }

    private static int GetFreePort()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        return port;
    }

    private static void Run(string fileName, string arguments)
    {
        using var process = Process.Start(new ProcessStartInfo
        {
            FileName = fileName,
            Arguments = arguments,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        }) ?? throw new InvalidOperationException($"Failed to start '{fileName}'.");

        var stdout = process.StandardOutput.ReadToEndAsync();
        var stderr = process.StandardError.ReadToEndAsync();
        process.WaitForExit();

        Assert.True(
            process.ExitCode == 0,
            $"'{fileName} {arguments}' exited with {process.ExitCode}.\n{stdout.Result}\n{stderr.Result}");
    }

    private static async Task WaitForHealthAsync(HttpClient http)
    {
        for (var attempt = 0; attempt < 60; attempt++)
        {
            try
            {
                using var response = await http.GetAsync(new Uri("/health", UriKind.Relative));
                if (response.IsSuccessStatusCode)
                {
                    return;
                }
            }
            catch (HttpRequestException)
            {
            }

            await Task.Delay(1000);
        }

        throw new InvalidOperationException("The published backend did not become healthy in time.");
    }
}
