using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using DockerManager.App;
using DockerManager.App.Models;
using DockerManager.App.Services;
using DockerManager.App.ViewModels;
using DockerManager.App.Views;
using Xunit;

namespace DockerManager.Tests;

public class CommandLineHelperTests
{
    [Fact]
    public void TestParseCommandLine_NginxDaemonOff_PreservesArguments()
    {
        var input = "nginx -g \"daemon off;\"";
        var result = DockerManager.App.Helpers.CommandLineHelper.ParseCommandLine(input);

        Assert.NotNull(result);
        Assert.Equal(3, result.Count);
        Assert.Equal("nginx", result[0]);
        Assert.Equal("-g", result[1]);
        Assert.Equal("daemon off;", result[2]);
    }

    [Fact]
    public void TestParseCommandLine_SingleQuotes()
    {
        var input = "nginx -g 'daemon off;'";
        var result = DockerManager.App.Helpers.CommandLineHelper.ParseCommandLine(input);

        Assert.NotNull(result);
        Assert.Equal(3, result.Count);
        Assert.Equal("nginx", result[0]);
        Assert.Equal("-g", result[1]);
        Assert.Equal("daemon off;", result[2]);
    }

    [Fact]
    public void TestParseCommandLine_JsonArraySyntax()
    {
        var input = "[\"nginx\", \"-g\", \"daemon off;\"]";
        var result = DockerManager.App.Helpers.CommandLineHelper.ParseCommandLine(input);

        Assert.NotNull(result);
        Assert.Equal(3, result.Count);
        Assert.Equal("nginx", result[0]);
        Assert.Equal("-g", result[1]);
        Assert.Equal("daemon off;", result[2]);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void TestParseCommandLine_NullOrWhitespace_ReturnsNull(string? input)
    {
        var result = DockerManager.App.Helpers.CommandLineHelper.ParseCommandLine(input);
        Assert.Null(result);
    }

    [Fact]
    public void TestFormatCommandLine_QuotesArgumentsWithSpaces()
    {
        var args = new List<string> { "nginx", "-g", "daemon off;" };
        var formatted = DockerManager.App.Helpers.CommandLineHelper.FormatCommandLine(args);

        Assert.Equal("nginx -g \"daemon off;\"", formatted);
    }

    [Fact]
    public void TestEditServiceViewModel_CommandParsing_MaintainsQuotes()
    {
        var vm = new EditServiceViewModel();
        vm.DisplayName = "Stageloper Site";
        vm.ContainerName = "stage-site";
        vm.Image = "nginx:alpine";
        vm.Command = "nginx -g \"daemon off;\"";

        var service = vm.ToServiceDefinition();

        Assert.NotNull(service.Command);
        Assert.Equal(3, service.Command.Count);
        Assert.Equal("nginx", service.Command[0]);
        Assert.Equal("-g", service.Command[1]);
        Assert.Equal("daemon off;", service.Command[2]);
    }

    [Fact]
    public void TestDockerComposeImporter_ImportsCommandCorrectly()
    {
        var yaml = @"
services:
  web:
    image: nginx:alpine
    command: nginx -g ""daemon off;""
";
        var importer = new DockerComposeImporterService();
        var result = importer.ParseComposeYaml(yaml);

        Assert.True(result.Success);
        Assert.Single(result.ParsedServices);
        var svc = result.ParsedServices[0];
        Assert.NotNull(svc.Command);
        Assert.Equal(3, svc.Command.Count);
        Assert.Equal("daemon off;", svc.Command[2]);
    }

    [Fact]
    public async Task TestGetContainerStatsSummaryAsync_InvalidContainer_ReturnsNullGracefully()
    {
        var cred = new CredentialService();
        var sett = new SettingsService(cred);
        var dock = new DockerService(sett, cred);

        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(500));
        var result = await dock.GetContainerStatsSummaryAsync("non_existent_container_xyz_123", cts.Token);

        // Should return null and not crash with ObjectDisposedException
        Assert.Null(result);
    }
}




