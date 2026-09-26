using YuJanggi.Core.Domain;
using YuJanggi.Server.V2.View;

namespace YuJanggi.Server.V2.GameRoom
{
    using ClientSession;

    /// <summary>게임룸의 생성, 대국 시작, 루프 종료 및 제거를 관리합니다.</summary>
    internal sealed class GameRoomManager : IAsyncDisposable
    {
        private readonly ClientSessionManager _sessionManager;
        private readonly Lock _roomSync;
        private readonly Dictionary<string, GameRoom> _gameRooms = new();
        private readonly Dictionary<string, Task> _roomTasks = new();
        private bool _stopping;

        public GameRoomManager(ClientSessionManager sessionManager, Lock roomSync)
        {
            _sessionManager = sessionManager;
            _roomSync = roomSync;
        }

        /// <summary>확정된 초·한 참가자와 포진으로 룸을 생성합니다. 실제 대국 시작은 별도입니다.</summary>
        public GameRoom CreateGameRoom(string matchId, IClientSession choPlayer, Formation choFormation,
            IClientSession hanPlayer, Formation hanFormation)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(matchId);
            lock (_roomSync)
            {
                if (_stopping)
                    throw new InvalidOperationException("종료 중인 서버에는 룸을 생성할 수 없습니다.");
                ArgumentNullException.ThrowIfNull(choPlayer);
                ArgumentNullException.ThrowIfNull(hanPlayer);
                if (!_sessionManager.Contains(choPlayer.ClientId) ||
                    !_sessionManager.Contains(hanPlayer.ClientId))
                    throw new InvalidOperationException("연결이 종료된 참가자의 룸을 생성할 수 없습니다.");
                if (_gameRooms.ContainsKey(matchId) || _roomTasks.ContainsKey(matchId))
                    throw new InvalidOperationException("이미 사용 중인 매칭 ID입니다.");

                var room = new GameRoom();
                room.Initialize(matchId, choPlayer, choFormation, hanPlayer, hanFormation);
                _gameRooms.Add(room.MatchId, room);
                _roomTasks.Add(room.MatchId, ObserveRoomAsync(room));
                return room;
            }
        }

        public bool TryGetRoom(string matchId, out GameRoom? room)
        {
            lock (_roomSync)
                return _gameRooms.TryGetValue(matchId, out room);
        }

        /// <summary>준비 상태를 기록합니다. 네트워크 준비 요청의 연결은 이후 핸들러에서 담당합니다.</summary>
        public void SetPlayerReady(string matchId, IClientSession session)
        {
            lock (_roomSync)
                _gameRooms[matchId].SetPlayerReady(session);
        }

        /// <summary>서버가 확정한 포진과 제한 시간으로 준비된 룸의 대국을 시작합니다.</summary>
        public bool TryStartGame(string matchId, float turnTime)
        {
            lock (_roomSync)
                return !_stopping && _gameRooms.TryGetValue(matchId, out var room)
                    && room.TryStartGame(turnTime);
        }

        private async Task ObserveRoomAsync(GameRoom room)
        {
            try
            {
                // 엔진 잠금 안에서 루프가 완료되어도 매니저 정리가 그 스레드에서 재진입하지 않습니다.
                await room.RunAsync().ConfigureAwait(ConfigureAwaitOptions.ForceYielding);
            }
            catch (Exception exception)
            {
                //NetworkView.Write(NetworkMessageType.Error,
                //    $"GameRoom {room.MatchId} 시간 루프 실패: {exception}");
            }
            finally
            {
                try
                {
                    await room.CloseAsync().ConfigureAwait(false);
                }
                catch (Exception exception)
                {
                    // 실패한 루프의 Task도 관찰하고 참조 정리는 반드시 수행합니다.
                    //NetworkView.Write(NetworkMessageType.Error,
                    //    $"GameRoom {room.MatchId} 종료 중 오류: {exception}");
                }
                finally
                {
                    lock (_roomSync)
                    {
                        _gameRooms.Remove(room.MatchId);
                        _roomTasks.Remove(room.MatchId);
                    }
                }
            }
        }

        /// <summary>대국 종료를 요청하고 루프 및 자원 정리 완료를 기다립니다.</summary>
        public Task EndGameAsync(string matchId) => RemoveRoomAsync(matchId);

        /// <summary>룸을 사용 목록에서 제거하고 종료 작업을 반환합니다. 잠금 밖에서 기다려야 합니다.</summary>
        public Task RemoveRoomAsync(string matchId)
        {
            lock (_roomSync)
            {
                Task completion = _roomTasks.GetValueOrDefault(matchId, Task.CompletedTask);
                if (_gameRooms.Remove(matchId, out var room))
                    room.EndGame();
                return completion;
            }
        }

        /// <summary>참가자의 룸들을 제거하고 루프 종료 작업을 반환합니다. 세션 제거와 같은 잠금으로 호출합니다.</summary>
        public Task RemoveRoomsForPlayerAsync(Guid clientId)
        {
            // TODO:
            // 연결 종료 시 룸은 중단하지만 상대에게 종료 이벤트를 보내는 프로토콜은 아직 없습니다.
            // 상대가 MatchingFound를 받았다면 Matched 상태로 남을 수 있습니다.
            // 대국 종료/재접속 프로토콜 구현 시 상대 알림과 복원 정책을 연결해야 합니다.
            lock (_roomSync)
            {
                var tasks = new List<Task>();
                foreach (var entry in _gameRooms.ToArray())
                {
                    if (entry.Value.ChoPlayer?.ClientId == clientId ||
                        entry.Value.HanPlayer?.ClientId == clientId)
                        tasks.Add(RemoveRoomAsync(entry.Key));
                }
                return Task.WhenAll(tasks);
            }
        }

        /// <summary>새 룸 생성을 막고, 제거 중인 룸을 포함한 모든 루프의 종료를 기다립니다.</summary>
        public async Task ClearAsync()
        {
            Task[] completions;
            lock (_roomSync)
            {
                _stopping = true;
                completions = _roomTasks.Values.ToArray();
                foreach (var room in _gameRooms.Values)
                    room.EndGame();
                _gameRooms.Clear();
            }
            await Task.WhenAll(completions).ConfigureAwait(false);
        }

        public ValueTask DisposeAsync() => new(ClearAsync());
    }
}
