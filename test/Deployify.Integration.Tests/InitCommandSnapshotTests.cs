using System;
using System.Collections.Generic;
using System.CommandLine;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using Deployify.Commands;
using Deployify.Core.Models;
using Deployify.Core.Services;
using Deployify.Tests.Shared;
using Shouldly;
using Spectre.Console;
using Spectre.Console.Testing;
using Xunit;

namespace Deployify.Integration.Tests;

/// <summary>
/// Pins down what <c>deployify init</c> accepts and writes, so its per-target flows can be
/// reorganized without changing behavior: the option list (names, aliases, defaults, help), and
/// the state file every target produces from flags and from the interactive prompts.
/// Regenerate with <c>DEPLOYIFY_UPDATE_SNAPSHOTS=1</c> only for a deliberate change.
/// </summary>
[Collection(nameof(ConsoleOutput))]
public sealed class InitCommandSnapshotTests : IDisposable
{
    private readonly IAnsiConsole _original = AnsiConsole.Console;
    private readonly TestConsole _console = new();
    private readonly TempDir _dir = new();

    public InitCommandSnapshotTests()
    {
        _console.Profile.Width = 200;
        AnsiConsole.Console = _console;
    }

    public void Dispose()
    {
        AnsiConsole.Console = _original;
        _dir.Dispose();
    }

    // ── Options ───────────────────────────────────────────────────────────────

    [Fact]
    public void Options_AreUnchanged()
    {
        var text = new StringBuilder();
        foreach (var option in InitCommand.Build().Options)
        {
            text.Append(option.Name);
            foreach (var alias in option.Aliases.Order(StringComparer.Ordinal))
            {
                text.Append(", ").Append(alias);
            }

            text.Append(" : ").Append(TypeName(option.ValueType));
            if (option.HasDefaultValue)
            {
                // --project-path defaults to the working directory, which differs per machine.
                var value = option.GetDefaultValue()?.ToString() ?? "null";
                text.Append(" = ").Append(value == Directory.GetCurrentDirectory() ? "<cwd>" : value);
            }

            if (option.Required)
            {
                text.Append(" (required)");
            }

            text.AppendLine().Append("    ").AppendLine(option.Description);
        }

        Snapshot.Verify("init/options.txt", text.ToString());
    }

    private static string TypeName(Type type) => Nullable.GetUnderlyingType(type) is { } inner
        ? TypeName(inner) + "?"
        : type.Name;

    // ── Non-interactive ───────────────────────────────────────────────────────

    public static TheoryData<string> Targets => new(
        "Nomad", "Kubernetes", "Compose", "AwsEcs", "AzureContainerApps", "NomadConsul",
        "Helm", "ArgoCd", "Flux", "OvhCloud", "Iis");

    /// <summary>Every flag set to a value other than its default, so each one shows up in the snapshot.</summary>
    private const string AllFlags =
        "--container-registry registry.example.com/team --image-tag 1.2.3 --output-path out " +
        "--container-runtime Podman --container-os linux --container-arch arm64 " +
        "--deploy-dapr --dapr-version 1.14 --dapr-http-port 3600 --dapr-grpc-port 50101 " +
        "--dapr-components-path ./components --dapr-config-path ./dapr.yaml --dapr-log-level debug " +
        "--deploy-dashboard --dashboard-tag 9.0 --dashboard-port 19888 --dashboard-otlp-port 19889 " +
        "--dashboard-token env:DASH_TOKEN " +
        "--nomad-address http://nomad:4646 --nomad-token env:PROD_NOMAD --nomad-namespace apps " +
        "--nomad-datacenter eu1 --nomad-mode RawExec --nomad-registry-username ci " +
        "--nomad-registry-password env:REG_PW --nomad-register-services false " +
        "--nomad-service-provider consul --nomad-health-check-path /healthz " +
        "--kubernetes-context prod-ctx --kubernetes-namespace-prefix shop --kubernetes-ingress-domain apps.example.com " +
        "--gitops-repo https://git.example.com/config.git --gitops-branch deploy --gitops-path envs/prod " +
        "--ovh-endpoint ovh-ca --ovh-project proj-1 --ovh-cluster cluster-1 " +
        "--iis-host web01 --iis-transport Ssh --iis-user deploy --iis-sites-root D:\\sites " +
        "--compose-project shopapp --compose-restart always " +
        "--ecs-region eu-west-1 --ecs-cluster shop --ecs-profile prod --ecs-subnets \"subnet-a, subnet-b\" " +
        "--ecs-security-groups sg-1 --ecs-execution-role arn:aws:iam::1:role/exec " +
        "--aca-subscription sub-1 --aca-resource-group shop-rg --aca-location westeurope " +
        "--aca-environment shop-env --aca-acr shop.azurecr.io " +
        "--nc-nomad-address http://nc-nomad:4646 --nc-nomad-token env:NC_NOMAD --nc-nomad-namespace mesh " +
        "--nc-datacenter eu2 --nc-consul-address http://consul:8500 --nc-consul-token env:NC_CONSUL " +
        "--nc-ingress-gateway";

    [Theory]
    [MemberData(nameof(Targets))]
    public async Task NonInteractive_WritesSnapshot_WithEveryFlag(string target)
    {
        var code = await new RootCommand("t") { InitCommand.Build() }.InvokeAsync(
            $"init --non-interactive --project-path {_dir.Path} --env-name prod --target {target} {AllFlags}",
            TestContext.Current.CancellationToken);

        code.ShouldBe(0, _console.Output);
        Snapshot.Verify($"init/non-interactive/{target}.json", ReadState());
    }

    [Theory]
    [MemberData(nameof(Targets))]
    public async Task NonInteractive_WritesSnapshot_WithDefaults(string target)
    {
        var code = await new RootCommand("t") { InitCommand.Build() }.InvokeAsync(
            $"init --non-interactive --project-path {_dir.Path} --env-name prod --target {target}",
            TestContext.Current.CancellationToken);

        code.ShouldBe(0, _console.Output);
        Snapshot.Verify($"init/defaults/{target}.json", ReadState());
    }

    [Fact]
    public async Task NonInteractive_RejectsEveryLiteralSecretFlag()
    {
        var code = await new RootCommand("t") { InitCommand.Build() }.InvokeAsync(
            $"init --non-interactive --project-path {_dir.Path} --target Kubernetes " +
            "--dashboard-token a --nomad-token b --nomad-registry-password c --nc-nomad-token d --nc-consul-token e",
            TestContext.Current.CancellationToken);

        code.ShouldBe(1);
        Snapshot.Verify("init/literal-secrets.txt", _console.Output);
    }

    // ── Interactive ───────────────────────────────────────────────────────────

    /// <summary>The target choices, in the order the prompt lists them.</summary>
    private static readonly string[] s_choices =
    [
        "Nomad", "Kubernetes", "Compose", "AwsEcs", "AzureContainerApps", "NomadConsul",
        "Helm", "ArgoCd", "Flux", "OvhCloud", "Iis"
    ];

    [Theory]
    [MemberData(nameof(Targets))]
    public async Task Interactive_WritesSnapshot(string target)
    {
        _console.Interactive();

        Pick(Array.IndexOf(s_choices, target));
        Text("registry.example.com/team");
        Text("1.2.3");
        Text("out");
        Pick(1); // Podman
        Pick(1); // arm64
        Text("y"); // Dapr
        Text("3600");
        Text("50101");
        Text("./components");
        Text("y"); // dashboard
        Text("19888");
        Text("19889");
        Text("y"); // browser token
        EnvReference("DASH_TOKEN");

        switch (target)
        {
            case "Nomad":
                Text("http://nomad:4646");
                Text("apps");
                Text("eu1");
                EnvReference("PROD_NOMAD");
                Pick(1); // RawExec
                Text("y"); // private registry
                Text("ci");
                EnvReference("REG_PW");
                Text("y"); // register services
                Pick(1); // consul
                Text("/healthz");
                break;

            case "Kubernetes" or "Helm":
                Text("prod-ctx");
                Text("shop");
                Text("apps.example.com");
                break;

            case "ArgoCd" or "Flux":
                Text("prod-ctx");
                Text("shop");
                Text("apps.example.com");
                Text("https://git.example.com/config.git");
                Text("deploy");
                Text("envs/prod");
                break;

            case "OvhCloud":
                Pick(1); // ovh-ca
                Text("proj-1");
                Text("cluster-1");
                Text("shop");
                Text("apps.example.com");
                break;

            case "Iis":
                Text("web01");
                Pick(1); // Ssh
                Text("deploy");
                Text("D:\\sites");
                break;

            case "Compose":
                Text("shopapp");
                Text("always");
                break;

            case "AwsEcs":
                Text("eu-west-1");
                Text("shop");
                Text("prod");
                Text("subnet-a, subnet-b");
                Text("sg-1");
                Text("arn:aws:iam::1:role/exec");
                break;

            case "AzureContainerApps":
                Text("sub-1");
                Text("shop-rg");
                Text("westeurope");
                Text("shop-env");
                Text("shop.azurecr.io");
                break;

            case "NomadConsul":
                Text("http://nc-nomad:4646");
                Text("mesh");
                Text("eu2");
                EnvReference("NC_NOMAD");
                Text("http://consul:8500");
                EnvReference("NC_CONSUL");
                Text("y"); // ingress gateway
                break;
        }

        var config = await InitCommand.PromptEnvironmentConfigAsync("prod", _dir.Path, TestContext.Current.CancellationToken);
        await new StateManager().SaveFileAsync(new DeployifyStateFile
        {
            ActiveEnvironment = "prod",
            Environments = new Dictionary<string, EnvironmentConfig> { ["prod"] = config }
        }, _dir.Path, TestContext.Current.CancellationToken);

        Snapshot.Verify($"init/interactive/{target}.json", ReadState());
    }

    [Theory]
    [MemberData(nameof(Targets))]
    public async Task Interactive_WritesSnapshot_WithDefaults(string target)
    {
        _console.Interactive();

        Pick(Array.IndexOf(s_choices, target));
        for (var i = 0; i < 3; i++)
        {
            Enter(); // registry, tag, output path
        }

        Enter(); // runtime
        Enter(); // architecture
        Enter(); // Dapr: no
        Enter(); // dashboard: no

        var answers = target switch
        {
            // address, namespace, datacenter, token source, mode, private registry, register, provider, health path
            "Nomad" => 9,
            "Kubernetes" or "Helm" => 3,
            "ArgoCd" or "Flux" => 6,
            "Iis" => 2,
            "Compose" => 2,
            "AwsEcs" => 6,
            "AzureContainerApps" => 5,
            "NomadConsul" => 7,
            _ => 0
        };

        if (target == "OvhCloud")
        {
            Enter(); // endpoint
            Enter(); // project
            Text("cluster-1"); // required
            Enter();
            Enter();
        }

        for (var i = 0; i < answers; i++)
        {
            Enter();
        }

        var config = await InitCommand.PromptEnvironmentConfigAsync("prod", _dir.Path, TestContext.Current.CancellationToken);
        await new StateManager().SaveFileAsync(new DeployifyStateFile
        {
            ActiveEnvironment = "prod",
            Environments = new Dictionary<string, EnvironmentConfig> { ["prod"] = config }
        }, _dir.Path, TestContext.Current.CancellationToken);

        Snapshot.Verify($"init/interactive-defaults/{target}.json", ReadState());
    }

    private void Text(string value) => _console.Input.PushTextWithEnter(value);

    private void Enter() => _console.Input.PushKey(ConsoleKey.Enter);

    private void Pick(int index)
    {
        for (var i = 0; i < index; i++)
        {
            _console.Input.PushKey(ConsoleKey.DownArrow);
        }

        _console.Input.PushKey(ConsoleKey.Enter);
    }

    /// <summary>Answers a "where should Deployify get the secret?" prompt with another variable.</summary>
    private void EnvReference(string variable)
    {
        Pick(1);
        Text(variable);
    }

    private string ReadState()
        => File.ReadAllText(Path.Combine(_dir.Path, "deployify-state.json"))
            .Replace(_dir.Path.Replace("\\", "\\\\"), "<dir>", StringComparison.Ordinal)
            .Replace(_dir.Path, "<dir>", StringComparison.Ordinal);
}
