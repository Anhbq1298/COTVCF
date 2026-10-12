using Mcotis.Desktop.Application;
using Mcotis.Desktop.Domain;

namespace Mcotis.Desktop.Presentation;

public sealed partial class MainViewModel
{
    public IReadOnlyList<string> FemOutputCaseChoices => FemLoadSelectionNames;

    public string FemDeflectionOutputCase
    {
        get => State.OutputSettings.DeflectionOutputCaseName;
        set { if (CanEditFemModel) { State.OutputSettings.DeflectionOutputCaseName = value; Update(); } }
    }
    public string FemMemberForceOutputCase
    {
        get => State.OutputSettings.MemberForceResultLoadCase;
        set { if (CanEditFemModel) { State.OutputSettings.MemberForceResultLoadCase = value; Update(); } }
    }
    public string FemFoundationOutputCase
    {
        get => State.OutputSettings.FoundationReactionResultLoadCase;
        set { if (CanEditFemModel) { State.OutputSettings.FoundationReactionResultLoadCase = value; Update(); } }
    }
    public string FemCableOutputCase
    {
        get => State.OutputSettings.CableResultLoadCase;
        set { if (CanEditFemModel) { State.OutputSettings.CableResultLoadCase = value; Update(); } }
    }

    public string FemDeflectionJointNames
    {
        get => Join(State.OutputSettings.DeflectionPoints);
        set => SetNames(value, State.OutputSettings.DeflectionPoints, MemberKind.Frame, points: true);
    }
    public string FemFoundationLeftJointNames
    {
        get => Join(State.OutputSettings.FoundationLeftPoints);
        set => SetNames(value, State.OutputSettings.FoundationLeftPoints, MemberKind.Frame, points: true);
    }
    public string FemFoundationRightJointNames
    {
        get => Join(State.OutputSettings.FoundationRightPoints);
        set => SetNames(value, State.OutputSettings.FoundationRightPoints, MemberKind.Frame, points: true);
    }
    public string FemCableObjectNames
    {
        get => Join(State.OutputSettings.CableNames);
        set => SetNames(value, State.OutputSettings.CableNames, MemberKind.Cable, points: false);
    }
    public string FemFrameObjectNames
    {
        get => Join(State.OutputSettings.MemberForceGroups.SelectMany(group => group.Members));
        set
        {
            if (!CanEditFemModel) return;
            var names = ParseNames(value);
            ValidateMembers(names, MemberKind.Frame);
            State.OutputSettings.MemberForceGroups.Clear();
            if (names.Count > 0)
            {
                var group = new MemberForceOutputGroup { Name = "FEM Frames" };
                foreach (var name in names) group.Members.Add(name);
                State.OutputSettings.MemberForceGroups.Add(group);
            }
            Update();
        }
    }

    public double? FemDeflectionLimitMinMm
    {
        get => State.OutputSettings.DeflectionLimitMinMm;
        set { if (CanEditFemModel) { State.OutputSettings.DeflectionLimitMinMm = value; ValidateDeflectionBand(); Update(); } }
    }
    public double? FemDeflectionLimitMaxMm
    {
        get => State.OutputSettings.DeflectionLimitMaxMm;
        set { if (CanEditFemModel) { State.OutputSettings.DeflectionLimitMaxMm = value; ValidateDeflectionBand(); Update(); } }
    }
    public double FemMaximumCableUtilization
    {
        get => State.OutputSettings.MaxCableUtilization;
        set
        {
            if (!CanEditFemModel) return;
            if (!double.IsFinite(value) || value <= 0) throw new ArgumentOutOfRangeException(nameof(value), "Maximum cable utilization must be greater than zero.");
            State.OutputSettings.MaxCableUtilization = value;
            Update();
        }
    }

    public string FemOutputSettingsStatus
    {
        get
        {
            var issues = new OutputSettingsValidator().Validate(State);
            return issues.Count == 0 ? "Ready · settings are stored in the FEM input database"
                : $"{issues.Count} issue(s) · {issues[0].Message}";
        }
    }

    private void SetNames(string text, System.Collections.ObjectModel.ObservableCollection<string> target, MemberKind kind, bool points)
    {
        if (!CanEditFemModel) return;
        var names = ParseNames(text);
        if (points) ValidatePoints(names); else ValidateMembers(names, kind);
        target.Clear();
        foreach (var name in names) target.Add(name);
        Update();
    }

    private void ValidatePoints(IReadOnlyList<string> names)
    {
        var missing = names.FirstOrDefault(name => !State.Geometry.Points.ContainsKey(name));
        if (missing is not null) throw new ArgumentException($"Joint '{missing}' does not exist in the current geometry.");
    }

    private void ValidateMembers(IReadOnlyList<string> names, MemberKind kind)
    {
        var available = State.Geometry.Members.Where(member => member.Kind == kind).Select(member => member.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var missing = names.FirstOrDefault(name => !available.Contains(name));
        if (missing is not null) throw new ArgumentException($"{kind} object '{missing}' does not exist in the current geometry.");
    }

    private void ValidateDeflectionBand()
    {
        if (State.OutputSettings.DeflectionLimitBand.Problem() is { } problem) throw new ArgumentException(problem);
    }

    private static List<string> ParseNames(string? value) => (value ?? "")
        .Split([',', ';', '\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
        .Distinct(StringComparer.OrdinalIgnoreCase).ToList();

    private static string Join(IEnumerable<string> values) => string.Join(", ", values);
}
