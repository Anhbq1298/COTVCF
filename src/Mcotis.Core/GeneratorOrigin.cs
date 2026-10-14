using System.Collections.Immutable;

namespace Mcotis.Core;

/// <summary>
/// The parametric generator a model was built by — the flag that makes it optimizable (task of 2026-10-14, §2: "generator
/// flag = optimization eligibility"): the generator's id (<c>VCF</c>, <c>TAS</c>), its version and the complete set of
/// parameters it built the model from, as text. The objects it generated carry their place in it as
/// <see cref="ObjectOrigin.GenerationKey"/>. Optimization is offered by this flag and the generator's own validation of the
/// document against it — never by a model's name or file. It is saved with the document and kept through Undo and Redo;
/// only an explicit Detach removes it. It is no part of the analysis input.
/// </summary>
public sealed record GeneratorOrigin(string GeneratorId, int Version, ImmutableSortedDictionary<string, string> Parameters)
{
    public ImmutableSortedDictionary<string, string> Parameters { get; init; } = Parameters ?? ImmutableSortedDictionary<string, string>.Empty;

    public static GeneratorOrigin Create(string generatorId, int version, IEnumerable<KeyValuePair<string, string>> parameters) =>
        new(generatorId, version, parameters.ToImmutableSortedDictionary(StringComparer.Ordinal));

    public bool Equals(GeneratorOrigin? other) => other is not null && GeneratorId == other.GeneratorId && Version == other.Version &&
        Parameters.SequenceEqual(other.Parameters);
    public override int GetHashCode() => HashCode.Combine(GeneratorId, Version, Parameters.Count);
}
