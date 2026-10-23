using System.Diagnostics;
using Xunit;
using TechJobs.Api.Platform.Runtime;

namespace TechJobs.UnitTests;

public class SingleFlightTests
{
    [Fact]
    public async Task Rajada_na_mesma_chave_executa_fabrica_uma_vez()
    {
        var singleFlight = new SingleFlight();
        var calls = 0;

        var tasks = Enumerable.Range(0, 20)
            .Select(_ => singleFlight.CoalesceAsync("rajada", async _ =>
            {
                Interlocked.Increment(ref calls);
                await Task.Delay(50); // janela para todos os concorrentes se registrarem como seguidores
                return "valor-do-lider";
            }))
            .ToList();

        var results = await Task.WhenAll(tasks);

        Assert.Equal(1, calls);
        Assert.All(results, r => Assert.Equal("valor-do-lider", r.Result));
        Assert.Equal(1, results.Count(r => r.ExecutedFactory)); // exatamente um líder
        Assert.Equal(0, singleFlight.InflightCount);
    }

    [Fact]
    public async Task Chaves_distintas_executam_concorrentemente()
    {
        var singleFlight = new SingleFlight();
        var calls = 0;

        var watch = Stopwatch.StartNew();
        var results = await Task.WhenAll(Enumerable.Range(0, 10).Select(async i =>
            await singleFlight.CoalesceAsync($"chave-{i}", async _ =>
            {
                Interlocked.Increment(ref calls);
                await Task.Delay(100);
                return i;
            })));
        watch.Stop();

        Assert.Equal(10, calls);
        // Serializado seriam ~1000ms; concorrente precisa ficar bem abaixo disso.
        Assert.True(watch.ElapsedMilliseconds < 10 * 100 * 0.8,
            $"fábricas executaram serializadas: {watch.ElapsedMilliseconds}ms");
        for (var i = 0; i < 10; i++)
            Assert.Equal((i, true), results[i]); // cada chave teve seu próprio líder
    }

    [Fact]
    public async Task Falha_do_lider_faz_seguidor_tentar()
    {
        var singleFlight = new SingleFlight();
        var calls = 0;

        async Task<string> FabricaQueFalhaNaPrimeiraChamada(CancellationToken _)
        {
            var call = Interlocked.Increment(ref calls);
            await Task.Delay(50); // garante o segundo chamador já registrado como seguidor
            if (call == 1)
                throw new InvalidOperationException("líder falhou");
            return "segunda-tentativa";
        }

        var lider = singleFlight.CoalesceAsync("chave", FabricaQueFalhaNaPrimeiraChamada);
        var seguidor = singleFlight.CoalesceAsync("chave", FabricaQueFalhaNaPrimeiraChamada);

        await Assert.ThrowsAsync<InvalidOperationException>(() => lider);
        var (resultado, executado) = await seguidor;

        Assert.Equal(2, calls);
        Assert.Equal("segunda-tentativa", resultado);
        Assert.True(executado); // seguidor virou líder e executou a fábrica
        Assert.Equal(0, singleFlight.InflightCount);
    }

    [Fact]
    public async Task Cancelamento_do_seguidor_nao_prende_o_lider()
    {
        var singleFlight = new SingleFlight();
        using var cts = new CancellationTokenSource();

        var lider = singleFlight.CoalesceAsync("chave", async _ =>
        {
            await Task.Delay(300);
            return "lider-concluiu";
        });
        var seguidor = singleFlight.CoalesceAsync("chave", async _ => "nunca-executa", cts.Token);
        cts.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => seguidor);
        var (resultado, executado) = await lider;

        Assert.Equal("lider-concluiu", resultado);
        Assert.True(executado);
        Assert.Equal(0, singleFlight.InflightCount);
    }

    [Fact]
    public async Task Dicionario_nao_cresce()
    {
        var singleFlight = new SingleFlight();

        for (var i = 0; i < 100; i++)
            await singleFlight.CoalesceAsync($"chave-{i}", _ => Task.FromResult(i));
        Assert.Equal(0, singleFlight.InflightCount);

        // Rajada concluída também zera o dicionário.
        await Task.WhenAll(Enumerable.Range(0, 20).Select(_ =>
            singleFlight.CoalesceAsync("rajada", async _ =>
            {
                await Task.Delay(20);
                return 0;
            })));
        Assert.Equal(0, singleFlight.InflightCount);
    }
}
