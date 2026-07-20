using ApplicationUpdater.Models;
using CommunityToolkit.Mvvm.ComponentModel;

namespace ApplicationUpdater.ViewModels;

public partial class ProgramItemViewModel : ObservableObject
{
    public ProgramInfo Model { get; }

    [ObservableProperty]
    private bool _isSelected;

    public ProgramItemViewModel(ProgramInfo model)
    {
        Model = model;
    }

    public string Name => Model.Name;
    public string Version => Model.Version;
    public string AvailableVersion => Model.UpdateAvailable ? Model.AvailableVersion : "—";
    public string Source => Model.SourceDisplay;
    public string Publisher => string.IsNullOrWhiteSpace(Model.Publisher) ? "—" : Model.Publisher;
    public string PackageId => string.IsNullOrWhiteSpace(Model.PackageId) ? "—" : Model.PackageId;
    public bool UpdateAvailable => Model.UpdateAvailable;
    public string UpdateStatus => Model.UpdateAvailable ? "Update available" : "Up to date";
}
