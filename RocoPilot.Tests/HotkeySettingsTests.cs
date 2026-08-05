using Microsoft.VisualStudio.TestTools.UnitTesting;

using RocoPilot.Models.Hotkeys;
using RocoPilot.Services;

namespace RocoPilot.Tests;

[TestClass]
public sealed class HotkeySettingsTests
{
    [TestMethod]
    public void NewSettingsBindCameraSweepToP()
    {
        var settings = HotkeyService.NormalizeSettings(null);

        var binding = settings.GetBinding(HotkeyAction.ToggleCameraSweep);

        Assert.IsNotNull(binding);
        Assert.AreEqual(0x50, binding.Key);
        Assert.AreEqual("P", binding.DisplayText);
        Assert.AreEqual(HotkeySettings.CurrentVersion, settings.Version);
    }

    [TestMethod]
    public void LegacyEmptySettingsReceiveCameraSweepDefault()
    {
        var legacySettings = new HotkeySettings
        {
            Version = 0,
            Bindings = []
        };

        var settings = HotkeyService.NormalizeSettings(legacySettings);

        Assert.AreEqual("P", settings.GetBinding(HotkeyAction.ToggleCameraSweep)?.DisplayText);
        Assert.AreEqual(HotkeySettings.CurrentVersion, settings.Version);
    }

    [TestMethod]
    public void ClearedCameraSweepBindingStaysUnbound()
    {
        var savedSettings = new HotkeySettings
        {
            Version = HotkeySettings.CurrentVersion,
            Bindings = []
        };

        var settings = HotkeyService.NormalizeSettings(savedSettings);

        Assert.IsNull(settings.GetBinding(HotkeyAction.ToggleCameraSweep));
    }

    [TestMethod]
    public void LegacyMigrationDoesNotReplaceAnExistingPBinding()
    {
        var savedSettings = new HotkeySettings
        {
            Version = 0,
            Bindings =
            [
                new HotkeyBindingAssignment
                {
                    Action = HotkeyAction.ToggleAutoBattle,
                    Binding = HotkeyBinding.Create([], 0x50)
                }
            ]
        };

        var settings = HotkeyService.NormalizeSettings(savedSettings);

        Assert.IsNull(settings.GetBinding(HotkeyAction.ToggleCameraSweep));
        Assert.AreEqual("P", settings.GetBinding(HotkeyAction.ToggleAutoBattle)?.DisplayText);
    }
}
