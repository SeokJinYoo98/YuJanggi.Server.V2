using System;
using System.Collections.Generic;
using System.Text;
using YuJanggi.Protocol.V2.Messages;
using YuJanggi.Server.V2.ClientSession;
using YuJanggi.Server.V2.Transport;

namespace YuJanggi.Server.V2.Handlers
{
    internal interface IMessageHandler
    {
        Task HandleAsync(
            IClientSession session,
            ClientMessage message,
            CancellationToken cancellationToken);
    }
}
