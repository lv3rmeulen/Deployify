using System;
using System.Collections.Generic;
using System.CommandLine;
using System.IO;
using System.Linq;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;
using Deployify.Core.State;
using Spectre.Console;

namespace Deployify.Commands.Json;

/// <summary>
/// <c>--output json</c> for a command: stdout gets exactly one JSON document, written when the
/// command ends whether it succeeded or not, and everything the command would normally print
/// goes to stderr. Commands set <see cref="JsonRun.Result"/>; the envelope, the diagnostics and
/// the exit code are filled in here.
/// </summary>
/// <remarks>
/// <para>
/// The human-readable output still goes out, on stderr, so a CI log shows the plan while a
/// script reads the JSON. The commands print through the static <see cref="AnsiConsole"/>, so
/// for the duration of the command it is pointed at stderr, and the console logger is told to
/// write to stderr too (<see cref="DependencyInjection.AddDeployifyCore"/>).
/// </para>
/// <para>
/// The JSON goes to <see cref="InvocationConfiguration.Output"/> and the human output to
/// <see cref="InvocationConfiguration.Error"/>: stdout and stderr in the CLI, writers in tests.
/// </para>
/// </remarks>
internal static class JsonOutput
{
    /// <summary>The <c>schemaVersion</c> of every document. Raised only for a breaking change, in a major release.</summary>
    public const int SchemaVersion = 1;

    public static readonly JsonSerializerOptions SerializerOptions = new()
    {
        // Valid JSON either way; this keeps quotes, '<' and non-ASCII names readable instead of \u escapes.
        // Nothing embeds the document in HTML.
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DictionaryKeyPolicy = null,
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.Never,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) }
    };

    private static readonly AsyncLocal<JsonRun?> CurrentRun = new();

    /// <summary>The JSON run the current command belongs to, or null when it prints text.</summary>
    public static JsonRun? Current => CurrentRun.Value;

    /// <summary>
    /// Runs <paramref name="body"/> as <paramref name="command"/>. With <c>--output text</c> it
    /// just runs, with a null <see cref="JsonRun"/>. With <c>--output json</c> it runs with the
    /// console on stderr, then writes the document to stdout. The exit code is the body's.
    /// </summary>
    public static async Task<int> RunAsync(ParseResult parseResult, string command, Func<JsonRun?, Task<int>> body)
    {
        if (parseResult.GetValue(GlobalOptions.Output) != OutputFormat.Json)
        {
            return await body(null);
        }

        var invocation = parseResult.InvocationConfiguration;
        var run = new JsonRun(command);
        var original = AnsiConsole.Console;
        AnsiConsole.Console = CreateErrorConsole(invocation.Error);
        CurrentRun.Value = run;

        int exitCode;
        try
        {
            exitCode = await body(run);
        }
        catch (OperationCanceledException)
        {
            // Still one document on stdout, so a script reading it doesn't choke on nothing.
            run.Report(JsonSeverity.Error, "The command was cancelled.");
            Restore(original);
            Write(invocation.Output, run.ToEnvelope(1));
            throw;
        }
        catch (Exception ex)
        {
            // What Program does for text output, but on stderr, and recorded in the document.
            run.Report(JsonSeverity.Error, ex.Message);
            invocation.Error.WriteLine(ex is StateFormatException ? $"✗ {ex.Message}" : $"Unhandled exception: {ex}");
            exitCode = 1;
        }
        finally
        {
            Restore(original);
        }

        Write(invocation.Output, run.ToEnvelope(exitCode));
        return exitCode;
    }

    /// <summary>The document as text, as written to stdout.</summary>
    public static string Serialize(JsonEnvelope envelope) => JsonSerializer.Serialize(envelope, SerializerOptions);

    /// <summary>A rollout state, operation or other enum value as the JSON spells it: <c>rolledBack</c>.</summary>
    public static string Name<T>(T value) where T : struct, Enum => JsonNamingPolicy.CamelCase.ConvertName(value.ToString());

    private static void Restore(IAnsiConsole original)
    {
        AnsiConsole.Console = original;
        CurrentRun.Value = null;
    }

    private static void Write(TextWriter output, JsonEnvelope envelope)
    {
        output.Write(Serialize(envelope));
        output.Write('\n');
        output.Flush();
    }

    /// <summary>
    /// A console on stderr. Never interactive: nothing may wait for input when a program reads
    /// the output, and spinners become plain lines.
    /// </summary>
    internal static IAnsiConsole CreateErrorConsole(TextWriter error)
    {
        var console = AnsiConsole.Create(new AnsiConsoleSettings
        {
            Out = new AnsiConsoleOutput(error),
            Interactive = InteractionSupport.No
        });
        ConsoleSetup.Configure(console);
        return console;
    }
}

/// <summary>What one command run with <c>--output json</c> reports.</summary>
internal sealed class JsonRun(string command)
{
    private readonly List<JsonDiagnostic> _diagnostics = [];

    public string Command { get; } = command;

    public string? Environment { get; set; }

    public JsonTarget? Target { get; set; }

    /// <summary>The command's document (<see cref="JsonPlan"/>, <see cref="JsonDiff"/>, …), or null when it failed before having one.</summary>
    public object? Result { get; set; }

    public IReadOnlyList<JsonDiagnostic> Diagnostics => _diagnostics;

    /// <summary>Records the environment and target the command resolved.</summary>
    public void Use(ResolvedContext resolved)
    {
        Environment = resolved.EnvName;
        Target = new JsonTarget(resolved.TargetType.Value, resolved.Target.DisplayName);
    }

    /// <summary>Records a message the command also printed. Called by <see cref="ConsoleDeploymentReporter"/>.</summary>
    public void Report(JsonSeverity severity, string message)
    {
        lock (_diagnostics)
        {
            _diagnostics.Add(new JsonDiagnostic(severity, message));
        }
    }

    public JsonEnvelope ToEnvelope(int exitCode)
    {
        JsonDiagnostic[] diagnostics;
        lock (_diagnostics)
        {
            diagnostics = [.. _diagnostics];
        }

        // The first error is the one that stopped the command; later ones tend to follow from it.
        var error = exitCode == 0
            ? null
            : diagnostics.FirstOrDefault(d => d.Severity == JsonSeverity.Error)?.Message
              ?? $"The command failed with exit code {exitCode}.";

        return new JsonEnvelope(JsonOutput.SchemaVersion, Command, exitCode == 0, exitCode, error,
            Environment, Target, diagnostics, Result);
    }
}
