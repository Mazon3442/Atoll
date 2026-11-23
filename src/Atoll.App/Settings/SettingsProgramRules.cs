using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace Atoll.Settings;

/// <summary>One "while this program is in front" rule in Settings.</summary>
public sealed partial class ProgramRuleRow(SettingsViewModel owner, ProgramRule rule) : ObservableObject
{
    public ProgramRule Rule { get; } = rule;
    public string Name => Rule.Process;

    public FullscreenMode Mode
    {
        get => Rule.Mode;
        set
        {
            if (Rule.Mode == value) return;
            Rule.Mode = value;
            OnPropertyChanged();
            owner.ProgramRuleChanged();
        }
    }

    public IReadOnlyList<Choice<FullscreenMode>> Modes => owner.ProgramModes;

    [RelayCommand] private void Remove() => owner.RemoveProgramRule(this);
}
