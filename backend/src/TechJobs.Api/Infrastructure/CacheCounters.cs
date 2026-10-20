namespace TechJobs.Api.Infrastructure;

/// <summary>
/// Contadores de cache desde o último /admin/reset (exibidos em /admin/cache-stats).
/// Em memória: a stack de avaliação roda uma única instância da API.
/// </summary>
public sealed class CacheCounters
{
    public enum Kind { Details, Search }

    public sealed class Counter
    {
        private long _hit;
        private long _miss;
        private long _error;

        public long Hit => Volatile.Read(ref _hit);
        public long Miss => Volatile.Read(ref _miss);
        public long Error => Volatile.Read(ref _error);

        public void AddHit() => Interlocked.Increment(ref _hit);
        public void AddMiss() => Interlocked.Increment(ref _miss);
        public void AddError() => Interlocked.Increment(ref _error);

        public void Reset()
        {
            Interlocked.Exchange(ref _hit, 0);
            Interlocked.Exchange(ref _miss, 0);
            Interlocked.Exchange(ref _error, 0);
        }
    }

    public Counter Details { get; } = new();
    public Counter Search { get; } = new();

    public Counter For(Kind kind) => kind == Kind.Details ? Details : Search;

    public void Reset()
    {
        Details.Reset();
        Search.Reset();
    }
}
