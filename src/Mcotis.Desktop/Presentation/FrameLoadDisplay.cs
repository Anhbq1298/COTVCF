using Mcotis.Core;

namespace Mcotis.Desktop.Presentation;

/// <summary>A frame distributed load read from the native model for the viewport.</summary>
public sealed record FrameLoadDisplay(string MemberName, string PatternName, FrameLoadType Type,
    LoadDirection Direction, bool Projected, double RelativeStart, double RelativeEnd, double ValueStart, double ValueEnd);
