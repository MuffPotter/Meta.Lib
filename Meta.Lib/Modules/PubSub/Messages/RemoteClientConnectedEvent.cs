﻿using System;

namespace Meta.Lib.Modules.PubSub.Messages
{
    public class RemoteClientConnectedEvent : PubSubMessageBase
    {
        public DateTime Timestamp { get; internal set; }
        public int TotalClientsCount { get; internal set; }
    }
}
