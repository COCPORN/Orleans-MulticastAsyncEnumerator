//
// Heavily inspired by this: https://gist.github.com/egil/c517eba3aacb60777e629eff4743c80a
//

using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;

namespace Orleans.Runtime;

#nullable enable

/// <summary>
/// Represents a multi-cast <see cref="IAsyncEnumerable{T}"/> where
/// each reader can consume the <typeparamref name="T"/> items
/// at its own pace.
/// </summary>
/// <typeparam name="T">The item type produced by the enumerable.</typeparam>
public sealed class MulticastAsyncEnumerable<T> : IAsyncEnumerable<T>
{
    private readonly UnboundedChannelOptions _channelOptions;
    private readonly object _activeChannelsLock = new();
    private ImmutableArray<Channel<T>> _activeChannels = ImmutableArray<Channel<T>>.Empty;

    private readonly IAsyncEnumerable<T> _source;
    private Task? _sourceTask;

    /// <param name="source">The source <see cref="IAsyncEnumerable{T}"/>.</param>
    public MulticastAsyncEnumerable(IAsyncEnumerable<T> source)
    {
        _channelOptions = new() { AllowSynchronousContinuations = false, SingleReader = false, SingleWriter = true, };
        _source = source;
    }

    /// <summary>
    /// Writes the <paramref name="item"/> to any readers.
    /// </summary>
    /// <param name="item">The item to write.</param>
    public void Write(T item)
    {
        foreach (var channel in _activeChannels)
        {
            channel.Writer.TryWrite(item);
        }
    }

    /// <summary>
    /// When the source has completed, fire off this action.
    /// </summary>
    public Action Completed { get; set; } = () => { };

    /// <summary>
    /// Mark all <see cref="IAsyncEnumerable{T}"/> streams
    /// as completed.
    /// </summary>
    public void Complete()
    {
        var channels = _activeChannels;

        lock (_activeChannelsLock)
        {
            _activeChannels = ImmutableArray<Channel<T>>.Empty;
        }

        foreach (var channel in channels)
        {
            channel.Writer.TryComplete();
        }

        Completed();
    }

    /// <summary>
    /// Gets an <see cref="IAsyncEnumerator{T}"/> that can be used to
    /// read the items from the enumerable.
    /// </summary>
    /// <param name="cancellationToken">An optional cancellation token.</param>
    /// <returns>The <see cref="IAsyncEnumerator{T}"/>.</returns>
    public async IAsyncEnumerator<T> GetAsyncEnumerator(CancellationToken cancellationToken = default)
    {
        var reader = Subscribe();

        StartSourceReader();

        try
        {
            while (await reader.WaitToReadAsync(cancellationToken).ConfigureAwait(false))
            {
                while (reader.TryRead(out var item))
                {
                    yield return item;
                }
            }
        }
        finally
        {
            Unsubscribe(reader);
        }
    }

    private void StartSourceReader()
    {
        if (_sourceTask is null)
        {
            _sourceTask = Task.Run(async () =>
            {
                await Task.Delay(200);
                try
                {
                    await foreach (var item in _source.ConfigureAwait(false))
                    {
                        Write(item);
                    }
                }
                finally
                {
                    Complete();
                }
            });
        }
    }

    private ChannelReader<T> Subscribe()
    {
        var channel = Channel.CreateUnbounded<T>(_channelOptions);

        lock (_activeChannelsLock)
        {
            _activeChannels = _activeChannels.Add(channel);
        }

        return channel.Reader;
    }

    private void Unsubscribe(ChannelReader<T> reader)
    {
        if (_activeChannels.FirstOrDefault(x => ReferenceEquals(x.Reader, reader)) is Channel<T> channel)
        {
            lock (_activeChannelsLock)
            {
                _activeChannels = _activeChannels.Remove(channel);
            }

            channel.Writer.TryComplete();
        }
    }
}