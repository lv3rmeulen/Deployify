using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Deployify.Core.Processes;

namespace Deployify.Tests.Shared;

/// <summary>
/// Records every process a component would start and answers with canned results, so tests
/// can check the exact argument list without the tool installed.
/// </summary>
internal sealed class FakeProcessRunner : IProcessRunner
{
    private readonly List<(Func<ProcessSpec, bool> Match, Func<ProcessSpec, ProcessResult> Respond)> _rules = [];

    public List<ProcessSpec> Calls { get; } = [];

    /// <summary>Result for calls that no rule matches.</summary>
    public ProcessResult Default { get; set; } = new(0, "", "");

    /// <summary>Programs that behave as if they weren't installed.</summary>
    public HashSet<string> Missing { get; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Answers calls whose arguments contain <paramref name="argsSequence"/> in order.</summary>
    public FakeProcessRunner When(string[] argsSequence, ProcessResult result)
        => When(spec => ContainsSequence(spec.Args, argsSequence), _ => result);

    public FakeProcessRunner When(Func<ProcessSpec, bool> match, Func<ProcessSpec, ProcessResult> respond)
    {
        _rules.Add((match, respond));
        return this;
    }

    public Task<ProcessResult> RunAsync(ProcessSpec spec, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        Calls.Add(spec);
        if (Missing.Contains(spec.FileName))
        {
            throw new ToolNotFoundException(spec.FileName);
        }

        // Later rules win, so a test can override a fixture's defaults.
        for (var i = _rules.Count - 1; i >= 0; i--)
        {
            if (_rules[i].Match(spec))
            {
                return Task.FromResult(_rules[i].Respond(spec));
            }
        }

        return Task.FromResult(Default);
    }

    public ProcessSpec Single(params string[] argsSequence)
        => Calls.Single(c => ContainsSequence(c.Args, argsSequence));

    private static bool ContainsSequence(IReadOnlyList<string> args, string[] seq)
    {
        for (var i = 0; i + seq.Length <= args.Count; i++)
        {
            if (seq.Select((s, j) => args[i + j] == s).All(x => x))
            {
                return true;
            }
        }

        return false;
    }
}
