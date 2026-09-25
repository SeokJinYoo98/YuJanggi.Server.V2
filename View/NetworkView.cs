using System;

namespace YuJanggi.Server.V2.View
{
    using YuJanggi.Core;
    using YuJanggi.Protocol.V2.Connection;
    using YuJanggi.Protocol.V2.Matching;

    internal enum NetworkMessageType
    {
        Error,
        Message,
        Debug
    }

    internal static class NetworkView
    {
        private static readonly object OutputLock = new();

        public static void Write(
               NetworkMessageType messageType,
               string message,
               string? nickname = null)
        {
            string source =
                string.IsNullOrWhiteSpace(nickname)
                    ? "[Server]"
                    : $"[{nickname}]";

            string lines =
                message.ReplaceLineEndings(
                    Environment.NewLine +
                    $"[{messageType}]: ");

            lock (OutputLock)
            {
                Console.WriteLine(
                    $"{source}" +
                    Environment.NewLine +
                    $"[{messageType}]: {lines}" +
                    Environment.NewLine);
            }
        }

        public static void ShowMatchingFound(MatchingFound matchingFound)
        {
            Write(
                NetworkMessageType.Message,
                $"매칭 알림 전송 완료: {matchingFound.MatchId}" +
                Environment.NewLine +
                $"수신자 진영: {matchingFound.MyTeam}" +
                Environment.NewLine +
                $"상대: {matchingFound.Opponent.PlayerNickname} " +
                $"({matchingFound.Opponent.PlayerId}, {matchingFound.Opponent.PlayerTeam})");
        }

        public static void ShowHandShakeResult(
            string nickname,
            ProtocolHandshakeRequest request,
            ProtocolHandshakeResult result)
        {
            Write(
                result == ProtocolHandshakeResult.Success
                    ? NetworkMessageType.Debug
                    : NetworkMessageType.Error,
                $"Protocol: Client={request.YuJanggiProtocolVersion}, " +
                $"Server={Protocol.V2.ProtocolVersion.Current}" +
                Environment.NewLine +
                $"Core: Client={request.YuJanggiCoreVersion}, " +
                $"Server={CoreVersion.Current}" +
                Environment.NewLine +
                $"Handshake Result: {result}",
                nickname);
        }
    }
}
