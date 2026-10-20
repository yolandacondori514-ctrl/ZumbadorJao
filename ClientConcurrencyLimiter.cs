namespace ZumbidoJao;

internal sealed class ClientConcurrencyLimiter : IDisposable
{
    private readonly SemaphoreSlim slots;

    public ClientConcurrencyLimiter(int capacity)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(capacity);
        slots = new SemaphoreSlim(capacity, capacity);
    }

    public IDisposable? TryAcquire()
    {
        if (!slots.Wait(0))
            return null;

        return new Lease(slots);
    }

    public void Dispose() => slots.Dispose();

    private sealed class Lease(SemaphoreSlim slots) : IDisposable
    {
        private SemaphoreSlim? ownedSlots = slots;

        public void Dispose()
        {
            Interlocked.Exchange(ref ownedSlots, null)?.Release();
        }
    }
}
