using System.Collections.Concurrent;

namespace TechJobs.Api.Platform.Runtime;

/// <summary>
/// Coalescing de execução por chave (single-flight): o primeiro request (líder) executa a
/// fábrica; os concorrentes na mesma chave (seguidores) esperam e reutilizam o resultado —
/// uma rajada fria de leituras gera uma única ida à fonte (PostgreSQL/Elasticsearch).
/// Sem lock global: cada chave tem sua própria entrada em um dicionário concorrente e a
/// entrada sai do dicionário na conclusão (sucesso ou falha) — o crescimento é limitado
/// pelas chaves em voo naquele instante. Cancelamento do seguidor não prende ninguém
/// (WaitAsync(ct)); falha do líder libera os seguidores, que tentam de novo — provavelmente
/// como novos líderes. O retorno distingue líder de seguidor (ExecutedFactory) para que o
/// chamador possa revalidar o próprio cache quando apenas reutilizou o resultado.
/// </summary>
public sealed class SingleFlight
{
    private readonly ConcurrentDictionary<string, object> _inflight = new();

    /// <summary>Entradas em voo (diagnóstico/testes: deve voltar a zero após as conclusões).</summary>
    public int InflightCount => _inflight.Count;

    public async Task<(T Result, bool ExecutedFactory)> CoalesceAsync<T>(
        string key, Func<CancellationToken, Task<T>> factory, CancellationToken ct = default)
    {
        while (true)
        {
            var tcs = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
            if (_inflight.TryAdd(key, tcs))
            {
                try
                {
                    var result = await factory(ct);
                    tcs.TrySetResult(result);
                    return (result, ExecutedFactory: true);
                }
                catch (Exception ex)
                {
                    tcs.TrySetException(ex);
                    throw;
                }
                finally
                {
                    // Remoção condicional (chave + valor): nunca remove a entrada de um
                    // líder mais recente que venceu a corrida após a minha conclusão.
                    _inflight.TryRemove(new KeyValuePair<string, object>(key, tcs));
                }
            }
            if (_inflight.TryGetValue(key, out var obj) && obj is TaskCompletionSource<T> leader)
            {
                try
                {
                    var result = await leader.Task.WaitAsync(ct);
                    return (result, ExecutedFactory: false); // seguidor reutiliza o resultado do líder
                }
                catch (OperationCanceledException) when (ct.IsCancellationRequested)
                {
                    throw; // cancelamento do próprio seguidor: propaga sem afetar o líder
                }
                catch (Exception)
                {
                    continue; // líder falhou/sumiu: tenta de novo (provavelmente vira líder)
                }
            }
            // Corrida com a remoção: recomeça (TryAdd ou TryGetValue na próxima iteração).
        }
    }
}
