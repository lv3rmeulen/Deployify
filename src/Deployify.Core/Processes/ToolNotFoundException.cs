using System;

namespace Deployify.Core.Processes;

/// <summary>The program isn't installed, or isn't on PATH.</summary>
public sealed class ToolNotFoundException(string tool, Exception? inner = null)
    : InvalidOperationException($"'{tool}' was not found. Install it or make sure it is on PATH.", inner)
{
    public string Tool { get; } = tool;
}
