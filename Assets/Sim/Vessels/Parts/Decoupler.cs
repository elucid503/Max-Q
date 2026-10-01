using MaxQ.Sim.Vessels.Motion;

namespace MaxQ.Sim.Vessels.Parts;

/// <summary>A separation ring. Staging cuts the stack here: the ring stays with everything below it, and its springs push
/// the two halves apart.</summary>
public sealed class Decoupler : Part {

    public double Radius { get; init; }
    public double Length { get; init; }
    public double Mass { get; init; }

    /// <summary>Spring impulse shared equally and oppositely between the halves, N s.</summary>
    public double Impulse { get; init; }

    public override double Height => Length;

    internal override MassProperties AddTo(MassProperties sum) => sum.AddShell(Mass, Station + 0.5 * Length, Radius, Length);

}
