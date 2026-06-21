using System.Windows;
using Dc.App.ViewModels;
using Dc.App.Views;
using Dc.Domain.Entities;
using Dc.Infrastructure.Orchestration;

namespace Dc.App.Services;

public sealed class TagEditorDialog : ITagEditorDialog
{
    private readonly IBrowseDialog _browseDialog;
    private readonly IFormulaValidator _formulaValidator;

    public TagEditorDialog(IBrowseDialog browseDialog, IFormulaValidator formulaValidator)
    {
        _browseDialog = browseDialog;
        _formulaValidator = formulaValidator;
    }

    public TagEditResult? Edit(
        IEnumerable<Group> availableGroups,
        Tag? existing,
        Group? defaultGroup = null,
        Func<string, CollectorTask?>? taskLookup = null,
        IReadOnlyCollection<Tag>? taskTags = null,
        IReadOnlyCollection<Formula>? existingFormulas = null)
    {
        var vm = new TagEditorViewModel(
            availableGroups, existing, defaultGroup, _browseDialog, taskLookup,
            taskTags, existingFormulas, _formulaValidator);
        var window = new TagEditorWindow
        {
            DataContext = vm,
            Owner = Application.Current.MainWindow
        };
        return window.ShowDialog() == true ? vm.ToResult() : null;
    }
}
