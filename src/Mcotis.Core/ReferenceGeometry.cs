using System.Collections.Immutable;
using System.Globalization;

namespace Mcotis.Core;

/// <summary>One line of a grid system: its label and where it is along the system's own axis, in metres from the
/// system's origin. A line of the X grid is a line of constant local x; one of the Y grid, of constant local y.</summary>
public sealed record GridLine(string Label, double Coordinate, bool Visible = true);

/// <summary>
/// A Cartesian grid system as SAP2000 and ETABS define one (geometry editing plan, 2026-10-15): an origin, a rotation
/// about global Z and the X and Y grid lines. Reference data only — a grid is no joint, no element and no input of an
/// analysis: defining, moving or renaming its lines moves no point and leaves results current. Version 1 is Cartesian;
/// radial and curvilinear systems are later work.
/// </summary>
public sealed record GridSystem(string Name, Xyz Origin, double RotationDeg, ImmutableArray<GridLine> XLines, ImmutableArray<GridLine> YLines,
    bool Visible = true)
{
    public ImmutableArray<GridLine> XLines { get; init; } = XLines.IsDefault ? [] : XLines;
    public ImmutableArray<GridLine> YLines { get; init; } = YLines.IsDefault ? [] : YLines;

    /// <summary>Lines of an unrotated system run along the global axes, so an X line is a global YZ plane at
    /// X = origin + coordinate and a Y line an XZ plane: only those name an elevation of the model.</summary>
    public bool IsAxisAligned => Math.Abs(Math.IEEERemainder(RotationDeg, 360)) < 1e-9;

    /// <summary>The global position of the local point (x, y) of the system, at the system's own elevation.</summary>
    public Xyz ToGlobal(double x, double y)
    {
        var radians = RotationDeg * Math.PI / 180;
        var (cos, sin) = (Math.Cos(radians), Math.Sin(radians));
        return new(Origin.X + x * cos - y * sin, Origin.Y + x * sin + y * cos, Origin.Z);
    }

    public bool Equals(GridSystem? other) => other is not null && Name == other.Name && Origin == other.Origin &&
        RotationDeg.Equals(other.RotationDeg) && Visible == other.Visible && XLines.SequenceEqual(other.XLines) && YLines.SequenceEqual(other.YLines);

    public override int GetHashCode() => HashCode.Combine(Name, Origin, RotationDeg, XLines.Length, YLines.Length);
}

/// <summary>A level (storey) of the model: a name and its elevation Z in metres. Reference data, as a grid line: it is
/// no point, and changing its elevation moves nothing.</summary>
public sealed record Level(string Name, double Elevation, bool Visible = true);

/// <summary>
/// The model's reference geometry: its grid systems and levels. Saved with the document (schema 7), undone and redone
/// with it as any edit, and no part of the analysis input (<see cref="ModelDocument.AnalysisInputHash"/>) — so a
/// grid edit never makes a result stale. Points are made at a grid intersection only by an explicit command.
/// </summary>
public sealed record ReferenceGeometry(ImmutableArray<GridSystem> GridSystems, ImmutableArray<Level> Levels)
{
    /// <summary>The id the change sets record a change of the reference geometry under.</summary>
    public static readonly ObjectId ChangeId = new(new Guid("6f1d3c2a-4b5e-4f80-9a1c-7e2d5b3a9c01"));

    public static ReferenceGeometry Empty { get; } = new([], []);

    public ImmutableArray<GridSystem> GridSystems { get; init; } = GridSystems.IsDefault ? [] : GridSystems;
    public ImmutableArray<Level> Levels { get; init; } = Levels.IsDefault ? [] : Levels;

    public bool IsEmpty => GridSystems.IsEmpty && Levels.IsEmpty;

    /// <summary>The levels from the lowest up.</summary>
    public IEnumerable<Level> LevelsByElevation => Levels.OrderBy(level => level.Elevation);

    public bool Equals(ReferenceGeometry? other) => other is not null && GridSystems.SequenceEqual(other.GridSystems) && Levels.SequenceEqual(other.Levels);
    public override int GetHashCode() => HashCode.Combine(GridSystems.Length, Levels.Length);

    /// <summary>Two grid lines of one axis, or two levels, closer than this [m] are one position.</summary>
    public const double PositionTolerance = Tolerances.Length;

    /// <summary>The stored form: names and labels trimmed (NFC), the levels ordered by elevation, each system's lines by
    /// coordinate. Throws <see cref="DocumentException"/> (<see cref="DocumentError.Invalid"/> or
    /// <see cref="DocumentError.InvalidName"/>) with every problem when the definitions are not valid.</summary>
    public ReferenceGeometry Normalized()
    {
        var problems = new List<string>();
        string Name(string? text, string what)
        {
            if (NamingService.TryNormalize(text, out var normalized)) return normalized;
            problems.Add($"{what} needs a name: not empty, not blank, without control characters.");
            return text ?? "";
        }
        static string Metres(double value) => value.ToString("0.###", CultureInfo.InvariantCulture);

        var systems = new List<GridSystem>();
        var systemNames = new HashSet<string>(NamingService.Comparer);
        foreach (var system in GridSystems)
        {
            var name = Name(system.Name, "A grid system");
            if (!systemNames.Add(name)) problems.Add($"The grid system name '{name}' is used more than once.");
            if (!system.Origin.IsFinite || !double.IsFinite(system.RotationDeg))
                problems.Add($"Grid system '{name}' needs a finite origin and rotation.");
            var labels = new HashSet<string>(NamingService.Comparer);
            ImmutableArray<GridLine> Lines(ImmutableArray<GridLine> lines, string axis)
            {
                var kept = new List<GridLine>();
                foreach (var line in lines)
                {
                    var label = Name(line.Label, $"A {axis} grid line of '{name}'");
                    if (!labels.Add(label)) problems.Add($"Grid system '{name}' has the grid line label '{label}' more than once: a label names one line.");
                    if (!double.IsFinite(line.Coordinate)) problems.Add($"Grid line '{label}' of '{name}' needs a finite coordinate.");
                    kept.Add(line with { Label = label });
                }
                var ordered = kept.OrderBy(line => line.Coordinate).ToImmutableArray();
                for (var index = 1; index < ordered.Length; index++)
                    if (Math.Abs(ordered[index].Coordinate - ordered[index - 1].Coordinate) <= PositionTolerance)
                        problems.Add($"Grid lines '{ordered[index - 1].Label}' and '{ordered[index].Label}' of '{name}' are both at {axis} = {Metres(ordered[index].Coordinate)} m.");
                return ordered;
            }
            systems.Add(system with { Name = name, XLines = Lines(system.XLines, "X"), YLines = Lines(system.YLines, "Y") });
        }

        var levels = new List<Level>();
        var levelNames = new HashSet<string>(NamingService.Comparer);
        foreach (var level in Levels)
        {
            var name = Name(level.Name, "A level");
            if (!levelNames.Add(name)) problems.Add($"The level name '{name}' is used more than once.");
            if (!double.IsFinite(level.Elevation)) problems.Add($"Level '{name}' needs a finite elevation.");
            levels.Add(level with { Name = name });
        }
        var byElevation = levels.OrderBy(level => level.Elevation).ToImmutableArray();
        for (var index = 1; index < byElevation.Length; index++)
            if (Math.Abs(byElevation[index].Elevation - byElevation[index - 1].Elevation) <= PositionTolerance)
                problems.Add($"Levels '{byElevation[index - 1].Name}' and '{byElevation[index].Name}' are both at Z = {Metres(byElevation[index].Elevation)} m.");

        if (problems.Count > 0)
            throw new DocumentException(DocumentError.Invalid, string.Join(" ", problems.Take(6)) + (problems.Count > 6 ? $" (+{problems.Count - 6} more)" : ""));
        return new(systems.ToImmutableArray(), byElevation);
    }
}
