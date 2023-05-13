using System;
using System.Diagnostics;
using System.Reactive.Linq;
using System.Threading.Tasks;
using System.Timers;
using Microsoft.Extensions.Logging;
using NetDaemon.AppModel;
using NetDaemon.HassModel;

namespace Apps;

[NetDaemonApp]
[Focus]
public sealed class PerfTestApp
{
    private readonly ILogger<HelloApp> _logger;
    private Stopwatch _stopwatch = new();
    public PerfTestApp(IHaContext ha, ILogger<HelloApp> logger)
    {
        _logger = logger;
        var nrOfMessagesReceived = 0;
        var _meassurementStarted = false;
        ha.StateAllChanges().Where(n => n.Entity.EntityId == "binary_sensor.vardagsrum_pir")
            .Subscribe(s =>
            {
                if (s.New?.State == "on")
                {
                    if (!_meassurementStarted)
                    {
                        _stopwatch.Start();
                        _meassurementStarted = true;
                    }
                    else
                    {
                        _stopwatch.Stop();
                        _logger.LogInformation("Performance measured APP: {MsgPerSecond} subscriptions/s ({NrOfMessagesReceived})", nrOfMessagesReceived/_stopwatch.Elapsed.TotalSeconds, nrOfMessagesReceived);
                    }
                }
                else
                {
                    nrOfMessagesReceived++;
                }
            });
    }

    public ValueTask DisposeAsync()
    {
        _logger.LogInformation("disposed app");
        return ValueTask.CompletedTask;
    }
}