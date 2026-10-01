using MaxQ.Sim.Vessels.Parts;

using NUnit.Framework;

namespace MaxQ.Sim.Tests.Vessels;

public sealed class TankTests {

    [Test]
    public void PropellantsRunDryTogether() {

        Tank tank = ReferenceCraft.Tank();

        Assert.That(tank.OxidiserCapacity / tank.FuelCapacity, Is.EqualTo(tank.MixtureRatio).Within(1e-9));

    }

    [Test]
    public void BulkheadSitsInsideTheBarrel() {

        Tank tank = ReferenceCraft.Tank();

        Assert.That(tank.IsValid, Is.True);
        Assert.That(tank.BulkheadHeight, Is.GreaterThan(2.0 * tank.DomeDepth));
        Assert.That(tank.BulkheadHeight, Is.LessThan(tank.Height));

    }

    [Test]
    public void BarrelTooShortForItsOxidiserIsInvalid() {

        Tank tank = new Tank {

            Radius = 1.85,
            BarrelLength = 0.2,
            DomeRatio = 0.7,
            MixtureRatio = 2.36,
            OxidiserDensity = 1_141.0,
            FuelDensity = 820.0,
            WallArealDensity = 14.0,

        };

        Assert.That(tank.IsValid, Is.False);

    }

    [Test]
    public void PropellantOutweighsTheWallsManyTimes() {

        Tank tank = ReferenceCraft.Tank();

        Assert.That((tank.OxidiserCapacity + tank.FuelCapacity) / tank.DryMass, Is.InRange(25.0, 60.0));

    }

}
