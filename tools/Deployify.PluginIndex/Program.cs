using Deployify.Plugins;

// dotnet run --project tools/Deployify.PluginIndex -- <entries folder> <output folder> [--valid-days N]
if (args.Length < 2)
{
    Console.Error.WriteLine("Usage: Deployify.PluginIndex <entries folder> <output folder> [--valid-days N]");
    return 2;
}

var validity = PluginIndexBuilder.DefaultValidity;
var days = Array.IndexOf(args, "--valid-days");
if (days >= 0)
{
    if (days + 1 >= args.Length || !int.TryParse(args[days + 1], out var n) || n <= 0)
    {
        Console.Error.WriteLine("--valid-days needs a positive number of days.");
        return 2;
    }

    validity = TimeSpan.FromDays(n);
}

try
{
    var result = await new PluginIndexBuilder().BuildAsync(args[0], validity);
    foreach (var note in result.Notes)
    {
        Console.WriteLine($"note: {note}");
    }

    foreach (var error in result.Errors)
    {
        // GitHub Actions shows these as annotations on the run.
        Console.WriteLine($"::error::{error}");
    }

    if (result.Errors.Count > 0)
    {
        Console.Error.WriteLine($"{result.Errors.Count} problem(s); index.json was not written.");
        return 1;
    }

    Directory.CreateDirectory(args[1]);
    var output = Path.Combine(args[1], "index.json");
    await File.WriteAllTextAsync(output, result.Index.Serialize());
    Console.WriteLine(
        $"Wrote {output}: {result.Index.Plugins.Count} plugin(s), " +
        $"{result.Index.Plugins.Values.Sum(p => p.Versions.Count)} version(s), valid until {result.Index.Expires:yyyy-MM-dd}.");
    return 0;
}
catch (PluginException ex)
{
    Console.Error.WriteLine(ex.Message);
    return 1;
}
