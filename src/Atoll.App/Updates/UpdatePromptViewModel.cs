using CommunityToolkit.Mvvm.Input;

namespace Atoll.Updates;

/// <summary>
/// The island's "update ready" prompt: the new version is already downloaded, so the only question is whether to
/// restart now. Both answers dismiss the prompt; Later leaves the update to apply the next time ATOLL starts.
/// </summary>
public sealed partial class UpdatePromptViewModel(string version, Action restart, Action dismiss)
{
    public string Title => $"ATOLL {version} is ready";

    public string Detail => "Restart to finish updating.";

    [RelayCommand]
    private void Restart()
    {
        dismiss();
        restart();
    }

    [RelayCommand]
    private void Later() => dismiss();
}
