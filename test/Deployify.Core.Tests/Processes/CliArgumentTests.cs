using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Deployify.Core.Processes;
using Deployify.Core.SecretBackends.KeyVault;
using Deployify.Core.Services;
using Deployify.Tests.Shared;
using Shouldly;
using Xunit;

namespace Deployify.Core.Tests.Processes;

public sealed class KeyVaultCliArgumentTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task SetSecret_TagsWithSpacesStayWhole_AndNothingIsLogged()
    {
        var fake = new FakeProcessRunner { Default = new ProcessResult(0, "https://kv.vault.azure.net/secrets/db/abc123\n", "") };
        var client = new AzureCliKeyVaultClient("sub-1", fake);

        var version = await client.SetSecretAsync("kv", "db", "s3cret",
            new Dictionary<string, string> { ["owner"] = "team a", ["managed-by"] = "deployify" }, Ct);

        version.ShouldBe("abc123");
        var call = fake.Calls.Single();
        call.Sensitive.ShouldBeTrue();
        call.Args.Take(2).ShouldBe(["--subscription", "sub-1"]);
        call.Args.ShouldContain("owner=team a");
        call.Args.ShouldContain("managed-by=deployify");
        call.Args.ShouldNotContain("s3cret");
    }

    [Fact]
    public async Task ShowSecret_IsNotLogged()
    {
        var fake = new FakeProcessRunner
        {
            Default = new ProcessResult(0, "{\"id\":\"https://kv.vault.azure.net/secrets/db/v1\",\"name\":\"db\",\"value\":\"x\"}", "")
        };

        await new AzureCliKeyVaultClient(runner: fake).GetSecretAsync("kv", "db", Ct);

        fake.Calls.Single().Sensitive.ShouldBeTrue();
        fake.Calls.Single().Args.ShouldBe(["keyvault", "secret", "show", "--vault-name", "kv", "--name", "db", "--output", "json"]);
    }
}

public sealed class PublishArgumentTests
{
    [Fact]
    public void ProjectPathAndProperties_AreSeparateArguments()
        => Publisher.BuildPublishArgs(@"C:\My Apps\Api\Api.csproj", "linux", "x64", "team/api", "v1",
                " -p:ContainerRegistry=docker.io", "", " -p:ContainerDaemonSocket=npipe:////./pipe/docker_engine")
            .ShouldBe(
            [
                "publish", @"C:\My Apps\Api\Api.csproj", "-t:PublishContainer", "--os", "linux", "--arch", "x64",
                "-p:ContainerRepository=team/api", "-p:ContainerImageTag=v1",
                "-p:ContainerRegistry=docker.io", "-p:ContainerDaemonSocket=npipe:////./pipe/docker_engine"
            ]);
}
