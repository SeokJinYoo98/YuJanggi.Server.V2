using System;

namespace YuJanggi.Server.V2.View
{
    using YuJanggi.Core;
    using YuJanggi.Protocol.V2.Connection;

    internal enum NetworkMessageType
    {
        Error,
        Message,
        Debug
    }

    internal static class NetworkView
    {
        private static readonly object OutputLock = new();

        public static void Write(NetworkMessageType messageType, string message, Guid? clientId = null)
        {
            string source = clientId.HasValue ? $"[{clientId.Value}]:" : "[Server]";
            string lines = message.ReplaceLineEndings(Environment.NewLine + $"[{messageType}]: ");
            lock (OutputLock)
            {
                Console.WriteLine($"{source}{Environment.NewLine}[{messageType}]: {lines}{Environment.NewLine}");
            }
        }

        public static void ShowHandShakeResult(
            Guid clientId,
            ProtocolHandshakeRequest request,
            ProtocolHandshakeResult result)
        {
            Write(
                result == ProtocolHandshakeResult.Success ? NetworkMessageType.Debug : NetworkMessageType.Error,
                $"Protocol: Client={request.YuJanggiProtocolVersion}, Server={Protocol.V2.ProtocolVersion.Current}" +
                Environment.NewLine +
                $"Core: Client={request.YuJanggiCoreVersion}, Server={CoreVersion.Current}" +
                Environment.NewLine +
                $"Handshake Result: {result}",
                clientId);
        }
    }
}
