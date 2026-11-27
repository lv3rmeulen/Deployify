using System.Reflection;
using Deployify.Commands;
using Deployify.Core.Versioning;

namespace Deployify.SelfUpdate;

/// <summary>The version of this Deployify, as <c>deployify --version</c> prints it but without the commit.</summary>
internal static class CliVersion
{
    public static string Current { get; } = VersionOrder.WithoutMetadata(
        typeof(DeployifyCli).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion
        ?? typeof(DeployifyCli).Assembly.GetName().Version?.ToString(3)
        ?? "0.0.0");
}
