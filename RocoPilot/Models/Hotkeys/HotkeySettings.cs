namespace RocoPilot.Models.Hotkeys;

public sealed class HotkeySettings
{
    public const int CurrentVersion = 1;

    public int Version
    {
        get;
        set;
    }

    public List<HotkeyBindingAssignment> Bindings
    {
        get;
        set;
    } = [];

    public HotkeyBinding? GetBinding(HotkeyAction action)
    {
        return Bindings.FirstOrDefault(binding => binding.Action == action)?.Binding?.Clone();
    }

    public HotkeySettings Clone()
    {
        return new HotkeySettings
        {
            Version = Version,
            Bindings = Bindings.Select(binding => binding.Clone()).ToList()
        };
    }

    public static HotkeySettings CreateDefault()
    {
        return new HotkeySettings
        {
            Version = CurrentVersion,
            Bindings =
            [
                new HotkeyBindingAssignment
                {
                    Action = HotkeyAction.ToggleCameraSweep,
                    Binding = HotkeyBinding.Create([], 0x50)
                }
            ]
        };
    }
}
