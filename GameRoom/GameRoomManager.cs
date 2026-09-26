namespace YuJanggi.Server.V2.GameRoom
{
    using ClientSession;

    /// <summary>게임룸의 생성, 조회, 제거 및 수명을 관리합니다.</summary>
    internal sealed class GameRoomManager : IAsyncDisposable
    {
        private readonly ClientSessionManager _sessionManager;
        private readonly Lock _roomSync;
        private readonly Dictionary<string, GameRoom> _gameRooms = new();
        private bool _stopping;

        public GameRoomManager(ClientSessionManager sessionManager, Lock roomSync)
        {
            _sessionManager = sessionManager;
            _roomSync = roomSync;
        }

        public GameRoom CreateGameRoom(string matchId, IClientSession choPlayer, IClientSession hanPlayer)
        {
            var room = new GameRoom();
            room.Initialize(matchId, choPlayer, hanPlayer);
            lock (_roomSync)
            {
                if (_stopping)
                    throw new InvalidOperationException("종료 중인 서버에는 룸을 생성할 수 없습니다.");
                if (!_sessionManager.Contains(choPlayer.ClientId) ||
                    !_sessionManager.Contains(hanPlayer.ClientId))
                    throw new InvalidOperationException("연결이 종료된 참가자의 룸을 생성할 수 없습니다.");
                if (_gameRooms.ContainsKey(matchId))
                    throw new InvalidOperationException("이미 사용 중인 대국 ID입니다.");
                if (_gameRooms.Values.Any(candidate => candidate.Contains(choPlayer) || candidate.Contains(hanPlayer)))
                    throw new InvalidOperationException("이미 게임룸에 참가 중인 세션입니다.");
                _gameRooms.Add(matchId, room);
                return room;
            }
        }

        public bool TryGetRoom(string matchId, out GameRoom? room)
        {
            lock (_roomSync)
                return _gameRooms.TryGetValue(matchId, out room);
        }

        public GameRoom? GetRoomBySession(IClientSession session)
        {
            ArgumentNullException.ThrowIfNull(session);
            lock (_roomSync)
            {
                if (_stopping || !_sessionManager.Contains(session.ClientId))
                    return null;
                return _gameRooms.Values.SingleOrDefault(room => room.Contains(session));
            }
        }

        public Task RemoveRoomAsync(string matchId)
        {
            GameRoom? room;
            lock (_roomSync)
                _gameRooms.Remove(matchId, out room);
            room?.Close();
            return Task.CompletedTask;
        }

        public Task RemoveRoomsForPlayerAsync(Guid clientId)
        {
            GameRoom[] rooms;
            lock (_roomSync)
            {
                rooms = _gameRooms.Values.Where(room =>
                    room.ChoPlayer?.ClientId == clientId || room.HanPlayer?.ClientId == clientId).ToArray();
                foreach (var room in rooms)
                    _gameRooms.Remove(room.MatchId);
            }
            // Manager 잠금 밖에서 룸을 닫습니다. 연결은 서버가 소유합니다.
            // 상대에게 연결 종료를 알리는 이벤트와 복구 정책은 추후 Protocol 작업입니다.
            foreach (var room in rooms)
                room.Close();
            return Task.CompletedTask;
        }

        public Task ClearAsync()
        {
            GameRoom[] rooms;
            lock (_roomSync)
            {
                _stopping = true;
                rooms = _gameRooms.Values.ToArray();
                _gameRooms.Clear();
            }
            foreach (var room in rooms)
                room.Close();
            return Task.CompletedTask;
        }

        // 기존 서버 및 호출자의 비동기 정리 계약은 유지합니다. 대기할 엔진 작업은 없습니다.
        public ValueTask DisposeAsync() => new(ClearAsync());
    }
}
