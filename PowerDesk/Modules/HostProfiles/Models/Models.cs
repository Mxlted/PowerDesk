using System;
using System.Collections.Generic;
using CommunityToolkit.Mvvm.ComponentModel;

namespace PowerDesk.Modules.HostProfiles.Models;

public sealed partial class HostProfile : ObservableObject
{
    [ObservableProperty] private string _name = string.Empty;
    [ObservableProperty] private string _content = string.Empty;
    [ObservableProperty] private DateTime _updatedAt = DateTime.Now;
    public string UpdatedLabel => UpdatedAt.ToString("yyyy-MM-dd HH:mm");

    partial void OnUpdatedAtChanged(DateTime value) => OnPropertyChanged(nameof(UpdatedLabel));
}

public sealed class HostProfilesSettings
{
    public List<HostProfile> Profiles { get; set; } = new();
}
