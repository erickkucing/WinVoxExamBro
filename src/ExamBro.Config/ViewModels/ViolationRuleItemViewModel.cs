using CommunityToolkit.Mvvm.ComponentModel;
using ExamBro.Core.Models;

namespace ExamBro.Config.ViewModels;

/// <summary>
/// ViewModel wrapper for an individual violation rule to support two-way DataGrid binding.
/// </summary>
public partial class ViolationRuleItemViewModel : ObservableObject
{
    public string EventKey { get; }
    public string DisplayName { get; }

    [ObservableProperty]
    private ViolationAction _action;

    [ObservableProperty]
    private int _threshold;

    [ObservableProperty]
    private ViolationAction _escalation;

    public ViolationRuleItemViewModel(string eventKey, string displayName, ViolationAction action, int threshold, ViolationAction escalation)
    {
        EventKey = eventKey;
        DisplayName = displayName;
        _action = action;
        _threshold = threshold;
        _escalation = escalation;
    }

    public ViolationRule ToViolationRule()
    {
        return new ViolationRule
        {
            Action = Action,
            Threshold = Threshold,
            Escalation = Escalation
        };
    }
}
