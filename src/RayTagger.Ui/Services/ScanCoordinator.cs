using Microsoft.Extensions.Logging;
using RayTagger.Core.Configuration;
using RayTagger.Core.Mapping;
using RayTagger.Core.Models;
using RayTagger.Core.Pipeline;

namespace RayTagger.Ui.Services;

/// <summary>
/// Wraps <see cref="ITagPipeline"/>-equivalent wiring for the UI side. Given a source directory,
/// builds a one-shot scan, streams <see cref="PipelineOutcome"/>s back to the caller, and tears
/// down the per-scan resources when done. UI view-models bind to the outcome stream rather than
/// instantiating pipeline parts directly.
/// </summary>
/// <remarks>
/// Phase-6 scope is "show what the pipeline would do", so analyzers and lookup providers are
/// disabled at this stage. The pipeline still reads existing tags, evaluates mapping rules
/// (when a rules file is present), and runs the sort engine in dry-run — enough to demonstrate
/// the diff view. Wiring analyzers + providers happens once the UI has settings panels for them.
/// </remarks>
public sealed class ScanCoordinator
{
    private readonly IFileDiscoveryService _discovery;
    private readonly ITagReaderAdapter _reader;
    private readonly ITagWriterAdapter _writer;
    private readonly IMappingRuleEngine _ruleEngine;
    private readonly ISortService _sortService;
    private readonly ILogger<ScanCoordinator> _logger;
    private readonly ILoggerFactory _loggerFactory;

    public ScanCoordinator(
        IFileDiscoveryService discovery,
        ITagReaderAdapter reader,
        ITagWriterAdapter writer,
        IMappingRuleEngine ruleEngine,
        ISortService sortService,
        ILogger<ScanCoordinator> logger,
        ILoggerFactory loggerFactory)
    {
        ArgumentNullException.ThrowIfNull(discovery);
        ArgumentNullException.ThrowIfNull(reader);
        ArgumentNullException.ThrowIfNull(writer);
        ArgumentNullException.ThrowIfNull(ruleEngine);
        ArgumentNullException.ThrowIfNull(sortService);
        ArgumentNullException.ThrowIfNull(logger);
        ArgumentNullException.ThrowIfNull(loggerFactory);

        _discovery = discovery;
        _reader = reader;
        _writer = writer;
        _ruleEngine = ruleEngine;
        _sortService = sortService;
        _logger = logger;
        _loggerFactory = loggerFactory;
    }

    /// <summary>
    /// Runs a dry-run scan over <paramref name="sourceDirectory"/>. Analyzers and online lookup
    /// stay off — the UI gets a "what would the pipeline currently propose" view based on
    /// existing tags + mapping rules + sort plan.
    /// </summary>
    public IAsyncEnumerable<PipelineOutcome> ScanAsync(
        string sourceDirectory,
        MappingRuleSet? rules = null,
        int parallelism = 4,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sourceDirectory);

        var options = new TaggerOptions
        {
            Scan = new ScanOptions
            {
                Source = sourceDirectory,
                Recursive = true,
                Parallelism = parallelism,
            },
            Write = new WriteOptions { DryRun = true, Backup = false },
        };

        var pipeline = new TagPipeline(
            _discovery,
            _reader,
            _writer,
            NoopAnalysisRunner.Instance,
            NoopLookupRunner.Instance,
            _ruleEngine,
            _sortService,
            _loggerFactory.CreateLogger<TagPipeline>());

        _logger.LogInformation("Starting UI scan of {Source}", sourceDirectory);
        return pipeline.RunAsync(options, rules ?? new MappingRuleSet(), cancellationToken);
    }
}
