namespace RocoPilot.Services;

internal static class InterceptionSynchronization
{
    public static object Gate
    {
        get;
    } = new();
}
