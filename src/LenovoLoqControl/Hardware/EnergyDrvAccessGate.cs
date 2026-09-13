namespace LenovoLoqControl.Hardware;

internal static class EnergyDrvAccessGate
{
    private static readonly object Gate = new();

    public static TResult Execute<TResult>(Func<TResult> operation)
    {
        ArgumentNullException.ThrowIfNull(operation);
        lock (Gate)
        {
            return operation();
        }
    }
}
