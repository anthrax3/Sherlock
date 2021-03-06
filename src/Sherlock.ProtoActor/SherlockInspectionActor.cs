﻿using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Grpc.Core;
using Microsoft.Extensions.Logging;
using Proto;
using Proto.Schedulers.SimpleScheduler;
using Sherlock.Client;
using Sherlock.ProtoActor.Messages;
using Sherlock.Services;

namespace Sherlock.ProtoActor
{
    public class SherlockInspectionOptions
    {
        public int StartupDelayMilliseconds { get; set; }
        public int IntervalMilliseconds { get; set; }

        public SherlockInspectionOptions()
        {
            this.StartupDelayMilliseconds = 5000;
            this.IntervalMilliseconds = 5000;
        }
    }

    public class SherlockInspectionActor : IActor
    {
        private readonly ISimpleScheduler _scheduler;
        private readonly TrackedStateMap _reports = new TrackedStateMap();
        private CancellationTokenSource _cts;
        public static PID Pid { get; private set; }
        private readonly HashSet<PID> _targets = new HashSet<PID>();
        private readonly ISherlockClient _client;
        private readonly SherlockInspectionOptions _options;
        private readonly ILogger _logger = Proto.Log.CreateLogger<SherlockInspectionActor>();

        public SherlockInspectionActor(
            ISimpleScheduler scheduler,
            ISherlockClient client,
            SherlockInspectionOptions options
        )
        {
            _scheduler = scheduler;
            _client = client;
            _options = options;
        }

        public async Task ReceiveAsync(IContext context)
        {
            switch (context.Message)
            {
                case Started _:
                {
                    SherlockInspectionActor.Pid = context.Self;
                    _scheduler.ScheduleTellRepeatedly(
                        TimeSpan.FromMilliseconds(_options.StartupDelayMilliseconds),
                        TimeSpan.FromMilliseconds(_options.IntervalMilliseconds),
                        context.Self,
                        Inspect.Instance,
                        out _cts
                    );
                    break;
                }

                case Stopping _:
                {
                    _cts.Cancel();
                    break;
                }

                case AddToInspection add:
                {
                    _targets.Add(add.ActorId);
                    break;
                }

                case Inspect i:
                {
                    if (!_targets.Any())
                    {
                        _logger.LogWarning("No targets configued");
                        return;
                    }

                    // send reports
                    try
                    {
                        await _client.PushAsync(_reports.Reports.Values).ConfigureAwait(false);
                    }
                    catch (RpcException ex)
                    {
                        _logger.LogDebug("Sherlock server connection error: {message}", ex.Message);
                        if (ex.Status.StatusCode != StatusCode.Unavailable)
                        {
                            throw;
                        }
                    }

                    // refresh reports
                    foreach (var t in _targets)
                    {
                        t.Request(Inspect.Instance, context.Self);
                    }

                    break;
                }

                case ReportState req:
                {
                    context.Sender.Tell(_reports);
                    break;
                }

                case TrackedState report:
                {
                    _reports.Reports[report.ActorId] = report;
                    break;
                }
            }
        }
    }
}
