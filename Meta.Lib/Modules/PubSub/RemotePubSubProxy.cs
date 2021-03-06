﻿using Meta.Lib.Modules.Logger;
using Meta.Lib.Modules.PubSub.Messages;
using System;
using System.Collections.Generic;
using System.IO.Pipes;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace Meta.Lib.Modules.PubSub
{
    internal class ConnectionScope
    {
        public string PipeName { get; set; }
        public string ServerName { get; set; }
        public int MillisecondsTimeout { get; set; }
        public int ReconnectionPeriod { get; set; }
        public CancellationTokenSource Cts { get; set; }
    }

    internal class RemotePubSubProxy : PipeConnection
    {
        readonly object _connectionLock = new object();
        readonly Dictionary<Type, Node> _nodes = new Dictionary<Type, Node>();

        public bool ConnectedOrConnecting => _connectionScope != null;

        ConnectionScope _connectionScope;
        int _isReconnecting;

        public RemotePubSubProxy(MessageHub hub, IMetaLogger logger)
            : base(hub, logger)
        {
        }

        #region Connection
        internal async Task<bool> Connect(
            string pipeName,
            int millisecondsTimeout = 5_000, 
            int reconnectionPeriod = 5_000, 
            string serverName = ".")
        {
            ConnectionScope scope;
            lock (_connectionLock)
            {
                if (_connectionScope != null)
                    throw new InvalidOperationException("The ConnectToServer() method has been already called. You need to call the Disconnect() method before attempting to establish a new connection.");

                scope = _connectionScope = new ConnectionScope
                {
                    PipeName = pipeName,
                    ServerName = serverName,
                    MillisecondsTimeout = millisecondsTimeout,
                    ReconnectionPeriod = reconnectionPeriod,
                    Cts = new CancellationTokenSource()
                };
            }

            _logger.Info($"Connecting to server '{_connectionScope.ServerName}{_connectionScope.PipeName}'... ");

            var pipe = await ConnectPipe(scope);

            if (pipe == null)
            {
                StartReconnectionLoop(scope);
                return false;
            }

            await Init(pipe);

            return true;
        }

        async Task Init(NamedPipeClientStream pipe)
        {
            InitPipeConnection(pipe);

            StartReadLoop();

            await ResubscribeAllMessages();

            _logger.Info($"Connected to server '{_connectionScope.ServerName}{_connectionScope.PipeName}'");
            
            FireConnectedEvent();

            try
            {
                await _hub.Publish(new ConnectedToServerEvent()
                {
                    Timestamp = DateTime.Now
                });
            }
            catch (Exception ex)
            {
                _logger.Error("Some of the subscribers of ConnectedToServerEvent generated exception: ", ex);
            }
        }

        async Task<NamedPipeClientStream> ConnectPipe(ConnectionScope connectionScope)
        {
            var pipe = new NamedPipeClientStream(
                connectionScope.ServerName,
                connectionScope.PipeName,
                PipeDirection.InOut,
                PipeOptions.Asynchronous);

            try
            {
                await pipe.ConnectAsync(connectionScope.MillisecondsTimeout, connectionScope.Cts.Token);
                Disconnected += RemotePubSubProxy_Disconnected;
                return pipe;
            }
            catch (Exception)
            {
                pipe.Dispose();

                if (connectionScope.ReconnectionPeriod > 0)
                    return null;

                throw;
            }
        }

        internal override async Task Disconnect()
        {
            ConnectionScope scope = null;
            lock (_connectionLock)
            {
                if (_connectionScope == null)
                    throw new InvalidOperationException("The ConnectToServer() method has not been called.");

                scope = _connectionScope;
                _connectionScope = null;
            }

            scope.Cts.Cancel();

            Disconnected -= RemotePubSubProxy_Disconnected;
            await base.Disconnect();

            try
            {
                await _hub.Publish(new DisconnectedFromServerEvent()
                {
                    Timestamp = DateTime.Now
                });
            }
            catch (Exception ex)
            {
                _logger.Error("Some of the subscribers of DisconnectedFromServerEvent generated exception: ", ex);
            }
        }

        async void RemotePubSubProxy_Disconnected(object sender, EventArgs e)
        {
            var scope = _connectionScope;
            if (scope != null)
            {
                StartReconnectionLoop(scope);

                try
                {
                    await _hub.Publish(new DisconnectedFromServerEvent()
                    {
                        Timestamp = DateTime.Now,
                        LostConnection = true
                    });
                }
                catch (Exception ex)
                {
                    _logger.Error("Some of the subscribers of DisconnectedFromServerEvent generated exception: ", ex);
                }
            }
        }

        internal void StartReconnectionLoop(ConnectionScope scope)
        {
            if (Interlocked.CompareExchange(ref _isReconnecting, 1, 0) == 0)
            {
                _logger.Info($"Starting reconnection loop to the server '{scope.ServerName}{scope.PipeName}'");

                Task.Run(async () =>
                {
                    await Task.Delay(scope.ReconnectionPeriod, scope.Cts.Token);

                    bool connected = false;
                    while (!connected && !scope.Cts.IsCancellationRequested)
                    {
                        try
                        {
                            var pipe = await ConnectPipe(scope);
                            if (pipe != null)
                            {
                                await Init(pipe);
                                connected = true;
                            }
                        }
                        catch (Exception)
                        {
                            await Task.Delay(scope.ReconnectionPeriod, scope.Cts.Token);
                        }
                    }

                    _logger.Info($"Finished reconnection loop to the server '{scope.ServerName}{scope.PipeName}'");

                    Interlocked.Exchange(ref _isReconnecting, 0);
                });
            }
        }
        #endregion Connection

        async Task ResubscribeAllMessages()
        {
            List<Type> nodes;
            lock (_nodes)
                nodes = _nodes.Keys.ToList();

            foreach (var item in nodes)
                await SendMessage(item.AssemblyQualifiedName, PipeMessageType.Subscribe);
        }

        public async Task Subscribe<TMessage>(Func<TMessage, Task> handler)
            where TMessage : class, IPubSubMessage
        {
            if (handler == null)
                throw new ArgumentNullException(nameof(handler));

            if (!_nodes.TryGetValue(typeof(TMessage), out Node node))
            {
                lock (_nodes)
                {
                    if (!_nodes.TryGetValue(typeof(TMessage), out node))
                    {
                        node = new Node();
                        _nodes.Add(typeof(TMessage), node);
                    }
                }
            }

            if (node.Find(x => x.Subscription.ActionEquals(handler)) == null)
            {
                var subscriber = new Subscriber(new Subscription<TMessage>(handler, null));
                node.Add(subscriber);

                if (node.Subscribers.Count == 1)
                {
                    await SendMessage(typeof(TMessage).AssemblyQualifiedName, PipeMessageType.Subscribe);
                    _logger.Trace($"Subscribed on server: '{typeof(TMessage).Name}'");
                }
            }
        }

        public async Task<bool> TrySubscribe<TMessage>(Func<TMessage, Task> handler)
            where TMessage : class, IPubSubMessage
        {
            try
            {
                await Subscribe(handler);
                return true;
            }
            catch (Exception ex)
            {
                _logger.Trace($"Failed to subscribe to '{typeof(TMessage).Name}': {ex.Message}");
                return false;
            }
        }

        public async Task Unsubscribe<TMessage>(Func<TMessage, Task> handler)
            where TMessage : class, IPubSubMessage
        {
            if (handler == null)
                throw new ArgumentNullException(nameof(handler));

            if (_nodes.TryGetValue(typeof(TMessage), out Node node))
            {
                var subscriber = node.Find(x => x.Subscription.ActionEquals(handler));

                if (subscriber != null)
                    node.Remove(subscriber);

                if (node.Subscribers.Count == 0)
                {
                    await SendMessage(typeof(TMessage).AssemblyQualifiedName, PipeMessageType.Unsubscribe);
                    _logger.Trace($"Unsubscribed on server: '{typeof(TMessage).Name}'");
                }
            }
        }

    }
}
