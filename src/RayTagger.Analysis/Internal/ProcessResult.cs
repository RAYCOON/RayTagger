namespace RayTagger.Analysis.Internal;

/// <summary>Outcome of one native-tool invocation.</summary>
public sealed record ProcessResult(
    int ExitCode,
    string StandardOutput,
    string StandardError,
    TimeSpan Duration)
{
    public bool Succeeded => ExitCode == 0;
}
