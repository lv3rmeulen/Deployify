using System.Collections;
using System.CommandLine;
using System.Text;
using Deployify.Commands;

// Generates docs/src/content/docs/reference/cli/*.md from Deployify's command tree: one page per
// top-level command (with its subcommands) and an index. Everything comes from the commands'
// names, descriptions, arguments and options, so the reference can't drift from the CLI.
//
//   dotnet run --project tools/Deployify.DocGen -- docs/src/content/docs/reference/cli

var outputDir = args.Length > 0 ? args[0] : Path.Combine("docs", "src", "content", "docs", "reference", "cli");
Directory.CreateDirectory(outputDir);

// Remove pages for commands that no longer exist.
foreach (var stale in Directory.GetFiles(outputDir, "*.md"))
{
    File.Delete(stale);
}

var root = DeployifyCli.CreateRootCommand();
var commands = root.Subcommands.Where(c => !c.Hidden).ToList();

var index = new StringBuilder();
index.AppendLine("---");
index.AppendLine("title: CLI reference");
index.AppendLine("description: Every deployify command and option.");
index.AppendLine("sidebar:");
index.AppendLine("  order: 0");
index.AppendLine("---");
index.AppendLine();
index.AppendLine(Generated);
index.AppendLine();
index.AppendLine("| Command | Description |");
index.AppendLine("|---|---|");
foreach (var command in commands)
{
    index.AppendLine($"| [`{command.Name}`](./{command.Name}/) | {Cell(command.Description)} |");
}

Write("index.md", index);

var order = 1;
foreach (var command in commands)
{
    var page = new StringBuilder();
    page.AppendLine("---");
    page.AppendLine($"title: deployify {command.Name}");
    page.AppendLine($"description: {Yaml(command.Description ?? command.Name)}");
    page.AppendLine("sidebar:");
    page.AppendLine($"  label: {command.Name}");
    page.AppendLine($"  order: {order++}");
    page.AppendLine("---");
    page.AppendLine();
    page.AppendLine(Generated);
    page.AppendLine();
    WriteCommand(page, command, ["deployify"], level: 0);
    Write($"{command.Name}.md", page);
}

Console.WriteLine($"Wrote {commands.Count + 1} pages to {outputDir}");
return 0;

void WriteCommand(StringBuilder sb, Command command, IReadOnlyList<string> parents, int level)
{
    var path = parents.Append(command.Name).ToList();
    var subcommands = command.Subcommands.Where(c => !c.Hidden).ToList();

    if (level > 0)
    {
        sb.AppendLine($"{new string('#', level + 1)} `{string.Join(' ', path.Skip(1))}`");
        sb.AppendLine();
    }

    if (!string.IsNullOrWhiteSpace(command.Description))
    {
        sb.AppendLine(Text(command.Description));
        sb.AppendLine();
    }

    var arguments = command.Arguments.Where(a => !a.Hidden).ToList();
    var options = command.Options.Where(o => !o.Hidden && o.Name is not ("--help" or "--version")).ToList();

    sb.AppendLine("```");
    var usage = new StringBuilder(string.Join(' ', path));
    if (subcommands.Count > 0 && arguments.Count == 0 && options.Count == 0)
    {
        usage.Append(" <command>");
    }

    foreach (var argument in arguments)
    {
        usage.Append($" <{argument.Name}>");
    }

    if (options.Count > 0)
    {
        usage.Append(" [options]");
    }

    sb.AppendLine(usage.ToString());
    sb.AppendLine("```");
    sb.AppendLine();

    if (arguments.Count > 0)
    {
        sb.AppendLine("**Arguments**");
        sb.AppendLine();
        sb.AppendLine("| Argument | Description |");
        sb.AppendLine("|---|---|");
        foreach (var argument in arguments)
        {
            sb.AppendLine($"| `<{argument.Name}>` | {Cell(argument.Description)} |");
        }

        sb.AppendLine();
    }

    if (options.Count > 0)
    {
        sb.AppendLine("**Options**");
        sb.AppendLine();
        sb.AppendLine("| Option | Description | Default |");
        sb.AppendLine("|---|---|---|");
        foreach (var option in options)
        {
            var names = new[] { option.Name }.Concat(option.Aliases.OrderBy(a => a.Length))
                .Select(n => $"`{n}`");
            var description = Cell(option.Description);
            if (option.Required)
            {
                description = $"**Required.** {description}";
            }

            sb.AppendLine($"| {string.Join(", ", names)} | {description} | {Default(option)} |");
        }

        sb.AppendLine();
    }

    foreach (var subcommand in subcommands)
    {
        WriteCommand(sb, subcommand, path, level + 1);
    }
}

void Write(string fileName, StringBuilder content)
{
    // Unix newlines inside; git checks the files out with the repo's own line endings.
    File.WriteAllText(Path.Combine(outputDir, fileName), content.ToString().Replace("\r\n", "\n"));
}

static string Default(Option option)
{
    if (!option.HasDefaultValue)
    {
        return "";
    }

    object? value;
    try
    {
        value = option.GetDefaultValue();
    }
    catch
    {
        return "";
    }

    return value switch
    {
        null => "",
        // Defaults computed from the machine running the generator (the current directory).
        string s when Path.IsPathFullyQualified(s) && s == Directory.GetCurrentDirectory() => "current directory",
        string s when s.Length == 0 => "",
        string s => $"`{s}`",
        bool b => $"`{b.ToString().ToLowerInvariant()}`",
        Enum e => $"`{e}`",
        IEnumerable items and not string => string.Join(", ", items.Cast<object>().Select(i => $"`{i}`")),
        _ => $"`{value}`",
    };
}

static string Text(string? s) => (s ?? "").Replace("<", "&lt;").Replace(">", "&gt;").Trim();

static string Cell(string? s) => Text(s).Replace("|", "\\|").Replace("\r", "").Replace("\n", "<br>");

static string Yaml(string s) => "\"" + s.Replace("\\", "\\\\").Replace("\"", "\\\"").Trim() + "\"";

partial class Program
{
    const string Generated =
        ":::note\nGenerated from the command definitions by `tools/Deployify.DocGen`; don't edit by hand. "
        + "Run `dotnet run --project tools/Deployify.DocGen` after changing a command.\n:::";
}
