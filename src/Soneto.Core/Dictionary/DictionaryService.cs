using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging;

namespace Soneto.Core.Dictionary;

/// <summary>
/// File-backed <see cref="IDictionaryService"/> implementation. Mirrors
/// <see cref="Soneto.Core.Configuration.ConfigService"/>'s established, twice-reviewed shape
/// closely (Phase 2 plan §2.7's own recommendation): same 500ms debounce
/// <see cref="Timer"/>, same <c>FileSystemWatcher</c> wiring, same "never throws, keep the
/// previous good state on any failure" contract, and the same <c>_disposed</c>-flag-flipped-
/// under-the-same-lock-that-guards-the-timer pattern that closed a real Dispose/timer race in
/// <c>ConfigService</c>'s own review history.
///
/// <para>
/// <b>Per-entry error isolation, per <see cref="DictionaryDocument"/>'s own doc-comment
/// instruction:</b> unlike <c>ConfigService</c>'s single whole-document
/// <c>JsonSerializer.Deserialize</c> call, this class parses the top-level JSON document only
/// far enough to pull out the raw <c>entries</c> array as individual <see cref="JsonElement"/>s,
/// then deserializes each element ONE AT A TIME inside its own try/catch -- one malformed entry
/// is logged and skipped (by index, and by Id if the element could at least be parsed as a bare
/// JSON object far enough to read <c>id</c>) without failing the rest of the file.
/// </para>
///
/// <para>
/// <b>Validation order, per §2.7 (each check documented at its own call site below):</b>
/// <list type="number">
/// <item>Parse the document shell (<c>schemaVersion</c> + raw <c>entries</c> array). A
/// completely malformed JSON document (not even an object, or no <c>entries</c> array) fails
/// the WHOLE load -- there's nothing per-entry to isolate at that point.</item>
/// <item>Deserialize each element of <c>entries</c> individually; a single bad element is
/// skipped (logged), the rest proceed to per-entry validation below.</item>
/// <item>Per-entry validation: an enabled <see cref="RegexRule.Pattern"/> that fails to compile,
/// or a <see cref="CorrectionPair.From"/>/<see cref="SpokenCommand.Phrase"/> that is empty or
/// whitespace-only, rejects that ONE entry (logged), the rest of the file still applies.</item>
/// <item>Whole-file validation: duplicate <see cref="DictionaryEntry.Id"/> values across ALL
/// entries that survived steps 2-3 reject the WHOLE file (ambiguous <c>AppliedRule</c>
/// correlation otherwise) -- the previous good <see cref="Current"/> is retained and no entries
/// from this load are applied, even the individually-valid ones.</item>
/// </list>
/// </para>
///
/// <para>
/// <b>First run (item 10, updated from item 9):</b> now that <see cref="SeedDictionary"/> exists,
/// a missing <c>dictionary.json</c> writes the embedded seed dictionary to
/// <see cref="DictionaryPath"/> -- exactly <c>ConfigService</c>'s own "write defaults on first
/// run" behavior, which item 9's own doc comment had deliberately deferred pending this item.
/// The written (or, if the write itself fails -- e.g. a permission-denied directory -- the
/// purely in-memory) seed JSON is then run through the SAME parse/validate pipeline as any other
/// load below, so the seed dictionary is genuinely round-tripped through real deserialization and
/// validation rather than trusted blindly. Per <c>ConfigService.LoadAsync</c>'s own precedent,
/// this first-run load does NOT raise <see cref="DictionaryChanged"/> (only a genuine SUBSEQUENT
/// reload does) -- there is no previous <see cref="Current"/> for a caller to meaningfully react
/// to changing away from.
/// </para>
///
/// <para>
/// <b>Collision warnings</b> (item 8's <see cref="DictionaryCollisionWarnings"/>) run on every
/// successful load/reload, against the full validated entry set -- this is that class's intended
/// call site, per its own doc comment.
/// </para>
/// </summary>
public sealed class DictionaryService : IDictionaryService, IDisposable
{
    private const int DebounceMs = 500;

    private readonly ILogger<DictionaryService> _logger;
    private readonly object _gate = new();
    private readonly JsonSerializerOptions _jsonOptions = DictionaryJsonOptions.Create();

    private DictionaryConfig _current = DictionaryConfig.Empty;
    private FileSystemWatcher? _watcher;
    private Timer? _debounceTimer;
    private bool _disposed;

    public DictionaryService(ILogger<DictionaryService> logger, string dictionaryPath)
    {
        _logger = logger;
        DictionaryPath = dictionaryPath;
    }

    public string DictionaryPath { get; }

    public DictionaryConfig Current
    {
        get { lock (_gate) return _current; }
    }

    public event EventHandler<DictionaryChangedEventArgs>? DictionaryChanged;

    public async Task<bool> LoadAsync(CancellationToken ct = default)
    {
        var isFirstRun = !File.Exists(DictionaryPath);
        string json;

        if (isFirstRun)
        {
            // Item 10: write the embedded seed dictionary as dictionary.json's default content
            // (ConfigService parity -- see class doc comment's "First run" paragraph). The
            // written (or, on a write failure, purely in-memory) seed JSON still falls through
            // to the exact same parse/validate pipeline below as any other load.
            _logger.LogInformation(
                "Dictionary file not found at {DictionaryPath}; writing the seed dictionary as defaults",
                DictionaryPath);

            json = SeedDictionary.Json;
            try
            {
                var dir = Path.GetDirectoryName(DictionaryPath);
                if (!string.IsNullOrEmpty(dir))
                    Directory.CreateDirectory(dir);

                await File.WriteAllTextAsync(DictionaryPath, json, ct);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // LoadAsync's contract is "never throws" -- a permission-denied dictionary
                // dir/AV lock etc. must never propagate. Fall back to the in-memory seed JSON
                // only and let the daemon keep starting, mirroring ConfigService's own fallback.
                _logger.LogError(ex,
                    "Failed to write seed dictionary to {DictionaryPath}; using the in-memory seed dictionary only",
                    DictionaryPath);
            }
        }
        else
        {
            try
            {
                json = await File.ReadAllTextAsync(DictionaryPath, ct);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                _logger.LogError(ex,
                    "Failed to read dictionary file at {DictionaryPath}; keeping previous dictionary",
                    DictionaryPath);
                return false;
            }
        }

        int schemaVersion;
        List<JsonElement> rawEntries;
        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;

            schemaVersion = root.TryGetProperty("schemaVersion", out var sv) && sv.ValueKind == JsonValueKind.Number
                ? sv.GetInt32()
                : 1;

            rawEntries = [];
            if (root.TryGetProperty("entries", out var entriesElement) &&
                entriesElement.ValueKind == JsonValueKind.Array)
            {
                foreach (var element in entriesElement.EnumerateArray())
                    rawEntries.Add(element.Clone());
            }
        }
        catch (JsonException ex)
        {
            _logger.LogError(ex,
                "Invalid dictionary JSON at {DictionaryPath}; keeping previous dictionary",
                DictionaryPath);
            return false;
        }

        var validEntries = new List<DictionaryEntry>();
        var rejected = new List<RejectedDictionaryEntry>();

        for (var index = 0; index < rawEntries.Count; index++)
        {
            var element = rawEntries[index];
            string? id = element.ValueKind == JsonValueKind.Object &&
                         element.TryGetProperty("id", out var idProp) &&
                         idProp.ValueKind == JsonValueKind.String
                ? idProp.GetString()
                : null;

            DictionaryEntry? entry;
            try
            {
                entry = element.Deserialize<DictionaryEntry>(_jsonOptions);
            }
            catch (JsonException ex)
            {
                _logger.LogError(ex,
                    "Skipping malformed dictionary entry at index {Index} (id: {Id}) in {DictionaryPath}: {Message}",
                    index, id ?? "<unknown>", DictionaryPath, ex.Message);
                rejected.Add(new RejectedDictionaryEntry(index, id, $"Malformed JSON: {ex.Message}"));
                continue;
            }

            if (entry is null)
            {
                _logger.LogError(
                    "Skipping dictionary entry at index {Index} in {DictionaryPath}: parsed to null",
                    index, DictionaryPath);
                rejected.Add(new RejectedDictionaryEntry(index, id, "Entry parsed to null"));
                continue;
            }

            // Per-entry validation (§2.7): an entry that fails ANY of these checks is
            // rejected individually -- the rest of the file still applies.
            if (entry.Enabled && entry is RegexRule regexRule)
            {
                try
                {
                    _ = new Regex(regexRule.Pattern);
                }
                catch (RegexParseException ex)
                {
                    _logger.LogError(ex,
                        "Rejecting RegexRule entry {EntryId} at index {Index} in {DictionaryPath}: pattern \"{Pattern}\" does not compile: {Message}",
                        entry.Id, index, DictionaryPath, regexRule.Pattern, ex.Message);
                    rejected.Add(new RejectedDictionaryEntry(
                        index, entry.Id, $"Invalid regex pattern \"{regexRule.Pattern}\": {ex.Message}"));
                    continue;
                }
            }

            var (blankField, blankValue) = entry switch
            {
                CorrectionPair pair => ("From", pair.From),
                SpokenCommand command => ("Phrase", command.Phrase),
                _ => (null, null),
            };

            if (blankField is not null && string.IsNullOrWhiteSpace(blankValue))
            {
                _logger.LogError(
                    "Rejecting {EntryType} entry {EntryId} at index {Index} in {DictionaryPath}: {Field} is empty or whitespace-only",
                    entry.GetType().Name, entry.Id, index, DictionaryPath, blankField);
                rejected.Add(new RejectedDictionaryEntry(
                    index, entry.Id, $"{blankField} is empty or whitespace-only"));
                continue;
            }

            validEntries.Add(entry);
        }

        // Whole-file validation (§2.7): duplicate Ids across everything that survived
        // per-entry validation reject the WHOLE file -- ambiguous AppliedRule correlation
        // otherwise. The previous good Current is retained.
        var duplicateIds = validEntries
            .GroupBy(e => e.Id, StringComparer.Ordinal)
            .Where(g => g.Count() > 1)
            .Select(g => g.Key)
            .ToList();

        if (duplicateIds.Count > 0)
        {
            _logger.LogError(
                "Rejecting the whole dictionary file at {DictionaryPath}: duplicate entry Ids found: {DuplicateIds}. Keeping previous dictionary.",
                DictionaryPath, string.Join(", ", duplicateIds));
            return false;
        }

        var config = new DictionaryConfig(validEntries, rejected, schemaVersion);

        // Item 8's advisory collision-warning hook -- runs on every successful load/reload,
        // against the full validated entry set.
        DictionaryCollisionWarnings.Check(validEntries, _logger);

        // First-run load never raises DictionaryChanged -- same ConfigService precedent noted
        // in the class doc comment's "First run" paragraph; only a genuine subsequent reload does.
        SetCurrent(config, raiseEvent: !isFirstRun);
        _logger.LogInformation(
            "Dictionary loaded from {DictionaryPath}: {EntryCount} entries ({RejectedCount} rejected)",
            DictionaryPath, validEntries.Count, rejected.Count);
        return true;
    }

    public void StartWatching()
    {
        if (_watcher is not null)
            return;

        var dir = Path.GetDirectoryName(DictionaryPath);
        var file = Path.GetFileName(DictionaryPath);
        if (string.IsNullOrEmpty(dir) || string.IsNullOrEmpty(file))
        {
            _logger.LogWarning(
                "Cannot watch dictionary path {DictionaryPath}: could not resolve directory/file name",
                DictionaryPath);
            return;
        }

        try
        {
            Directory.CreateDirectory(dir);

            var watcher = new FileSystemWatcher(dir, file)
            {
                NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.Size | NotifyFilters.CreationTime,
            };
            watcher.Changed += OnFileEvent;
            watcher.Created += OnFileEvent;
            watcher.Renamed += OnFileEvent;
            watcher.Error += OnWatcherError;
            watcher.EnableRaisingEvents = true;

            _watcher = watcher;
            _logger.LogInformation("Watching {DictionaryPath} for changes", DictionaryPath);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Hot-reload is a nice-to-have, not a startup requirement -- never let a
            // watcher construction failure (permission-denied dir, etc.) take the daemon
            // down. Dictionary loading itself is unaffected.
            _logger.LogError(ex,
                "Failed to start watching {DictionaryPath} for changes; hot-reload disabled",
                DictionaryPath);
        }
    }

    public void StopWatching()
    {
        if (_watcher is null)
            return;

        _watcher.EnableRaisingEvents = false;
        _watcher.Changed -= OnFileEvent;
        _watcher.Created -= OnFileEvent;
        _watcher.Renamed -= OnFileEvent;
        _watcher.Error -= OnWatcherError;
        _watcher.Dispose();
        _watcher = null;
    }

    private void OnWatcherError(object sender, ErrorEventArgs e)
    {
        // The watcher's internal buffer can overflow under rapid writes/directory churn, at
        // which point it silently stops delivering change notifications -- warn loudly, same
        // as ConfigService's own fix for this.
        _logger.LogWarning(e.GetException(),
            "Dictionary file watcher for {DictionaryPath} reported an error; hot-reload may stop working until the daemon restarts",
            DictionaryPath);
    }

    private void OnFileEvent(object sender, FileSystemEventArgs e)
    {
        lock (_gate)
        {
            if (_disposed)
                return;

            _debounceTimer?.Dispose();
            _debounceTimer = new Timer(_ => _ = OnDebounceElapsedAsync(), null, DebounceMs, Timeout.Infinite);
        }
    }

    private async Task OnDebounceElapsedAsync()
    {
        try
        {
            var reloaded = await LoadAsync();
            if (reloaded)
                _logger.LogInformation("Dictionary hot-reloaded from {DictionaryPath}", DictionaryPath);
        }
        catch (Exception ex)
        {
            // Defensive: LoadAsync itself never throws, but this runs on a bare Timer
            // callback thread with no other error boundary above it.
            _logger.LogError(ex, "Unexpected error during dictionary hot-reload");
        }
    }

    private void SetCurrent(DictionaryConfig config, bool raiseEvent)
    {
        lock (_gate)
            _current = config;

        if (raiseEvent)
            DictionaryChanged?.Invoke(this, new DictionaryChangedEventArgs { Config = config });
    }

    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed)
                return;

            _disposed = true;
            _debounceTimer?.Dispose();
            _debounceTimer = null;
        }

        // Outside the lock, mirroring ConfigService.Dispose()'s established race-closing
        // pattern: StopWatching() unsubscribes/disposes the FileSystemWatcher independently
        // of _gate, and _disposed is flipped under the SAME lock that guards the timer field,
        // so any OnFileEvent call that acquires _gate after this point sees _disposed and
        // returns before touching the timer at all.
        StopWatching();
    }
}
