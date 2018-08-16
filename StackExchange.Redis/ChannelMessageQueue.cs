﻿using System;
using System.Reflection;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;

namespace StackExchange.Redis
{
    /// <summary>
    /// Represents a message that is broadcast via pub/sub
    /// </summary>
    public readonly struct ChannelMessage
    {
        private readonly ChannelMessageQueue _queue; // this is *smaller* than storing a RedisChannel for the subsribed channel
        /// <summary>
        /// See Object.ToString
        /// </summary>
        public override string ToString() => ((string)Channel) + ":" + ((string)Message);

        /// <summary>
        /// See Object.GetHashCode
        /// </summary>
        public override int GetHashCode() => Channel.GetHashCode() ^ Message.GetHashCode();

        /// <summary>
        /// See Object.Equals
        /// </summary>
        /// <param name="obj">The <see cref="object"/> to compare.</param>
        public override bool Equals(object obj) => obj is ChannelMessage cm
            && cm.Channel == Channel && cm.Message == Message;
        internal ChannelMessage(ChannelMessageQueue queue, RedisChannel channel, RedisValue value)
        {
            _queue = queue;
            Channel = channel;
            Message = value;
        }

        /// <summary>
        /// The channel that the subscription was created from
        /// </summary>
        public RedisChannel SubscriptionChannel => _queue.Channel;

        /// <summary>
        /// The channel that the message was broadcast to
        /// </summary>
        public RedisChannel Channel { get; }
        /// <summary>
        /// The value that was broadcast
        /// </summary>
        public RedisValue Message { get; }
    }

    /// <summary>
    /// Represents a message queue of ordered pub/sub notifications
    /// </summary>
    /// <remarks>To create a ChannelMessageQueue, use ISubscriber.Subscribe[Async](RedisKey)</remarks>
    public sealed class ChannelMessageQueue
    {
        private readonly Channel<ChannelMessage> _queue;
        /// <summary>
        /// The Channel that was subscribed for this queue
        /// </summary>
        public RedisChannel Channel { get; }
        private RedisSubscriber _parent;

        /// <summary>
        /// See Object.ToString
        /// </summary>
        public override string ToString() => (string)Channel;

        /// <summary>
        /// An awaitable task the indicates completion of the queue (including drain of data)
        /// </summary>
        public Task Completion => _queue.Reader.Completion;

        internal ChannelMessageQueue(RedisChannel redisChannel, RedisSubscriber parent)
        {
            Channel = redisChannel;
            _parent = parent;
            _queue = System.Threading.Channels.Channel.CreateUnbounded<ChannelMessage>(s_ChannelOptions);
        }

        private static readonly UnboundedChannelOptions s_ChannelOptions = new UnboundedChannelOptions
        {
            SingleWriter = true,
            SingleReader = false,
            AllowSynchronousContinuations = false,
        };
        internal void Subscribe(CommandFlags flags) => _parent.Subscribe(Channel, HandleMessage, flags);
        internal Task SubscribeAsync(CommandFlags flags) => _parent.SubscribeAsync(Channel, HandleMessage, flags);

        private void HandleMessage(RedisChannel channel, RedisValue value)
        {
            var writer = _queue.Writer;
            if (channel.IsNull && value.IsNull) // see ForSyncShutdown
            {
                writer.TryComplete();
            }
            else
            {
                writer.TryWrite(new ChannelMessage(this, channel, value));
            }
        }

        /// <summary>
        /// Consume a message from the channel.
        /// </summary>
        /// <param name="cancellationToken">The <see cref="CancellationToken"/> to use.</param>
        public ValueTask<ChannelMessage> ReadAsync(CancellationToken cancellationToken = default)
            => _queue.Reader.ReadAsync(cancellationToken);

        /// <summary>
        /// Attempt to synchronously consume a message from the channel.
        /// </summary>
        /// <param name="item">The <see cref="ChannelMessage"/> read from the Channel.</param>
        public bool TryRead(out ChannelMessage item) => _queue.Reader.TryRead(out item);

        /// <summary>
        /// Attempt to query the backlog length of the queue.
        /// </summary>
        /// <param name="count">The (approximate) count of items in the Channel.</param>
        public bool TryGetCount(out int count)
        {
            // get this using the reflection
            try
            {
                var prop = _queue.GetType().GetProperty("ItemsCountForDebugger", BindingFlags.Instance | BindingFlags.NonPublic);
                if (prop != null)
                {
                    count = (int)prop.GetValue(_queue);
                    return true;
                }
            }
            catch { }
            count = default;
            return false;
        }

        private Delegate _onMessageHandler;
        private void AssertOnMessage(Delegate handler)
        {
            if (handler == null) throw new ArgumentNullException(nameof(handler));
            if (Interlocked.CompareExchange(ref _onMessageHandler, handler, null) != null)
                throw new InvalidOperationException("Only a single " + nameof(OnMessage) + " is allowed");
        }

        /// <summary>
        /// Create a message loop that processes messages sequentially.
        /// </summary>
        /// <param name="handler">The handler to run when receiving a message.</param>
        public void OnMessage(Action<ChannelMessage> handler)
        {
            AssertOnMessage(handler);
            using (ExecutionContext.SuppressFlow())
            {
                ThreadPool.QueueUserWorkItem(
                state => ((ChannelMessageQueue)state).OnMessageSyncImpl(), this);
            }
        }

        private async void OnMessageSyncImpl()
        {
            var handler = (Action<ChannelMessage>)_onMessageHandler;
            while (!Completion.IsCompleted)
            {
                ChannelMessage next;
                try { if (!TryRead(out next)) next = await ReadAsync().ConfigureAwait(false); }
                catch (ChannelClosedException) { break; } // expected
                catch (Exception ex)
                {
                    _parent.multiplexer?.OnInternalError(ex);
                    break;
                }

                try { handler(next); }
                catch { } // matches MessageCompletable
            }
        }

        /// <summary>
        /// Create a message loop that processes messages sequentially.
        /// </summary>
        /// <param name="handler">The handler to execute when receiving a message.</param>
        public void OnMessage(Func<ChannelMessage, Task> handler)
        {
            AssertOnMessage(handler);
            using (ExecutionContext.SuppressFlow())
            {
                ThreadPool.QueueUserWorkItem(
                state => ((ChannelMessageQueue)state).OnMessageAsyncImpl(), this);
            }
        }

        private async void OnMessageAsyncImpl()
        {
            var handler = (Func<ChannelMessage, Task>)_onMessageHandler;
            while (!Completion.IsCompleted)
            {
                ChannelMessage next;
                try { if (!TryRead(out next)) next = await ReadAsync().ConfigureAwait(false); }
                catch (ChannelClosedException) { break; } // expected
                catch (Exception ex)
                {
                    _parent.multiplexer?.OnInternalError(ex);
                    break;
                }

                try
                {
                    var task = handler(next);
                    if (task != null && task.Status != TaskStatus.RanToCompletion) await task.ConfigureAwait(false);
                }
                catch { } // matches MessageCompletable
            }
        }

        internal void UnsubscribeImpl(Exception error = null, CommandFlags flags = CommandFlags.None)
        {
            var parent = _parent;
            _parent = null;
            if (parent != null)
            {
                parent.UnsubscribeAsync(Channel, HandleMessage, flags);
            }
            _queue.Writer.TryComplete(error);
        }

        internal async Task UnsubscribeAsyncImpl(Exception error = null, CommandFlags flags = CommandFlags.None)
        {
            var parent = _parent;
            _parent = null;
            if (parent != null)
            {
                await parent.UnsubscribeAsync(Channel, HandleMessage, flags).ConfigureAwait(false);
            }
            _queue.Writer.TryComplete(error);
        }

        internal static bool IsOneOf(Action<RedisChannel, RedisValue> handler)
        {
            try
            {
                return handler?.Target is ChannelMessageQueue
                    && handler.Method.Name == nameof(HandleMessage);
            }
            catch
            {
                return false;
            }
        }

        /// <summary>
        /// Stop receiving messages on this channel.
        /// </summary>
        /// <param name="flags">The flags to use when unsubscribing.</param>
        public void Unsubscribe(CommandFlags flags = CommandFlags.None) => UnsubscribeImpl(null, flags);

        /// <summary>
        /// Stop receiving messages on this channel.
        /// </summary>
        /// <param name="flags">The flags to use when unsubscribing.</param>
        public Task UnsubscribeAsync(CommandFlags flags = CommandFlags.None) => UnsubscribeAsyncImpl(null, flags);
    }
}
