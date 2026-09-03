using Avalonia.Controls;
using Avalonia.Interactivity;
using Soneto.App.ViewModels;
using Soneto.Core.Dictionary;

namespace Soneto.App.Views;

public partial class DictionaryEditorView : UserControl
{
    /// <summary>
    /// Required by the Avalonia XAML compiler (AVLN3001 -- "no public constructor was found",
    /// needed for avares:// dynamic/design-time XAML resolution). Never used by production
    /// code, which always calls the <see cref="IDictionaryService"/>-taking constructor below;
    /// leaves <see cref="UserControl.DataContext"/> unset.
    /// </summary>
    public DictionaryEditorView()
    {
        InitializeComponent();
    }

    /// <summary>
    /// Item 7 (§3.11): replaces the item-4 parameterless placeholder. Takes the real
    /// <see cref="IDictionaryService"/> the composition root (<c>App.axaml.cs</c>) constructs
    /// and synchronously loads at startup (see that file's own doc comment for why the
    /// dictionary load is deliberately synchronous, unlike <c>PipelineHost</c>'s pipeline
    /// startup) and builds this view's <see cref="DictionaryEditorViewModel"/> from it.
    /// </summary>
    public DictionaryEditorView(IDictionaryService dictionaryService) : this()
    {
        DataContext = new DictionaryEditorViewModel(dictionaryService);
    }

    private DictionaryEditorViewModel? ViewModel => DataContext as DictionaryEditorViewModel;

    private void OnFilterAllClick(object? sender, RoutedEventArgs e) =>
        SetFilter(null);

    private void OnFilterVocabularyClick(object? sender, RoutedEventArgs e) =>
        SetFilter(DictionaryEntryKind.VocabularyTerm);

    private void OnFilterCorrectionClick(object? sender, RoutedEventArgs e) =>
        SetFilter(DictionaryEntryKind.CorrectionPair);

    private void OnFilterRegexClick(object? sender, RoutedEventArgs e) =>
        SetFilter(DictionaryEntryKind.RegexRule);

    private void OnFilterSpokenCommandClick(object? sender, RoutedEventArgs e) =>
        SetFilter(DictionaryEntryKind.SpokenCommand);

    private void OnFilterPerAppClick(object? sender, RoutedEventArgs e) =>
        SetFilter(DictionaryEntryKind.PerAppOverride);

    private void SetFilter(DictionaryEntryKind? kind)
    {
        if (ViewModel is { } vm)
            vm.TypeFilter = kind;
    }

    private void OnAddVocabularyClick(object? sender, RoutedEventArgs e) =>
        ViewModel?.BeginAddNew(DictionaryEntryKind.VocabularyTerm);

    private void OnAddCorrectionClick(object? sender, RoutedEventArgs e) =>
        ViewModel?.BeginAddNew(DictionaryEntryKind.CorrectionPair);

    private void OnAddRegexClick(object? sender, RoutedEventArgs e) =>
        ViewModel?.BeginAddNew(DictionaryEntryKind.RegexRule);

    private void OnAddSpokenCommandClick(object? sender, RoutedEventArgs e) =>
        ViewModel?.BeginAddNew(DictionaryEntryKind.SpokenCommand);

    private void OnAddPerAppClick(object? sender, RoutedEventArgs e) =>
        ViewModel?.BeginAddNew(DictionaryEntryKind.PerAppOverride);

    private void OnEditEntryClick(object? sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: DictionaryEntryRowViewModel row })
            ViewModel?.BeginEdit(row);
    }

    private async void OnToggleEnabledClick(object? sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: DictionaryEntryRowViewModel row } && ViewModel is { } vm)
            await vm.ToggleEnabledAsync(row);
    }

    private async void OnDeleteEntryClick(object? sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: DictionaryEntryRowViewModel row } && ViewModel is { } vm)
            await vm.DeleteAsync(row);
    }

    private async void OnSaveClick(object? sender, RoutedEventArgs e)
    {
        if (ViewModel is { } vm)
            await vm.SaveAsync();
    }

    private void OnCancelClick(object? sender, RoutedEventArgs e) =>
        ViewModel?.CancelEdit();
}
