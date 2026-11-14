using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using Deployify.Core.Models;
using Deployify.Core.State;
using Shouldly;
using Xunit;

namespace Deployify.Core.Tests.SecretBackends;

public sealed class StateFileSecretsMigrationTests
{
    private static EnvironmentConfig Nomad(string? token, string? registryPassword = null) => new()
    {
        Target = DeploymentTargetType.Nomad,
        TargetConfig =
        {
            // Loaded state files hold JsonElements, with whatever casing the file uses.
            ["nomad"] = JsonSerializer.SerializeToElement(new Dictionary<string, object?>
            {
                ["address"] = "http://nomad:4646",
                ["token"] = token,
                ["registryPassword"] = registryPassword
            })
        }
    };

    private static string? NomadSetting(EnvironmentConfig env, string name)
        => ((JsonElement)env.TargetConfig["nomad"]!).GetProperty(name).GetString();

    [Fact]
    public void LiteralNomadToken_BecomesAReferenceToTheVariableTheTargetReads()
    {
        var state = new DeployifyStateFile { Environments = { ["prod"] = Nomad("s3cret", "regpass") } };

        var changes = StateFileSecrets.MigrateToReferences(state);

        NomadSetting(state.Environments["prod"], "token").ShouldBe("env:NOMAD_TOKEN");
        NomadSetting(state.Environments["prod"], "registryPassword").ShouldBe("env:DEPLOYIFY_REGISTRY_PASSWORD");
        NomadSetting(state.Environments["prod"], "address").ShouldBe("http://nomad:4646");
        changes.ShouldContain(new SecretMigration("prod.nomad.token", "NOMAD_TOKEN", "s3cret"));
        changes.ShouldContain(new SecretMigration("prod.nomad.registryPassword", "DEPLOYIFY_REGISTRY_PASSWORD", "regpass"));
        StateFileSecrets.FindLiteral(state).ShouldBeEmpty();
    }

    [Fact]
    public void ExistingReferences_AreLeftAlone()
    {
        var state = new DeployifyStateFile { Environments = { ["prod"] = Nomad("env:MY_NOMAD") } };

        StateFileSecrets.MigrateToReferences(state).ShouldBeEmpty();
        NomadSetting(state.Environments["prod"], "token").ShouldBe("env:MY_NOMAD");
    }

    [Fact]
    public void DifferentValuesForTheSameVariable_GetAnEnvironmentSuffix()
    {
        var state = new DeployifyStateFile
        {
            Environments = { ["dev"] = Nomad("dev-token"), ["prod"] = Nomad("prod-token") }
        };

        StateFileSecrets.MigrateToReferences(state);

        NomadSetting(state.Environments["dev"], "token").ShouldBe("env:NOMAD_TOKEN");
        NomadSetting(state.Environments["prod"], "token").ShouldBe("env:NOMAD_TOKEN_PROD");
    }

    [Fact]
    public void TheSameValueInTwoEnvironments_SharesOneVariable()
    {
        var state = new DeployifyStateFile
        {
            Environments = { ["dev"] = Nomad("shared"), ["prod"] = Nomad("shared") }
        };

        var changes = StateFileSecrets.MigrateToReferences(state);

        changes.Select(c => c.Variable).Distinct().ShouldBe(["NOMAD_TOKEN"]);
        NomadSetting(state.Environments["prod"], "token").ShouldBe("env:NOMAD_TOKEN");
    }

    [Fact]
    public void OtherSecretSettings_AreMigrated()
    {
        var env = new EnvironmentConfig
        {
            Dashboard = new DashboardConfig { BrowserToken = "dash" },
            Parameters = { ["db-password"] = "pw", ["replicas"] = "3" },
            State = new StateBackendConfig
            {
                Backend = StateBackendType.Consul,
                Consul = new ConsulStateConfig { Token = "consul" }
            }
        };
        env.Secrets.Vault.Token = "vault";
        var state = new DeployifyStateFile { Environments = { ["staging"] = env } };

        StateFileSecrets.FindLiteral(state).ShouldContain("staging.secrets.vault.token");

        StateFileSecrets.MigrateToReferences(state);

        env.Dashboard.BrowserToken.ShouldBe("env:DEPLOYIFY_DASHBOARD_TOKEN");
        env.Parameters["db-password"].ShouldBe("env:DEPLOYIFY_STAGING_PARAMETERS_DB_PASSWORD");
        env.Parameters["replicas"].ShouldBe("3");
        env.State.Consul.Token.ShouldBe("env:CONSUL_HTTP_TOKEN");
        env.Secrets.Vault.Token.ShouldBe("env:VAULT_TOKEN");
        StateFileSecrets.FindLiteral(state).ShouldBeEmpty();
    }

    [Fact]
    public void NestedTargetSecrets_WithoutAWellKnownVariable_GetADerivedName()
    {
        var env = new EnvironmentConfig
        {
            TargetConfig =
            {
                ["nomad"] = JsonSerializer.SerializeToElement(new
                {
                    ArtifactStore = new { HttpToken = "abc" }
                })
            }
        };
        var state = new DeployifyStateFile { Environments = { ["prod"] = env } };

        var changes = StateFileSecrets.MigrateToReferences(state);

        changes.ShouldHaveSingleItem().Variable.ShouldBe("DEPLOYIFY_PROD_NOMAD_ARTIFACTSTORE_HTTPTOKEN");
        ((JsonElement)env.TargetConfig["nomad"]!).GetProperty("ArtifactStore").GetProperty("HttpToken").GetString()
            .ShouldBe("env:DEPLOYIFY_PROD_NOMAD_ARTIFACTSTORE_HTTPTOKEN");
    }
}
