using System.Net;
using System.Net.Sockets;
using System.Reflection;
using System.Text.Json;
using YuJanggi.Protocol.V2.Framing;
using YuJanggi.Protocol.V2.Messages;
using YuJanggi.Protocol.V2.Matching;
using YuJanggi.Protocol.V2.Connection;
using YuJanggi.Protocol.V2.Serialization;
using YuJanggi.Server.V2.ClientSession;
using YuJanggi.Server.V2.Handlers;
using YuJanggi.Server.V2.GameRoom;
using YuJanggi.Server.V2.Matching;
using YuJanggi.Server.V2.Server;
using YuJanggi.Server.V2.Transport;

using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(20));
var token = timeout.Token;
await Run("신청·중복·취소·재신청·FIFO·동일 세션 방어", async () =>
{
    using var first = await Peer.Create();
    using var second = await Peer.Create();
    var service = new MatchMakingService();
    Check(service.RequestMatch(first.Session, out var pair) == MatchingResult.Accepted && pair is null);
    Check(service.RequestMatch(first.Session, out pair) == MatchingResult.AlreadyMatching && pair is null);
    Check(service.CancelMatch(first.Session) == MatchingCancelResult.Cancelled);
    Check(service.CancelMatch(first.Session) == MatchingCancelResult.Cancelled);
    Check(service.RequestMatch(first.Session, out _) == MatchingResult.Accepted);
    Check(service.RequestMatch(second.Session, out pair) == MatchingResult.Accepted);
    Check(pair?.First == first.Session && pair.Second == second.Session);
    Check(pair!.First.ClientId != pair.Second.ClientId);
    var queue = new MatchMakingQueue();
    queue.Enqueue(first.Session);
    Check(!queue.TryDequeue(out var a, out var b) && a is null && b is null && queue.Count == 1);
    queue.Enqueue(second.Session);
    Check(queue.TryDequeue(out a, out b) && a == first.Session && b == second.Session && queue.Count == 0);
});
await Run("Handshake 전 거절 및 성공 후 신청", async () =>
{
    using var peer = await Peer.Create(false);
    var handler = new MatchingHandler(new MatchMakingService(), CreateRoomManager(peer.Session));
    await handler.HandleAsync(peer.Session, Request("before"), token);
    var response = await peer.Read(token);
    Check(response.RequestId == "before" && response.GetPayload<MatchingResponse>().Result == MatchingResult.HandshakeRequired);
    await new ProtocolHandshakeHandler().HandleAsync(peer.Session, new ClientMessage
    {
        Type = ClientMessageType.ProtocolHandshake, RequestId = "handshake",
        Payload = JsonSerializer.SerializeToElement(new ProtocolHandshakeRequest
        {
            YuJanggiProtocolVersion = YuJanggi.Protocol.V2.ProtocolVersion.Current,
            YuJanggiCoreVersion = YuJanggi.Core.CoreVersion.Current
        })
    }, token);
    Check((await peer.Read(token)).Type == ServerMessageType.ProtocolHandshake);
    Check(peer.Session.IsHandshakeCompleted);
    await handler.HandleAsync(peer.Session, Request("after"), token);
    Check((await peer.Read(token)).GetPayload<MatchingResponse>().Result == MatchingResult.Accepted);
});
await Run("동시 신청에서 양쪽 응답 후 동일 MatchingFound 전달", async () =>
{
    using var first = await Peer.Create();
    using var second = await Peer.Create();
    var handler = new MatchingHandler(new MatchMakingService(), CreateRoomManager(first.Session, second.Session));
    var sendLock = first.SendLock;
    await sendLock.WaitAsync(token);
    var firstTask = handler.HandleAsync(first.Session, Request("first"), token);
    var secondTask = handler.HandleAsync(second.Session, Request("second"), token);
    Check((await second.Read(token)).Type == ServerMessageType.MatchingResponse);
    Check(!secondTask.IsCompleted && !firstTask.IsCompleted);
    sendLock.Release();
    await Task.WhenAll(firstTask, secondTask);
    var response = await first.Read(token);
    Check(response.Type == ServerMessageType.MatchingResponse && response.RequestId == "first");
    var found1 = await first.Read(token);
    var found2 = await second.Read(token);
    Check(found1.Type == ServerMessageType.MatchingFound && found2.Type == ServerMessageType.MatchingFound);
    Check(found1.RequestId is null && found2.RequestId is null);
    Check(found1.GetPayload<MatchingFound>() == found2.GetPayload<MatchingFound>());
    Check(found1.GetPayload<MatchingFound>().ChoPlayer.PlayerId == first.Session.ClientId.ToString());
});
await Run("취소 응답 및 대기 중 연결 종료 정리", async () =>
{
    using var first = await Peer.Create();
    using var second = await Peer.Create();
    var server = new YuJanggiServer();
    var service = (MatchMakingService)Field(server, "_matchMakingService");
    var handler = new MatchingHandler(service, (GameRoomManager)Field(server, "_gameRoomManager"));
    await handler.HandleAsync(first.Session, new ClientMessage
    { Type = ClientMessageType.MatchingCancelRequest, RequestId = "cancel" }, token);
    var response = await first.Read(token);
    Check(response.RequestId == "cancel" && response.GetPayload<MatchingCancelResponse>().Result == MatchingCancelResult.Cancelled);
    service.RequestMatch(first.Session, out _);
    var sessions = (ClientSessionManager)Field(server, "_sessionManager");
    Check(sessions.Add(first.Session));
    var processing = (Task)typeof(YuJanggiServer)
        .GetMethod("HandleClientAsync", BindingFlags.Instance | BindingFlags.NonPublic)!
        .Invoke(server, [first.Session, token])!;
    first.Client.Dispose();
    await processing.WaitAsync(token);
    Check(!sessions.Contains(first.Session.ClientId));
    Check(service.RequestMatch(second.Session, out var pair) == MatchingResult.Accepted && pair is null);
    // 세션 목록이 이미 비워진 서버 종료 경로에서도 큐 정리가 실행되어야 합니다.
    typeof(YuJanggiServer).GetMethod("DisconnectClient", BindingFlags.Instance | BindingFlags.NonPublic)!
        .Invoke(server, [second.Session]);
    using var third = await Peer.Create();
    Check(service.RequestMatch(third.Session, out pair) == MatchingResult.Accepted && pair is null);
});
await Run("진입 전 토큰 취소 시 큐 미등록", async () =>
{
    using var first = await Peer.Create();
    using var second = await Peer.Create();
    var service = new MatchMakingService();
    var handler = new MatchingHandler(service, CreateRoomManager(first.Session, second.Session));
    using var cancelled = new CancellationTokenSource();
    cancelled.Cancel();
    await ExpectFailure(() => handler.HandleAsync(first.Session, Request("cancelled"), cancelled.Token));
    service.RequestMatch(second.Session, out var pair);
    Check(pair is null);
});
await Run("응답 전송 실패 시 큐 제거", async () =>
{
    using var first = await Peer.Create();
    using var second = await Peer.Create();
    var service = new MatchMakingService();
    var handler = new MatchingHandler(service, CreateRoomManager(first.Session, second.Session));
    first.Session.Dispose();
    await ExpectFailure(() => handler.HandleAsync(first.Session, Request("failed"), token));
    service.RequestMatch(second.Session, out var pair);
    Check(pair is null);
});
await Run("응답 대기 중 취소 시 이미 생성된 쌍의 이벤트 억제", async () =>
{
    using var first = await Peer.Create();
    using var second = await Peer.Create();
    var handler = new MatchingHandler(new MatchMakingService(), CreateRoomManager(first.Session, second.Session));
    using var cancelled = new CancellationTokenSource();
    await first.SendLock.WaitAsync(token);
    var firstTask = handler.HandleAsync(first.Session, Request("first"), cancelled.Token);
    var secondTask = handler.HandleAsync(second.Session, Request("second"), token);
    Check((await second.Read(token)).Type == ServerMessageType.MatchingResponse);
    cancelled.Cancel();
    await ExpectFailure(() => firstTask);
    await secondTask;
    Check(!second.Client.GetStream().DataAvailable);
    first.SendLock.Release();
});
await Run("쌍 생성 직후 연결 종료 및 두 번째 이벤트 실패", async () =>
{
    using var first = await Peer.Create();
    using var second = await Peer.Create();
    var handler = new MatchingHandler(new MatchMakingService(), CreateRoomManager(first.Session, second.Session));
    await handler.HandleAsync(first.Session, Request("first"), token);
    await first.Read(token);
    await first.SendLock.WaitAsync(token);
    var matching = handler.HandleAsync(second.Session, Request("second"), token);
    Check((await second.Read(token)).Type == ServerMessageType.MatchingResponse);
    second.Session.Dispose();
    first.SendLock.Release();
    await ExpectFailure(() => matching);
    Check((await first.Read(token)).Type == ServerMessageType.MatchingFound);
    // 부분 전송 결과는 TODO로 명시한 현재 한계입니다. 복구 성공을 주장하지 않습니다.
});
await Run("다수 동시 신청의 원자성과 중복 방어", async () =>
{
    using var first = await Peer.Create();
    var service = new MatchMakingService();
    var results = await Task.WhenAll(Enumerable.Range(0, 32).Select(_ => Task.Run(() =>
    {
        var result = service.RequestMatch(first.Session, out var pair);
        Check(pair is null);
        return result;
    })));
    Check(results.Count(r => r == MatchingResult.Accepted) == 1);
    Check(results.Count(r => r == MatchingResult.AlreadyMatching) == 31);
});
Console.WriteLine("전체 9개 검증 그룹 통과");

static GameRoomManager CreateRoomManager(params IClientSession[] participants)
{
    // 실제 서버처럼 등록된 참가자만 룸을 생성할 수 있도록 테스트 세션을 등록합니다.
    var sessions = new ClientSessionManager();
    foreach (var participant in participants)
        Check(sessions.Add(participant));

    return new GameRoomManager(sessions, new Lock());
}
static ClientMessage Request(string id) => new() { Type = ClientMessageType.MatchingRequest, RequestId = id };
static void Check(bool condition) { if (!condition) throw new Exception("검증 실패"); }
static async Task Run(string name, Func<Task> test) { await test(); Console.WriteLine($"통과: {name}"); }
static async Task ExpectFailure(Func<Task> action)
{
    try { await action(); }
    catch (Exception ex) when (ex is OperationCanceledException or ObjectDisposedException or IOException) { return; }
    throw new Exception("예상한 전송 실패 또는 취소가 발생하지 않았습니다.");
}
static object Field(object instance, string name) => instance.GetType()
    .GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(instance)!;

sealed class Peer : IDisposable
{
    public required TcpClient Client { get; init; }
    public required ClientSession Session { get; init; }
    public SemaphoreSlim SendLock => (SemaphoreSlim)typeof(TcpClientConnection)
        .GetField("_sendLock", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(Session.Connection)!;
    public static async Task<Peer> Create(bool handshake = true)
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var client = new TcpClient();
        var connect = client.ConnectAsync((IPEndPoint)listener.LocalEndpoint);
        var accepted = await listener.AcceptTcpClientAsync();
        await connect;
        var session = new ClientSession(new TcpClientConnection(accepted), "검증 클라이언트");
        if (handshake) session.CompleteHandshake();
        return new Peer { Client = client, Session = session };
    }
    public async Task<ServerMessage> Read(CancellationToken token)
    {
        var stream = Client.GetStream();
        byte[] header = new byte[MessageFramer.HeaderSize];
        await stream.ReadExactlyAsync(header, token);
        byte[] body = new byte[MessageFramer.DecodeBodyLength(header)];
        await stream.ReadExactlyAsync(body, token);
        return MessageSerializer.Deserialize<ServerMessage>(body);
    }
    public void Dispose() { Session.Dispose(); Client.Dispose(); }
}
