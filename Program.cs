namespace YuJanggi.Server.V2
{
    using Server;
    public static class Program
    {
        private static readonly YuJanggiServer _server = new YuJanggiServer();

        public static async Task Main()
        {
            await _server.RunAsync();
        }
    }
}
