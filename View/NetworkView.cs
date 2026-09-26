using System;

namespace YuJanggi.Server.V2.View
{
    using System.Text.Json;
    using YuJanggi.Core;
    using YuJanggi.Protocol.V2.Connection;
    using YuJanggi.Protocol.V2.Matching;
    using YuJanggi.Protocol.V2.Messages;

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

        public static void ShowReceiveMessage(
            string? nickname,
            ClientMessage message)
        {
            Write(
                NetworkMessageType.Debug,
                $"Receive" +
                Environment.NewLine +
                $"Type: {message.Type}" +
                Environment.NewLine +
                $"RequestId: {message.RequestId ?? "None"}" +
                Environment.NewLine +
                $"Payload: {GetPayloadText(message.Payload)}",
                nickname);
        }

        public static void ShowSendMessage(
            string? nickname,
            ServerMessage message)
        {
            Write(
                NetworkMessageType.Debug,
                $"Send" +
                Environment.NewLine +
                $"Type: {message.Type}" +
                Environment.NewLine +
                $"RequestId: {message.RequestId ?? "None"}" +
                Environment.NewLine +
                $"Payload: {GetPayloadText(message.Payload)}",
                nickname);
        }

        private static string GetPayloadText(JsonElement? payload)
        {
            if (payload is null)
                return "None";

            return payload.Value.GetRawText();
        }
    }
}
