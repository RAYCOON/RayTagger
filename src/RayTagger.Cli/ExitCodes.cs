namespace RayTagger.Cli;

/// <summary>
/// Process exit codes used across the CLI. Conventional Unix-style: 0 = success, 1 = generic
/// runtime failure, 2 = bad invocation / config error, 64 onwards for domain errors (none yet).
/// </summary>
internal static class ExitCodes
{
    public const int Success = 0;
    public const int GenericFailure = 1;
    public const int InvalidConfiguration = 2;
    public const int InvalidArguments = 64;
}
