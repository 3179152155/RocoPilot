using Microsoft.VisualStudio.TestTools.UnitTesting;

using RocoPilot.Models;

namespace RocoPilot.Tests;

[TestClass]
public sealed class CameraSweepSettingsTests
{
    [TestMethod]
    public void UsesCurrentSweepDefaults()
    {
        var settings = CameraSweepSettings.CreateDefault();

        Assert.AreEqual(2, settings.PixelsPerTick);
        Assert.AreEqual(10, settings.MovementIntervalMs);
        Assert.AreEqual(5, settings.DirectionDurationSeconds);
        Assert.AreEqual(CameraSweepDirection.Right, settings.InitialDirection);
    }

    [TestMethod]
    public void ClampsOutOfRangeValues()
    {
        var settings = CameraSweepSettings.Normalize(new CameraSweepSettings
        {
            PixelsPerTick = int.MaxValue,
            MovementIntervalMs = int.MinValue,
            DirectionDurationSeconds = int.MaxValue,
            InitialDirection = (CameraSweepDirection)99
        });

        Assert.AreEqual(CameraSweepSettings.MaximumPixelsPerTick, settings.PixelsPerTick);
        Assert.AreEqual(CameraSweepSettings.MinimumMovementIntervalMs, settings.MovementIntervalMs);
        Assert.AreEqual(CameraSweepSettings.MaximumDirectionDurationSeconds, settings.DirectionDurationSeconds);
        Assert.AreEqual(CameraSweepDirection.Right, settings.InitialDirection);
    }

    [TestMethod]
    public void ClonesValuesWithoutSharingState()
    {
        var original = new CameraSweepSettings
        {
            PixelsPerTick = 8,
            MovementIntervalMs = 20,
            DirectionDurationSeconds = 30,
            InitialDirection = CameraSweepDirection.Left
        };

        var clone = original.Clone();
        clone.PixelsPerTick = 1;

        Assert.AreEqual(8, original.PixelsPerTick);
        Assert.AreEqual(CameraSweepDirection.Left, clone.InitialDirection);
    }
}
