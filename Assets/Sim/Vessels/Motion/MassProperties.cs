namespace MaxQ.Sim.Vessels.Motion;

/// <summary>Mass, centre of mass and inertia of an axisymmetric stack, summed piece by piece along the stack axis (+Z).</summary>
public readonly struct MassProperties {

    public readonly double Mass;

    // Sums of m z and of (own transverse moment + m z^2), so pieces combine without knowing the centre yet.
    private readonly double _moment;
    private readonly double _second;

    /// <summary>Moment of inertia about the stack axis, kg m^2.</summary>
    public readonly double Axial;

    private MassProperties(double mass, double moment, double second, double axial) {

        Mass = mass;
        _moment = moment;
        _second = second;
        Axial = axial;

    }

    /// <summary>Stack station of the centre of mass.</summary>
    public double CentreOfMass => Mass > 0.0 ? _moment / Mass : 0.0;

    /// <summary>Moment of inertia about a transverse axis through the centre of mass, kg m^2.</summary>
    public double Transverse => _second - Mass * CentreOfMass * CentreOfMass;

    /// <summary>Adds a piece centred on the axis at station <paramref name="z"/>, with its own moments about its centre.</summary>
    public MassProperties Add(double mass, double z, double axial, double transverse) =>
        new MassProperties(Mass + mass, _moment + mass * z, _second + transverse + mass * z * z, Axial + axial);

    /// <summary>Adds a solid cylinder, or a slab of liquid standing in a tank.</summary>
    public MassProperties AddCylinder(double mass, double z, double radius, double length) =>
        Add(mass, z, 0.5 * mass * radius * radius, mass * (3.0 * radius * radius + length * length) / 12.0);

    /// <summary>Adds a thin cylindrical shell.</summary>
    public MassProperties AddShell(double mass, double z, double radius, double length) =>
        Add(mass, z, mass * radius * radius, mass * (0.5 * radius * radius + length * length / 12.0));

    /// <summary>Adds equal point masses spread evenly round a ring of the given radius.</summary>
    public MassProperties AddRing(double mass, double z, double radius) =>
        Add(mass, z, mass * radius * radius, 0.5 * mass * radius * radius);

}
