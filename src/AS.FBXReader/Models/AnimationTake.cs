namespace AS.FBXReader.Models;

public sealed class AnimationTake
{
    public required int Index { get; init; }
    public required string Name { get; init; }
    public double DurationSeconds { get; init; }
    public double TicksPerSecond { get; init; }

    public override string ToString() => $"{Name} ({DurationSeconds:0.00}s)";
}
