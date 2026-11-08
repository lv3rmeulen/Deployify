using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Deployify.Core.Processes;
using Deployify.Tests.Shared;
using Microsoft.Extensions.Logging.Abstractions;
using Shouldly;
using Xunit;

namespace Deployify.Target.Nomad.Tests;

/// <summary>The exact CLI calls the artifact stores make, checked through a fake process runner.</summary>
public sealed class ArtifactStoreCliTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task AzureBlob_ConnectionString_GoesThroughTheEnvironment_NotTheCommandLine()
    {
        const string conn = "DefaultEndpointsProtocol=https;AccountName=acc;AccountKey=abc+/==;EndpointSuffix=core.windows.net";
        var fake = new FakeProcessRunner { Default = new ProcessResult(0, "https://acc.blob.core.windows.net/c/api-1.zip\n", "") };
        var store = new AzureBlobArtifactStore(new ArtifactStoreConfig
        {
            AzureStorageAccount = "acc", AzureContainer = "c", AzureConnectionString = conn
        }, NullLogger.Instance, fake);

        var url = await store.UploadAsync("api", "1", @"C:\out dir\api.zip", Ct);

        url.ShouldBe("https://acc.blob.core.windows.net/c/api-1.zip");
        fake.Calls.Count.ShouldBe(2);
        fake.Calls.ShouldAllBe(c => c.Env!["AZURE_STORAGE_CONNECTION_STRING"] == conn);
        fake.Calls.ShouldAllBe(c => !c.Args.Any(a => a.Contains("AccountKey")));
        fake.Single("upload").Args.ShouldContain(@"C:\out dir\api.zip");
    }

    [Fact]
    public async Task AzureBlob_FailedUrlLookup_Throws_InsteadOfReturningTheError()
    {
        var fake = new FakeProcessRunner()
            .When(["blob", "url"], new ProcessResult(1, "", "ERROR: AuthorizationFailure"));
        var store = new AzureBlobArtifactStore(new ArtifactStoreConfig { AzureStorageAccount = "acc", AzureContainer = "c" },
            NullLogger.Instance, fake);

        var ex = await Should.ThrowAsync<System.InvalidOperationException>(() => store.UploadAsync("api", "1", "api.zip", Ct));

        ex.Message.ShouldContain("AuthorizationFailure");
        fake.Single("upload").Args.ShouldContain("--auth-mode");
    }

    [Fact]
    public async Task S3_ProfileAndRegion_AreSeparateArguments()
    {
        var fake = new FakeProcessRunner();
        var store = new S3ArtifactStore(new ArtifactStoreConfig
        {
            S3Bucket = "b", S3Region = "eu-west-1", S3AwsProfile = "my profile"
        }, NullLogger.Instance, fake);

        await store.UploadAsync("api", "1", "/tmp/my dir/api.zip", Ct);

        fake.Calls.Single().Args.ShouldBe(
            ["s3", "cp", "/tmp/my dir/api.zip", "s3://b/api-1.zip", "--profile", "my profile", "--region", "eu-west-1"]);
    }

    [Fact]
    public async Task Gcs_CredentialsFile_IsPassedAsEnvironment()
    {
        var fake = new FakeProcessRunner();
        var store = new GcsArtifactStore(new ArtifactStoreConfig { GcsBucket = "b", GcsCredentialsFile = "/keys/sa.json" },
            NullLogger.Instance, fake);

        await store.UploadAsync("api", "1", "api.zip", Ct);

        var call = fake.Calls.Single();
        call.Args.ShouldBe(["storage", "cp", "api.zip", "gs://b/api-1.zip"]);
        call.Env!["GOOGLE_APPLICATION_CREDENTIALS"].ShouldBe("/keys/sa.json");
    }
}
