﻿using Meta.Lib.Utils;
using System;
using System.Threading;
using System.Threading.Tasks;

namespace Meta.Lib.Modules.PubSub
{
    internal class RequestResponseProcessor
    {
        readonly MessageHub _hub;

        public RequestResponseProcessor(MessageHub hub)
        {
            _hub = hub;
        }

        public async Task<TMessage> When<TMessage>(int millisecondsTimeout, CancellationToken cancellationToken)
            where TMessage : class, IPubSubMessage
        {
            var tcs = new TaskCompletionSource<TMessage>();

            Task Handler(TMessage x)
            {
                tcs.SetResult(x);
                return Task.CompletedTask;
            }

            _hub.Subscribe((Func<TMessage, Task>)Handler, null);

            try
            {
                return await tcs.Task.TimeoutAfter(millisecondsTimeout, cancellationToken);
            }
            finally
            {
                _hub.Unsubscribe((Func<TMessage, Task>)Handler);
            }
        }

        public async Task<TResponse> Process<TResponse>(IPubSubMessage message, int millisecondsTimeout, CancellationToken cancellationToken)
            where TResponse : class, IPubSubMessage
        {
            var tcs = new TaskCompletionSource<TResponse>();

            Task Handler(TResponse response)
            {
                tcs.SetResult(response);
                return Task.CompletedTask;
            }

            _hub.Subscribe((Func<TResponse, Task>)Handler, null);

            try
            {
                await _hub.Publish(message);
                return await tcs.Task.TimeoutAfter(millisecondsTimeout, cancellationToken);
            }
            finally
            {
                _hub.Unsubscribe((Func<TResponse, Task>)Handler);
            }
        }
    }
}