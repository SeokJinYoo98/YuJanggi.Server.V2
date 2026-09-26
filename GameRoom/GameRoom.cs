using System.Diagnostics;
using YuJanggi.Core.Board;
using YuJanggi.Core.Domain;
using YuJanggi.Core.Match;
using YuJanggi.Core.Rule;

namespace YuJanggi.Server.V2.GameRoom
{
    using ClientSession;
    using YuJanggi.Server.V2.View;

    /// <summary>한 대국의 엔진과 시간 루프를 소유하며 모든 엔진 변경을 직렬화합니다.</summary>
    internal sealed class GameRoom
    {
        private static readonly TimeSpan TickInterval = TimeSpan.FromMilliseconds(50);
        private readonly Lock _engineSync = new();
        private readonly CancellationTokenSource _lifetimeCts = new();
        // 현재 배포된 Core의 장기 엔진 타입은 MatchModel입니다.
        private MatchModel? _engine;
        private Task? _runTask;
        private long _lastTick;
        private bool _choReady;
        private bool _hanReady;
        private bool _started;
        private bool _ended;
        private bool _closed;

        public string MatchId { get; private set; } = string.Empty;
        public IClientSession? ChoPlayer { get; private set; }
        public IClientSession? HanPlayer { get; private set; }
        public Formation ChoFormation { get; private set; }
        public Formation HanFormation { get; private set; }

        /// <summary>참가자를 등록합니다. 대국 시작은 양쪽 준비 완료 이후 별도로 요청합니다.</summary>
        public void Initialize(string matchId, IClientSession choPlayer, Formation choFormation,
            IClientSession hanPlayer, Formation hanFormation)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(matchId);
            ArgumentNullException.ThrowIfNull(choPlayer);
            ArgumentNullException.ThrowIfNull(hanPlayer);
            if (!Enum.IsDefined(choFormation) || !Enum.IsDefined(hanFormation))
                throw new ArgumentException("잘못된 포진입니다.");
            lock (_engineSync)
            {
                if (_ended || _closed || MatchId.Length != 0)
                    throw new InvalidOperationException("초기화할 수 없는 장기 룸입니다.");
                if (choPlayer.ClientId == hanPlayer.ClientId)
                    throw new ArgumentException("서로 다른 두 세션이 필요합니다.");

                MatchId = matchId;
                ChoPlayer = choPlayer;
                HanPlayer = hanPlayer;
                ChoFormation = choFormation;
                HanFormation = hanFormation;
            }
        }

        /// <summary>룸당 하나의 시간 루프를 시작합니다. 반복 호출은 같은 작업을 반환합니다.</summary>
        public Task RunAsync(CancellationToken cancellationToken = default)
        {
            lock (_engineSync)
            {
                if (_runTask is not null)
                    return _runTask;
                if (_ended || _closed || MatchId.Length == 0)
                    throw new InvalidOperationException("시간 루프를 시작할 수 없는 룸입니다.");

                return _runTask = RunLoopAsync(cancellationToken);
            }
        }

        private async Task RunLoopAsync(CancellationToken cancellationToken)
        {
            using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(
                cancellationToken, _lifetimeCts.Token);
            using var timer = new PeriodicTimer(TickInterval);
            try
            {
                while (await timer.WaitForNextTickAsync(linkedCts.Token).ConfigureAwait(false))
                {
                    lock (_engineSync)
                    {
                        if (_ended || linkedCts.IsCancellationRequested)
                            break;
                        if (_started)
                            AdvanceEngineTime();
                    }
                }
            }
            catch (OperationCanceledException) when (linkedCts.IsCancellationRequested)
            {
                // 서버/룸 종료에 의한 정상 취소입니다.
            }
            finally
            {
                lock (_engineSync)
                    StopEngine();
            }
        }

        /// <summary>타이머 지연을 포함한 실제 경과 시간을 엔진에 전달합니다. 엔진 잠금 안에서만 호출합니다.</summary>
        private void AdvanceEngineTime()
        {
            long now = Stopwatch.GetTimestamp();
            float deltaTime = (float)Stopwatch.GetElapsedTime(_lastTick, now).TotalSeconds;
            _lastTick = now;
            _engine!.Tick(deltaTime);
        }

        /// <summary>참가자의 준비 상태만 기록하며 중복 준비 요청은 무시합니다.</summary>
        public void SetPlayerReady(IClientSession session)
        {
            ArgumentNullException.ThrowIfNull(session);
            lock (_engineSync)
            {
                if (_ended || _closed)
                    throw new InvalidOperationException("종료된 룸입니다.");
                if (ChoPlayer?.ClientId == session.ClientId)
                    _choReady = true;
                else if (HanPlayer?.ClientId == session.ClientId)
                    _hanReady = true;
                else
                    throw new InvalidOperationException("룸 참가자가 아닙니다.");
            }
        }

        /// <summary>양쪽 준비 완료 시 엔진을 초기화하고 대국 시간을 시작합니다. 0초는 Core의 무제한 설정입니다.</summary>
        public bool TryStartGame(float turnTime)
        {
            if (!float.IsFinite(turnTime) || turnTime < 0)
                throw new ArgumentOutOfRangeException(nameof(turnTime));
            lock (_engineSync)
            {
                if (_ended || _started || !_choReady || !_hanReady || _runTask is null)
                    return false;

                try
                {
                    _engine = new MatchModel(new Turn(turnTime), new Record(), new Score(),
                        new BoardModel(), new JanggiRule());
                    _engine.InitGame(ChoFormation, HanFormation);
                    _engine.BindEvents();
                    _engine.MatchEvent.OnGameEnded += HandleGameEnded;
                    _engine.StartGame();
                    _lastTick = Stopwatch.GetTimestamp();
                    _started = true;
                    return true;
                }
                catch
                {
                    StopEngine();
                    throw;
                }
            }
        }

        /// <summary>서버가 허용한 이동을 엔진에 전달합니다. 시간 갱신과 동시에 실행되지 않습니다.</summary>
        public bool TryMove(Pos from, Pos to)
        {
            lock (_engineSync)
            {
                if (!_started || _ended)
                    return false;
                // 이동 전에 이전 턴의 경과 시간을 반영하여 다음 턴에 시간이 넘어가지 않게 합니다.
                PlayerTeam requestedTurn = _engine!.PlayerTurn;
                AdvanceEngineTime();
                if (_ended || _engine.PlayerTurn != requestedTurn)
                    return false;
                return _engine.TryMove(from, to);
            }
        }

        /// <summary>현재 턴의 기권을 엔진에 전달합니다. 요청자/진영 검증은 호출하는 서버 핸들러가 담당합니다.</summary>
        public void GiveUp()
        {
            lock (_engineSync)
            {
                if (!_started || _ended)
                    return;
                PlayerTeam requestedTurn = _engine!.PlayerTurn;
                AdvanceEngineTime();
                if (!_ended && _engine.PlayerTurn == requestedTurn)
                    _engine.GiveUp();
            }
        }

        private void HandleGameEnded(GameResultInfo result)
        {
            // 엔진 이벤트도 Tick/명령의 동일 잠금 안에서 발생합니다.
            StopEngine();
        }

        /// <summary>결과를 새로 판정하지 않고 엔진과 시간 루프를 중단합니다.</summary>
        public void EndGame()
        {
            lock (_engineSync)
            {


                StopEngine();
            }
        }

        private void StopEngine()
        {
            if (_ended)
                return;
            _ended = true;
            _engine?.Turn.EndGame();
            _lifetimeCts.Cancel();
        }

        /// <summary>루프 종료를 기다린 뒤 이벤트와 참가자 참조를 정리합니다. 연결은 서버가 소유합니다.</summary>
        public async Task CloseAsync()
        {
            Task completion;
            lock (_engineSync)
            {
                StopEngine();
                completion = _runTask ?? Task.CompletedTask;
            }
            try
            {
                await completion.ConfigureAwait(false);
            }
            finally
            {
                lock (_engineSync)
                {
                    if (!_closed)
                    {
                        _closed = true;

                        if (_engine is not null)
                        {
                            _engine.MatchEvent.OnGameEnded -= HandleGameEnded;
                            _engine.UnBindEvents();
                            _engine = null;
                        }

                        ChoPlayer = null;
                        HanPlayer = null;
                        _lifetimeCts.Dispose();
                    }
                }
            }
        }
    }
}
