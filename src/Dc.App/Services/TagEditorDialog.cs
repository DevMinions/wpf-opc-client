using System.Windows;
using Dc.App.ViewModels;
using Dc.App.Views;
using Dc.Domain.Entities;

namespace Dc.App.Services;

public sealed class TagEditorDialog : ITagEditorDialog
{
    private readonly IBrowseDialog _browseDialog;

    public TagEditorDialog(IBrowseDialog browseDialog) => _browseDialog = browseDialog;

    public Tag? Edit(
        IEnumerable<Group> availableGroups,
        Tag? existing,
        Group? defaultGroup = null,
        Func<string, CollectorTask?>? taskLookup = null)
    {
        var vm = new TagEditorViewModel(availableGroups, existing, defaultGroup, _browseDialog, taskLookup);
        var window = new TagEditorWindow
        {
            DataContext = vm,
            Owner = Application.Current.MainWindow
        };
        return window.ShowDialog() == true ? vm.ToEntity() : null;
    }
}
