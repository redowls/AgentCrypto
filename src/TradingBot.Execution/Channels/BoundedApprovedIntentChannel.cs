using System.Threading.Channels;
using Microsoft.Extensions.Options;
using TradingBot.Execution.Configuration;

namespace TradingBot.Execution.Channels;

public sealed class BoundedApprovedIntentChannel : IApprovedIntentChannel
{
    private readonly Channel<ApprovedIntent> _channel;

    public BoundedApprovedIntentChannel(IOptions<ExecutionOptions> options)
    {
        Capacity = Math.Max(16, options.Value.IntentChannelCapacity);
        _channel = Channel.CreateBounded<ApprovedIntent>(new BoundedChannelOptions(Capacity)
        {
            FullMode     = BoundedChannelFullMode.Wait,
            SingleReader = true,   // ExecutionEngine is the sole consumer.
            SingleWriter = false,  // SignalApprovalHostedService + tests may write.
        });
    }

    public ChannelReader<ApprovedIntent> Reader => _channel.Reader;
    public ChannelWriter<ApprovedIntent> Writer => _channel.Writer;
    public int CurrentCount => _channel.Reader.CanCount ? _channel.Reader.Count : -1;
    public int Capacity { get; }
}
