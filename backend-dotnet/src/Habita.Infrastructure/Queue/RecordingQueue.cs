using System.Collections.Concurrent;
using System.Threading.Channels;
using Habita.Application;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Habita.Infrastructure.Queue;

/// <summary>Cola en memoria. Sustituto local de Azure Service Bus; ignora duplicados que aún esperan turno.</summary>
internal sealed class ChannelJobQueue : IJobQueue
{
    private readonly Channel<string> _channel = Channel.CreateUnbounded<string>(new UnboundedChannelOptions { SingleReader = true });
    private readonly ConcurrentDictionary<string, byte> _waiting = new();

    public void Enqueue(string recordingId)
    {
        if (_waiting.TryAdd(recordingId, 0)) _channel.Writer.TryWrite(recordingId);
    }

    public async IAsyncEnumerable<string> ReadAllAsync([System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct)
    {
        await foreach (var id in _channel.Reader.ReadAllAsync(ct))
        {
            _waiting.TryRemove(id, out _);
            yield return id;
        }
    }
}

/// <summary>Worker: procesa una grabación a la vez y, al arrancar, retoma las que quedaron a medias.</summary>
internal sealed class RecordingWorker(ChannelJobQueue queue, RecordingService service, IRecordingRepository repo, ILogger<RecordingWorker> log) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            foreach (var id in await repo.ListPendingIdsAsync(stoppingToken)) queue.Enqueue(id);
        }
        catch (Exception ex) when (!stoppingToken.IsCancellationRequested)
        {
            log.LogError(ex, "No se pudieron recuperar las grabaciones pendientes");
        }

        await foreach (var id in queue.ReadAllAsync(stoppingToken))
        {
            try
            {
                await service.ProcessAsync(id, stoppingToken);
            }
            catch (Exception ex) when (!stoppingToken.IsCancellationRequested)
            {
                log.LogError(ex, "Error en el worker con la grabación {RecordingId}", id);
            }
        }
    }
}
