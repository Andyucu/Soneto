using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Text.Json;
using Avalonia.Threading;
using Soneto.Core.Dictionary;

namespace Soneto.App.ViewModels;

/// <summary>
/// Which of the five <see cref="DictionaryEntry"/> subtypes a row/edit form is for. A plain
/// enum (not a general-purpose type-registry) is enough per §3.11's own "5 fixed types, don't
/// over-engineer" precedent (same call item 4's nav-rail shell already made for its 4 fixed
/// destinations).
/// </summary>
public enum DictionaryEntryKind
{
    VocabularyTerm,
    CorrectionPair,
    RegexRule,
    SpokenCommand,
    PerAppOverride,
}

/// <summary>
/// A single row in the dictionary list — a thin, read-only presentation wrapper around a real
/// <see cref="DictionaryEntry"/>, computing a human-readable type label/summary for display.
/// </summary>
public sealed class DictionaryEntryRowViewModel
{
    public DictionaryEntryRowViewModel(DictionaryEntry entry) => Entry = entry;

    public DictionaryEntry Entry { get; }

    public string Id => Entry.Id;

    public bool Enabled => Entry.Enabled;

    /// <summary>Precomputed label for the row's toggle button, since this project's hand-rolled
    /// binding style avoids adding a value-converter just for "Enable"/"Disable" text.</summary>
    public string ToggleLabel => Enabled ? "Disable" : "Enable";

    public DictionaryEntryKind Kind => DictionaryEditorViewModel.KindOf(Entry);

    public string TypeLabel => Entry switch
    {
        VocabularyTerm => "Vocabulary term",
        CorrectionPair => "Correction",
        RegexRule => "Regex rule",
        SpokenCommand => "Spoken command",
        PerAppOverride => "Per-app override (not yet active)",
        _ => Entry.GetType().Name,
    };

    public string Summary => Entry switch
    {
        VocabularyTerm v => v.Term,
        CorrectionPair c => $"{c.From} -> {c.To}",
        RegexRule r => $"{r.Pattern} -> {r.Replacement}",
        SpokenCommand s => $"\"{s.Phrase}\" -> {EscapeControlChars(s.Emits)}",
        PerAppOverride p => p.ProcessName,
        _ => string.Empty,
    };

    private static string EscapeControlChars(string s) =>
        s.Replace("\n", "\\n").Replace("\r", "\\r").Replace("\t", "\\t");
}

/// <summary>
/// Phase 3 item 7 (§3.11) — the Dictionary editor ViewModel. Hand-rolled
/// <see cref="INotifyPropertyChanged"/>, same style as <see cref="HistoryViewModel"/> (this
/// project has no reactive-UI/MVVM package pinned).
///
/// <para>
/// <b>Reads happen at construction, synchronously</b> — by the time this class is constructed,
/// <c>App.axaml.cs</c>'s composition root has ALREADY awaited
/// <c>DaemonComposition.LoadAndStartWatchingDictionaryAsync</c> (see that file's own doc
/// comment for the "block briefly on the dictionary load, unlike PipelineHost's ASR model
/// load" architecture decision), so <see cref="IDictionaryService.Current"/> already reflects
/// dictionary.json's real, on-disk content the first time this constructor runs — no loading
/// spinner/empty-then-catch-up state is needed.
/// </para>
///
/// <para>
/// <b>The entire write path</b> (per this item's own explicit "ZERO new watch-side code"
/// instruction): construct the appropriately-typed <see cref="DictionaryEntry"/>, merge it
/// into (or remove it from) a copy of <see cref="IDictionaryService.Current"/>'s entries,
/// serialize as a <see cref="DictionaryDocument"/> via <see cref="DictionaryJsonOptions"/>, and
/// write it to <see cref="IDictionaryService.DictionaryPath"/>. Nothing here talks to a
/// <see cref="System.IO.FileSystemWatcher"/> or reload timer directly — the already-running
/// <see cref="IDictionaryService.StartWatching"/> (started once, eagerly, in
/// <c>App.axaml.cs</c>) picks this exact write up automatically via its existing
/// 500ms-debounced watcher.
/// </para>
///
/// <para>
/// <b>Surfacing validation failures</b> (§3.11's one genuinely new requirement): after writing,
/// <see cref="UpsertAsync"/>/<see cref="DeleteAsync"/> wait for the NEXT
/// <see cref="IDictionaryService.DictionaryChanged"/> event (or a timeout comfortably longer
/// than the watcher's 500ms debounce, <see cref="DefaultSettleTimeout"/>), then inspect
/// <see cref="IDictionaryService.Current"/>:
/// <list type="bullet">
/// <item>the written entry is present with the expected shape → success;</item>
/// <item>absent, but <see cref="DictionaryConfig.RejectedEntries"/> has an entry for the same
/// Id → that rejection's <see cref="RejectedDictionaryEntry.Reason"/> is shown verbatim;</item>
/// <item>absent, no matching rejection either, and the event NEVER fired within the timeout →
/// per <see cref="DictionaryConfig.RejectedEntries"/>'s own doc comment ("Empty when the whole
/// file was rejected outright... there is no partial entry list to report on"), a real
/// <see cref="DictionaryService"/> NEVER raises <see cref="IDictionaryService.DictionaryChanged"/>
/// for a whole-file duplicate-Id rejection — so a settle timeout with no event at all IS the
/// expected shape of exactly that failure, and is reported as such rather than a vague
/// "still pending" message;</item>
/// <item>absent, no matching rejection, but the event DID still fire (a rare, defensive
/// fallback the real service should never actually produce) → a generic "didn't apply" message,
/// so this case is never silently swallowed even though it isn't expected to occur.</item>
/// </list>
/// </para>
///
/// <para>
/// <b>Concurrency — the one race this item's own instructions explicitly asked to check for,
/// verified by inspection, not just asserted:</b> could a <see cref="DictionaryChanged"/>-driven
/// list refresh (<see cref="RefreshFromCurrent"/>, subscribed for the whole ViewModel's
/// lifetime so a hand-edit or another write shows up live) clobber a write-then-verify flow's
/// own outcome? No: <see cref="RefreshFromCurrent"/> only ever repopulates
/// <see cref="Entries"/>/<see cref="RejectedEntries"/> from a fresh read of
/// <see cref="IDictionaryService.Current"/> — it never touches <see cref="StatusMessage"/>, and
/// <see cref="WriteAndVerifyAsync"/> computes its own outcome from its OWN fresh read of
/// <see cref="IDictionaryService.Current"/> after its wait settles, regardless of which handler
/// (the temporary settle-waiter or the permanent live-refresh subscriber) actually observed the
/// event first. The other real risk — two concurrent writes each racing to attribute the SAME
/// next <see cref="DictionaryChanged"/> event to their own outcome — is closed by
/// <see cref="_writeGate"/>, a <see cref="SemaphoreSlim"/> serializing the whole
/// write-then-verify sequence so only one such flow is ever in flight at a time.
/// </para>
/// </summary>
public sealed class DictionaryEditorViewModel : INotifyPropertyChanged, IDisposable
{
    /// <summary>500ms watcher debounce + generous buffer for the reload itself to complete.</summary>
    public static readonly TimeSpan DefaultSettleTimeout = TimeSpan.FromMilliseconds(1500);

    private readonly IDictionaryService _dictionaryService;
    private readonly Action<Action> _postToUiThread;
    private readonly TimeSpan _settleTimeout;
    private readonly SemaphoreSlim _writeGate = new(1, 1);

    private List<DictionaryEntryRowViewModel> _allRows = [];
    private DictionaryEntryKind? _typeFilter;
    private string? _statusMessage;
    private bool _statusIsError;
    private bool _isSaving;
    private bool _isEditing;
    private string? _editingId;
    private DictionaryEntryKind _editingKind;
    private bool _editingEnabled = true;
    private string _editingTerm = string.Empty;
    private string _editingFrom = string.Empty;
    private string _editingTo = string.Empty;
    private string _editingPattern = string.Empty;
    private string _editingReplacement = string.Empty;
    private string _editingPhrase = string.Empty;
    private string _editingEmits = string.Empty;
    private string _editingProcessName = string.Empty;
    private bool _editingAutoCapitalize = true;
    private bool _editingTrailingPunctuation = true;

    public event PropertyChangedEventHandler? PropertyChanged;

    /// <summary>Currently-displayed rows (post type-filter). Newest-write-first is not
    /// meaningful here (unlike History) — kept in <see cref="IDictionaryService.Current"/>'s
    /// own entry order.</summary>
    public ObservableCollection<DictionaryEntryRowViewModel> Entries { get; } = new();

    /// <summary>Diagnostics from the last successful load — surfaced directly per §3.11
    /// (this is exactly what that section's own note says needs no <c>IDictionaryService</c>
    /// change: <see cref="DictionaryConfig.RejectedEntries"/> already carries everything).</summary>
    public IReadOnlyList<RejectedDictionaryEntry> RejectedEntries => _dictionaryService.Current.RejectedEntries;

    public DictionaryEntryKind? TypeFilter
    {
        get => _typeFilter;
        set
        {
            if (_typeFilter == value)
                return;

            _typeFilter = value;
            OnPropertyChanged();
            ApplyFilter();
        }
    }

    /// <summary>Result of the last add/edit/delete/toggle write, for the UI to display. Null
    /// until the first write attempt.</summary>
    public string? StatusMessage
    {
        get => _statusMessage;
        private set { _statusMessage = value; OnPropertyChanged(); }
    }

    public bool StatusIsError
    {
        get => _statusIsError;
        private set { _statusIsError = value; OnPropertyChanged(); }
    }

    /// <summary>
    /// True while an add/edit/delete/toggle write is in flight (post-review addition). The
    /// <see cref="_writeGate"/> semaphore already makes concurrent writes CORRECT (a second
    /// write just queues behind the first, each computing its own outcome independently) — this
    /// property exists purely for UX clarity, so the view can disable Save/Delete/Toggle buttons
    /// and show that something is happening during the ~150ms-1.5s write-then-settle wait,
    /// rather than giving a rapid double-click no visual feedback at all.
    /// </summary>
    public bool IsSaving
    {
        get => _isSaving;
        private set { _isSaving = value; OnPropertyChanged(); }
    }

    /// <summary>True while the add/edit form is open.</summary>
    public bool IsEditing
    {
        get => _isEditing;
        set { _isEditing = value; OnPropertyChanged(); }
    }

    /// <summary>Null while adding a NEW entry; the existing entry's Id while editing one.</summary>
    public string? EditingId
    {
        get => _editingId;
        set
        {
            _editingId = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(EditorTitle));
        }
    }

    public string EditorTitle => EditingId is null ? "Add entry" : $"Edit entry ({EditingId})";

    public DictionaryEntryKind EditingKind
    {
        get => _editingKind;
        set
        {
            _editingKind = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(IsEditingVocabularyTerm));
            OnPropertyChanged(nameof(IsEditingCorrectionPair));
            OnPropertyChanged(nameof(IsEditingRegexRule));
            OnPropertyChanged(nameof(IsEditingSpokenCommand));
            OnPropertyChanged(nameof(IsEditingPerAppOverride));
        }
    }

    // Computed, per-shape visibility flags for the add/edit form's XAML (this project's
    // established style avoids adding a value-converter for a simple enum-equality check —
    // see e.g. HistoryView's own Classes.Highlighted bool-binding precedent).
    public bool IsEditingVocabularyTerm => EditingKind == DictionaryEntryKind.VocabularyTerm;
    public bool IsEditingCorrectionPair => EditingKind == DictionaryEntryKind.CorrectionPair;
    public bool IsEditingRegexRule => EditingKind == DictionaryEntryKind.RegexRule;
    public bool IsEditingSpokenCommand => EditingKind == DictionaryEntryKind.SpokenCommand;
    public bool IsEditingPerAppOverride => EditingKind == DictionaryEntryKind.PerAppOverride;

    public bool EditingEnabled
    {
        get => _editingEnabled;
        set { _editingEnabled = value; OnPropertyChanged(); }
    }

    public string EditingTerm
    {
        get => _editingTerm;
        set { _editingTerm = value; OnPropertyChanged(); }
    }

    public string EditingFrom
    {
        get => _editingFrom;
        set { _editingFrom = value; OnPropertyChanged(); }
    }

    public string EditingTo
    {
        get => _editingTo;
        set { _editingTo = value; OnPropertyChanged(); }
    }

    public string EditingPattern
    {
        get => _editingPattern;
        set { _editingPattern = value; OnPropertyChanged(); }
    }

    public string EditingReplacement
    {
        get => _editingReplacement;
        set { _editingReplacement = value; OnPropertyChanged(); }
    }

    public string EditingPhrase
    {
        get => _editingPhrase;
        set { _editingPhrase = value; OnPropertyChanged(); }
    }

    public string EditingEmits
    {
        get => _editingEmits;
        set { _editingEmits = value; OnPropertyChanged(); }
    }

    public string EditingProcessName
    {
        get => _editingProcessName;
        set { _editingProcessName = value; OnPropertyChanged(); }
    }

    public bool EditingAutoCapitalize
    {
        get => _editingAutoCapitalize;
        set { _editingAutoCapitalize = value; OnPropertyChanged(); }
    }

    public bool EditingTrailingPunctuation
    {
        get => _editingTrailingPunctuation;
        set { _editingTrailingPunctuation = value; OnPropertyChanged(); }
    }

    /// <summary>Real, production constructor.</summary>
    public DictionaryEditorViewModel(IDictionaryService dictionaryService)
        : this(dictionaryService, uiThreadPost: null, settleTimeout: null)
    {
    }

    /// <summary>
    /// Test-facing constructor (internal, mirroring <see cref="HistoryViewModel"/>'s own
    /// established pattern): <paramref name="uiThreadPost"/> replaces
    /// <see cref="Dispatcher.UIThread"/> marshaling with a synchronous stand-in, and
    /// <paramref name="settleTimeout"/> lets a test use a short window instead of
    /// <see cref="DefaultSettleTimeout"/>'s real 1.5s.
    /// </summary>
    internal DictionaryEditorViewModel(
        IDictionaryService dictionaryService, Action<Action>? uiThreadPost, TimeSpan? settleTimeout)
    {
        _dictionaryService = dictionaryService ?? throw new ArgumentNullException(nameof(dictionaryService));
        _postToUiThread = uiThreadPost ?? (action => Dispatcher.UIThread.Post(action));
        _settleTimeout = settleTimeout ?? DefaultSettleTimeout;

        RefreshFromCurrent();
        _dictionaryService.DictionaryChanged += OnDictionaryChanged;
    }

    /// <summary>Opens the add form for a brand-new entry of <paramref name="kind"/>.</summary>
    public void BeginAddNew(DictionaryEntryKind kind)
    {
        EditingId = null;
        EditingKind = kind;
        EditingEnabled = true;
        EditingTerm = string.Empty;
        EditingFrom = string.Empty;
        EditingTo = string.Empty;
        EditingPattern = string.Empty;
        EditingReplacement = string.Empty;
        EditingPhrase = string.Empty;
        EditingEmits = string.Empty;
        EditingProcessName = string.Empty;
        EditingAutoCapitalize = true;
        EditingTrailingPunctuation = true;
        IsEditing = true;
    }

    /// <summary>Opens the edit form pre-populated from an existing row.</summary>
    public void BeginEdit(DictionaryEntryRowViewModel row)
    {
        EditingId = row.Entry.Id;
        EditingKind = row.Kind;
        EditingEnabled = row.Entry.Enabled;

        switch (row.Entry)
        {
            case VocabularyTerm v:
                EditingTerm = v.Term;
                break;
            case CorrectionPair c:
                EditingFrom = c.From;
                EditingTo = c.To;
                break;
            case RegexRule r:
                EditingPattern = r.Pattern;
                EditingReplacement = r.Replacement;
                break;
            case SpokenCommand s:
                EditingPhrase = s.Phrase;
                EditingEmits = s.Emits;
                break;
            case PerAppOverride p:
                EditingProcessName = p.ProcessName;
                EditingAutoCapitalize = p.AutoCapitalize;
                EditingTrailingPunctuation = p.TrailingPunctuation;
                break;
        }

        IsEditing = true;
    }

    public void CancelEdit() => IsEditing = false;

    /// <summary>Builds the appropriately-typed entry from the current Editing* fields and
    /// writes it (add or edit, depending on <see cref="EditingId"/>).</summary>
    public async Task SaveAsync()
    {
        var id = EditingId ?? Guid.NewGuid().ToString();
        DictionaryEntry entry = EditingKind switch
        {
            DictionaryEntryKind.VocabularyTerm => new VocabularyTerm
            { Id = id, Enabled = EditingEnabled, Term = EditingTerm },
            DictionaryEntryKind.CorrectionPair => new CorrectionPair
            { Id = id, Enabled = EditingEnabled, From = EditingFrom, To = EditingTo },
            DictionaryEntryKind.RegexRule => new RegexRule
            { Id = id, Enabled = EditingEnabled, Pattern = EditingPattern, Replacement = EditingReplacement },
            DictionaryEntryKind.SpokenCommand => new SpokenCommand
            { Id = id, Enabled = EditingEnabled, Phrase = EditingPhrase, Emits = EditingEmits },
            DictionaryEntryKind.PerAppOverride => new PerAppOverride
            {
                Id = id, Enabled = EditingEnabled, ProcessName = EditingProcessName,
                AutoCapitalize = EditingAutoCapitalize, TrailingPunctuation = EditingTrailingPunctuation,
            },
            _ => throw new InvalidOperationException($"Unknown {nameof(DictionaryEntryKind)}: {EditingKind}"),
        };

        await UpsertAsync(entry);
        IsEditing = false;
    }

    /// <summary>Flips <see cref="DictionaryEntry.Enabled"/> for an existing row and writes it.</summary>
    public Task ToggleEnabledAsync(DictionaryEntryRowViewModel row) =>
        UpsertAsync(WithEnabled(row.Entry, !row.Entry.Enabled));

    public async Task DeleteAsync(DictionaryEntryRowViewModel row)
    {
        await _writeGate.WaitAsync();
        IsSaving = true;
        try
        {
            var newEntries = _dictionaryService.Current.Entries
                .Where(e => !string.Equals(e.Id, row.Entry.Id, StringComparison.Ordinal))
                .ToList();

            await WriteDocumentAsync(newEntries);
            var settled = await WaitForNextDictionaryChangedOrTimeoutAsync();

            var current = _dictionaryService.Current;
            var stillPresent = current.Entries.Any(e => string.Equals(e.Id, row.Entry.Id, StringComparison.Ordinal));

            if (!stillPresent)
            {
                SetStatus($"Deleted \"{row.Entry.Id}\".", isError: false);
            }
            else if (!settled)
            {
                // The real DictionaryService's whole-file duplicate-Id rejection NEVER raises
                // DictionaryChanged (see DictionaryConfig.RejectedEntries's own doc comment) --
                // so a settle timeout with no event at all is the expected shape of exactly
                // that failure, not merely "still pending". Named as such rather than left as a
                // vague "might still be pending" message.
                SetStatus(
                    "The delete did not apply, and no reload was observed — the dictionary file was most likely "
                    + "rejected as a whole (e.g. a duplicate Id elsewhere in the file). Check the app log.",
                    isError: true);
            }
            else
            {
                SetStatus(
                    "The delete did not apply, even though a reload was observed. Check the app log.",
                    isError: true);
            }
        }
        catch (Exception ex)
        {
            SetStatus($"Failed to write dictionary.json: {ex.Message}", isError: true);
        }
        finally
        {
            RefreshFromCurrent();
            IsSaving = false;
            _writeGate.Release();
        }
    }

    /// <summary>Add-or-replace-by-Id, write, then wait for and interpret the outcome. This is
    /// the entire write path this item builds -- see the class doc comment.</summary>
    private async Task UpsertAsync(DictionaryEntry entry)
    {
        await _writeGate.WaitAsync();
        IsSaving = true;
        try
        {
            var newEntries = _dictionaryService.Current.Entries
                .Where(e => !string.Equals(e.Id, entry.Id, StringComparison.Ordinal))
                .Append(entry)
                .ToList();

            await WriteDocumentAsync(newEntries);
            var settled = await WaitForNextDictionaryChangedOrTimeoutAsync();

            var current = _dictionaryService.Current;
            var landed = current.Entries.FirstOrDefault(e => string.Equals(e.Id, entry.Id, StringComparison.Ordinal));

            if (landed is not null && landed.Equals(entry))
            {
                SetStatus($"Saved \"{entry.Id}\".", isError: false);
            }
            else
            {
                var rejection = current.RejectedEntries
                    .FirstOrDefault(r => string.Equals(r.Id, entry.Id, StringComparison.Ordinal));

                if (rejection is not null)
                {
                    SetStatus($"Rejected: {rejection.Reason}", isError: true);
                }
                else if (!settled)
                {
                    // The real DictionaryService's whole-file duplicate-Id rejection NEVER
                    // raises DictionaryChanged at all (see DictionaryConfig.RejectedEntries's
                    // own doc comment: "Empty when the whole file was rejected outright... there
                    // is no partial entry list to report on") -- so a settle timeout with no
                    // event observed is the expected SHAPE of exactly that failure, named as
                    // such rather than reported as a vague "might still be pending" message.
                    SetStatus(
                        "The change did not apply, and no reload was observed — the dictionary file was most "
                        + "likely rejected as a whole (e.g. a duplicate Id elsewhere in the file). Check the app log.",
                        isError: true);
                }
                else
                {
                    SetStatus(
                        "The change did not apply, even though a reload was observed. Check the app log.",
                        isError: true);
                }
            }
        }
        catch (Exception ex)
        {
            SetStatus($"Failed to write dictionary.json: {ex.Message}", isError: true);
        }
        finally
        {
            RefreshFromCurrent();
            IsSaving = false;
            _writeGate.Release();
        }
    }

    private async Task WriteDocumentAsync(List<DictionaryEntry> entries)
    {
        var document = new DictionaryDocument
        {
            SchemaVersion = _dictionaryService.Current.SchemaVersion,
            Entries = entries,
        };
        var json = JsonSerializer.Serialize(document, DictionaryJsonOptions.Create());
        await File.WriteAllTextAsync(_dictionaryService.DictionaryPath, json);
    }

    /// <summary>Awaits the next <see cref="IDictionaryService.DictionaryChanged"/> event or
    /// <see cref="_settleTimeout"/>, whichever comes first. Returns whether the event fired.</summary>
    private async Task<bool> WaitForNextDictionaryChangedOrTimeoutAsync()
    {
        var tcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        void Handler(object? s, DictionaryChangedEventArgs e) => tcs.TrySetResult(true);

        _dictionaryService.DictionaryChanged += Handler;
        try
        {
            var completed = await Task.WhenAny(tcs.Task, Task.Delay(_settleTimeout));
            return completed == tcs.Task;
        }
        finally
        {
            _dictionaryService.DictionaryChanged -= Handler;
        }
    }

    /// <summary>The permanent live-refresh subscription (hand-edits, or this ViewModel's own
    /// writes settling) -- see class doc comment for why this can never clobber a
    /// write-then-verify flow's own <see cref="StatusMessage"/>.</summary>
    private void OnDictionaryChanged(object? sender, DictionaryChangedEventArgs e) =>
        _postToUiThread(RefreshFromCurrent);

    private void RefreshFromCurrent()
    {
        _allRows = _dictionaryService.Current.Entries
            .Select(entry => new DictionaryEntryRowViewModel(entry))
            .ToList();
        ApplyFilter();
        OnPropertyChanged(nameof(RejectedEntries));
    }

    private void ApplyFilter()
    {
        Entries.Clear();
        foreach (var row in _allRows.Where(r => _typeFilter is null || r.Kind == _typeFilter))
            Entries.Add(row);
    }

    private void SetStatus(string message, bool isError)
    {
        StatusMessage = message;
        StatusIsError = isError;
    }

    internal static DictionaryEntryKind KindOf(DictionaryEntry entry) => entry switch
    {
        VocabularyTerm => DictionaryEntryKind.VocabularyTerm,
        CorrectionPair => DictionaryEntryKind.CorrectionPair,
        RegexRule => DictionaryEntryKind.RegexRule,
        SpokenCommand => DictionaryEntryKind.SpokenCommand,
        PerAppOverride => DictionaryEntryKind.PerAppOverride,
        _ => throw new InvalidOperationException($"Unknown dictionary entry type {entry.GetType()}"),
    };

    private static DictionaryEntry WithEnabled(DictionaryEntry entry, bool enabled) => entry switch
    {
        VocabularyTerm v => v with { Enabled = enabled },
        CorrectionPair c => c with { Enabled = enabled },
        RegexRule r => r with { Enabled = enabled },
        SpokenCommand s => s with { Enabled = enabled },
        PerAppOverride p => p with { Enabled = enabled },
        _ => throw new InvalidOperationException($"Unknown dictionary entry type {entry.GetType()}"),
    };

    private void OnPropertyChanged([CallerMemberName] string? propertyName = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));

    /// <summary>Unsubscribes from <see cref="IDictionaryService.DictionaryChanged"/>. Does NOT
    /// dispose <see cref="IDictionaryService"/> itself -- this ViewModel does not own the
    /// service's lifetime (the composition root does).</summary>
    public void Dispose()
    {
        _dictionaryService.DictionaryChanged -= OnDictionaryChanged;
        _writeGate.Dispose();
    }
}
