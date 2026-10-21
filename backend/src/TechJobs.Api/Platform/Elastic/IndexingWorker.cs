using System.Threading.Channels;
using Microsoft.Extensions.Logging;
using TechJobs.Api.JobsCore.Persistence;

namespace TechJobs.Api.Platform.Elastic;

/// <summary>
/// Sinalização de indexação pendente: o POST/PUT acorda o worker via canal; o tick
/// periódico (1s) cobre eventos que sobraram de falhas, restarts ou quedas do ES.
/// A verdade do "pendente" é a tabela outbox_events — o canal é apenas latência baixa.
/// </summary>
public sealed class IndexingQueue
{
    private readonly Channel<Guid> _wake = Channel.CreateBounded<Guid>(
        new BoundedChannelOptions(1024) { FullMode = BoundedChannelFullMode.DropOldest });

    public void Wake(Guid jobId) => _wake.Writer.TryWrite(jobId);

    public ChannelReader<Guid> Reader => _wake.Reader;
}

/// <summary>
/// Sincronização PostgreSQL → Elasticsearch (eventualmente consistente):
/// 1. commit da vaga + evento outbox na mesma transação;
/// 2. worker indexa em lote e só remove o evento após confirmação do bulk;
/// 3. falha (ES fora do ar) deixa o evento pendente — próximo tick repete com backoff natural.
/// Se a API cair entre commit e indexação, o evento sobrevive no outbox e é recuperado no restart.
/// </summary>
public sealed class IndexingWorker : BackgroundService
{
    private readonly JobStore _repo;
    private readonly ElasticIndex _elastic;
    private readonly Outbox _outbox;
    private readonly IndexingQueue _queue;
    private readonly ILogger<IndexingWorker> _logger;

    public IndexingWorker(JobStore repo, ElasticIndex elastic, Outbox outbox, IndexingQueue queue, ILogger<IndexingWorker> logger)
    {
        _repo = repo;
        _elastic = elastic;
        _outbox = outbox;
        _queue = queue;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // Recuperação de eventos pendentes de execuções anteriores (crash entre commit e indexação).
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                var pending = await _outbox.OutboxCountAsync(stoppingToken);
                if (pending > 0)
                {
                    await ProcessPendingAsync(stoppingToken);
                    _logger.LogInformation("Worker de indexação recuperou {Pending} evento(s) pendente(s).", pending);
                }
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Varredura inicial de indexação falhou; nova tentativa no próximo tick.");
            }

            var wakeTask = _queue.Reader.WaitToReadAsync(stoppingToken).AsTask();
            var delayTask = Task.Delay(TimeSpan.FromSeconds(1), stoppingToken);
            var completed = await Task.WhenAny(wakeTask, delayTask);

            // Consome sinais acumulados sem bloquear.
            while (_queue.Reader.TryRead(out _)) { }

            if (completed == delayTask && !wakeTask.IsCompleted) continue; // tick periódico
        }
    }

    /// <summary>Processa todo o outbox em lotes; usado pelo worker (erros engolidos) e pelo drain.</summary>
    public async Task ProcessPendingAsync(CancellationToken ct = default)
    {
        while (true)
        {
            var ids = await _outbox.PeekOutboxAsync(500, ct);
            if (ids.Count == 0) break;

            var jobs = await _repo.GetByIdsAsync(ids, ct);
            if (jobs.Count > 0)
                await _elastic.IndexManyAsync(jobs, ct);

            await _outbox.RemoveFromOutboxAsync(ids, ct);
        }
    }
}
