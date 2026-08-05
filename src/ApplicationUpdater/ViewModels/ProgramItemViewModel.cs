using ApplicationUpdater.Models;
using CommunityToolkit.Mvvm.ComponentModel;

namespace ApplicationUpdater.ViewModels;

public partial class ProgramItemViewModel : ObservableObject
{
    public ProgramInfo Model { get; }

    [ObservableProperty]
    private bool _isSelected;

    [ObservableProperty]
    private int _progressPercent = -1;

    [ObservableProperty]
    private string _progressStatus = string.Empty;

    public ProgramItemViewModel(ProgramInfo model)
    {
        Model = model;
        _progressPercent = model.ProgressPercent;
        _progressStatus = model.ProgressStatus ?? string.Empty;
    }

    public string Name => Model.Name;
    public string Version => Model.Version;
    public string AvailableVersion =>
        Model.UpdateAvailable || !string.IsNullOrWhiteSpace(Model.AvailableVersion)
            ? (string.IsNullOrWhiteSpace(Model.AvailableVersion) ? "—" : Model.AvailableVersion)
            : "—";
    /// <summary>Origin/store for sorting (Steam, Epic, winget, …).</summary>
    public string Source => Model.SourceDisplay;
    public string Origin => string.IsNullOrWhiteSpace(Model.Origin) ? Model.SourceDisplay : Model.Origin;
    public string Publisher => string.IsNullOrWhiteSpace(Model.Publisher) ? "—" : Model.Publisher;
    public string PackageId => string.IsNullOrWhiteSpace(Model.PackageId) ? "—" : Model.PackageId;
    public bool UpdateAvailable => Model.UpdateAvailable;
    public string UpdateStatus => Model.UpdateAvailable ? "Update available" : "Up to date";
    public string LastUpdated => Model.LastUpdatedDisplay;
    public string ProgressText =>
        ProgressPercent < 0
            ? "—"
            : (string.IsNullOrWhiteSpace(ProgressStatus)
                ? $"{ProgressPercent}%"
                : $"{ProgressPercent}% · {ProgressStatus}");

    public void SetProgress(int percent, string? status = null)
    {
        ProgressPercent = percent;
        Model.ProgressPercent = percent;
        if (status is not null)
        {
            ProgressStatus = status;
            Model.ProgressStatus = status;
        }
        OnPropertyChanged(nameof(ProgressText));
    }

    public void ClearProgress()
    {
        ProgressPercent = -1;
        ProgressStatus = string.Empty;
        Model.ProgressPercent = -1;
        Model.ProgressStatus = string.Empty;
        OnPropertyChanged(nameof(ProgressText));
    }

    /// <summary>Raises property change notifications after Model fields are mutated in place.</summary>
    public void NotifyModelChanged()
    {
        OnPropertyChanged(nameof(Name));
        OnPropertyChanged(nameof(Version));
        OnPropertyChanged(nameof(AvailableVersion));
        OnPropertyChanged(nameof(Source));
        OnPropertyChanged(nameof(Origin));
        OnPropertyChanged(nameof(Publisher));
        OnPropertyChanged(nameof(PackageId));
        OnPropertyChanged(nameof(UpdateAvailable));
        OnPropertyChanged(nameof(UpdateStatus));
        OnPropertyChanged(nameof(LastUpdated));
        OnPropertyChanged(nameof(ProgressText));
    }
}
