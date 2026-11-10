using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace Atoll.Shell;

/// <summary>
/// Two side activities that share one side of the island. They sit inside a single longer pill, each in its own
/// tappable cell, instead of becoming two separate pills.
/// </summary>
public sealed partial class SideChipPair(SideChipViewModel first, SideChipViewModel second, Action openFirst, Action openSecond)
    : ObservableObject
{
    public SideChipViewModel First => first;

    public SideChipViewModel Second => second;

    /// <summary>The cell widths follow what each chip is (a ring or a labelled capsule); the window sets them.</summary>
    [ObservableProperty] private double _firstWidth = 30;
    [ObservableProperty] private double _secondWidth = 30;

    public bool HasRing => first.IsRing || second.IsRing;

    public string Label => $"{first.Label}, {second.Label}";

    [RelayCommand]
    private void OpenFirst() => openFirst();

    [RelayCommand]
    private void OpenSecond() => openSecond();
}
