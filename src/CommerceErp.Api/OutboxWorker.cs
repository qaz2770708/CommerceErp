using CommerceErp.Infrastructure;
namespace CommerceErp.Api;
public sealed class OutboxWorker(OutboxProcessor processor,WorkerHealthService health):BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var name="业务事件/"+Guid.NewGuid().ToString("N");var nextPulse=DateTimeOffset.MinValue;
        try{while(!stoppingToken.IsCancellationRequested)
        {
            try{var processed=await processor.ProcessNextAsync(stoppingToken);if(DateTimeOffset.UtcNow>=nextPulse){health.Pulse(name);nextPulse=DateTimeOffset.UtcNow.AddSeconds(15);}if(!processed)await Task.Delay(TimeSpan.FromSeconds(2),stoppingToken);}
            catch(OperationCanceledException)when(stoppingToken.IsCancellationRequested){break;}
            catch(Exception e){try{health.Pulse(name,e is DatabaseBusyException?"DatabaseBusy":"ProcessorError");}catch{}await Task.Delay(TimeSpan.FromSeconds(5),stoppingToken);}
        }}finally{try{health.Pulse(name,"Stopped");}catch{}}
    }
}
