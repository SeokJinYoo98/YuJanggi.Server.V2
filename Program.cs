namespace YuJanggi.Server.V2
{
    using Server;
    public static class Program
    {
        private static readonly YuJanggiServer _server = new YuJanggiServer();

        public static async Task Main()
        {
            try
            {
                await _server.RunAsync();
            }
            catch (Exception exception)
            {
                View.NetworkView.Write(View.NetworkMessageType.Error, exception.ToString());
                Environment.ExitCode = 1;
            }
        }
    }
}
