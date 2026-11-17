namespace Velia.Core.Input;

internal enum PhysicalKeyEdge
{
    Pressed,
    Released,
}

/// <summary>
/// Converts sampled physical key state into one press and one release edge.
/// A key already held when Velia starts is ignored until it is released once.
/// </summary>
internal sealed class PhysicalKeyEdgeDetector
{
    private bool initialized;
    private bool armed;
    private bool wasDown;

    public void Reset()
    {
        initialized = false;
        armed = false;
        wasDown = false;
    }

    public PhysicalKeyEdge? Sample(bool isDown)
    {
        if (!initialized)
        {
            initialized = true;
            wasDown = isDown;
            armed = !isDown;
            return null;
        }

        if (!armed)
        {
            wasDown = isDown;
            armed = !isDown;
            return null;
        }

        if (isDown == wasDown) return null;
        wasDown = isDown;
        return isDown ? PhysicalKeyEdge.Pressed : PhysicalKeyEdge.Released;
    }
}
