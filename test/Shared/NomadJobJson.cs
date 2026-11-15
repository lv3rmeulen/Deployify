using System;
using System.IO;
using System.Linq;
using System.Text.Json.Nodes;
using System.Threading.Tasks;

namespace Deployify.Tests.Shared;

/// <summary>A generated Nomad job file (<c>{"Job": {…}}</c>), with shortcuts to the parts tests look at.</summary>
internal sealed class NomadJobJson
{
    private NomadJobJson(string text)
    {
        Text = text;
        Job = JsonNode.Parse(text)!["Job"]!.AsObject();
    }

    /// <summary>The file as written.</summary>
    public string Text { get; }

    public JsonObject Job { get; }

    public static async Task<NomadJobJson> ReadAsync(string path) => new(await File.ReadAllTextAsync(path));

    public static NomadJobJson Parse(string text) => new(text);

    /// <summary>The job's only task group.</summary>
    public JsonObject Group => Job["TaskGroups"]!.AsArray().Single()!.AsObject();

    public JsonArray Tasks => Group["Tasks"]?.AsArray() ?? [];

    /// <summary>The task named <paramref name="name"/>, or the group's first task.</summary>
    public JsonObject Task(string? name = null)
        => (name is null ? Tasks.First() : Tasks.Single(t => (string?)t!["Name"] == name))!.AsObject();

    public bool HasTask(string name) => Tasks.Any(t => (string?)t!["Name"] == name);

    public JsonObject Config(string? task = null) => Task(task)["Config"]!.AsObject();

    public JsonObject? Env(string? task = null) => Task(task)["Env"]?.AsObject();

    /// <summary>The task's only template, or null.</summary>
    public JsonObject? Template(string? task = null) => Task(task)["Templates"]?.AsArray().Single()!.AsObject();

    /// <summary>The group's only service, or null.</summary>
    public JsonObject? Service => Group["Services"]?.AsArray().Single()!.AsObject();

    /// <summary>The service's only check, or null.</summary>
    public JsonObject? Check => Service?["Checks"]?.AsArray().Single()!.AsObject();

    public JsonObject? Network => Group["Networks"]?.AsArray().Single()!.AsObject();

    /// <summary>Labels of the network's static and dynamic ports.</summary>
    public string[] PortLabels =>
    [
        .. (Network?["ReservedPorts"]?.AsArray() ?? []).Concat(Network?["DynamicPorts"]?.AsArray() ?? [])
            .Select(p => (string)p!["Label"]!)
    ];

    /// <summary>A string array (driver args, ports) as .NET strings.</summary>
    public static string[] Strings(JsonNode? array) => array is null ? [] : [.. array.AsArray().Select(n => (string)n!)];

    /// <summary>Nanoseconds for a whole number of seconds, as Nomad's API takes durations.</summary>
    public static long Seconds(double seconds) => (long)(seconds * 1_000_000_000);

    public override string ToString() => Text;
}
