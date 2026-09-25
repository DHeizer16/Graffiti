using System.Threading.Channels;

namespace GlobalGraffitiWall.API;

public class PixelPlacementQueue
{
    private readonly Channel<PixelPlacementItem> _channel;

    public PixelPlacementQueue(int capacity = 50_000)
    {
        var options = new BoundedChannelOptions(capacity)
        {
            FullMode = BoundedChannelFullMode.Wait,
            SingleReader = true,
            SingleWriter = false
        };
        _channel = Channel.CreateBounded<PixelPlacementItem>(options);
    }

    public bool TryEnqueue(PixelPlacementItem item)
    {
        return _channel.Writer.TryWrite(item);
    }

    public ValueTask EnqueueAsync(PixelPlacementItem item, CancellationToken cancellationToken = default)
    {
        return _channel.Writer.WriteAsync(item, cancellationToken);
    }

    public ChannelReader<PixelPlacementItem> Reader => _channel.Reader;
}
