using System;
using System.Collections.Generic;
using System.Text;

namespace YuJanggi.Server.V2.View
{
    using YuJanggi.Core;
    using YuJanggi.Protocol.V2.Connection;

    internal static class NetworkView
    {
        public static void ShowHandShakeResult(
            string clientInfo,
            ProtocolHandshakeRequest request,
            ProtocolHandshakeResult result)
        {
            Console.WriteLine($"Client: {clientInfo}");
            Console.WriteLine(
                $"Protocol: Client={request.YuJanggiProtocolVersion}, " +
                $"Server={Protocol.V2.ProtocolVersion.Current}");

            Console.WriteLine(
                $"Core: Client={request.YuJanggiCoreVersion}, " +
                $"Server={CoreVersion.Current}");

            Console.WriteLine(
                $"Handshake Result: {result}");
        }
    }
}
