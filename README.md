# YuJanggi.Server.V2

유장기 클라이언트의 TCP 연결을 수락하고 YuJanggi.Protocol.V2 형식의 메시지를 읽는
.NET 10 콘솔 서버입니다. 현재는 네트워크 전송 계층을 구축하는 단계입니다.

## 구현 상태

- TCP 연결 비동기 수락과 클라이언트별 수신 루프
- 4바이트 길이 헤더 및 JSON 본문의 정확한 길이 읽기
- 프로토콜 라이브러리를 이용한 메시지 복원과 본문 크기 검증
- 전송 계층의 서버 메시지 전송 메서드와 동시 전송 잠금

현재 수신 루프는 메시지를 읽은 뒤 다음 메시지를 기다립니다. 핸드셰이크 판정·응답,
요청 라우팅, 게임 로직은 아직 구현되지 않았으며 실행 중 서버가 자동 응답을 보내지 않습니다.

## 요구 환경

| 항목 | 요구 사항 |
| --- | --- |
| 개발 및 실행 | .NET SDK 10 |
| 프로젝트 | `YuJanggi.Server.V2.csproj`, `net10.0` |
| 프로토콜 의존성 | NuGet `YuJanggi.Protocol.V2` 0.1.0 |
| 수신 포트 | TCP 7777 사용 가능 |

현재 `NuGet.Config`에는 작성자 환경의 절대 경로가 들어 있으므로 다른 환경에서는
`local-protocol` 경로를 먼저 변경해야 합니다.

## 빠른 시작

### 1. 프로토콜 패키지 준비

[프로토콜 저장소](https://github.com/SeokJinYoo98/YuJanggi.Protocol.V2)를 준비한 뒤
해당 저장소 루트에서 실행합니다.

```powershell
dotnet pack YuJanggi.Protocol.V2/YuJanggi.Protocol.V2.csproj -c Release -o artifacts/nuget
```

`artifacts/nuget/YuJanggi.Protocol.V2.0.1.0.nupkg`가 생성되었는지 확인합니다.

### 2. 서버의 로컬 NuGet 경로 설정

서버 저장소 루트에서 실행합니다. 두 저장소가 같은 부모 디렉터리에 있다고 가정합니다.
배치가 다르면 `Resolve-Path`의 입력을 실제 패키지 폴더로 변경합니다.

```powershell
$protocolFeed = (Resolve-Path ../YuJanggi.Protocol.V2/artifacts/nuget).Path
dotnet nuget update source local-protocol --source "$protocolFeed" --configfile NuGet.Config
dotnet restore YuJanggi.Server.V2.csproj --configfile NuGet.Config
```

`NuGet.Config`는 `YuJanggi.Protocol.V2`를 `local-protocol`에 매핑합니다.
패키지 생성 폴더와 소스 경로가 일치해야 합니다. 공개 NuGet 배포를 전제로 하지 않습니다.

### 3. 빌드 및 실행

서버 저장소 루트에서 실행합니다.

```powershell
dotnet build YuJanggi.Server.V2.csproj -c Release --no-restore
dotnet run --project YuJanggi.Server.V2.csproj -c Release --no-build
```

정상 기동 시 다음 문구가 출력됩니다.

```text
YuJanggi Server started.
```

서버는 `0.0.0.0:7777`에서 수신합니다. 같은 컴퓨터의 클라이언트는 `127.0.0.1:7777`로
접속합니다. 터미널에서 `Ctrl+C`로 프로세스를 종료할 수 있습니다. 현재 진입점은
종료 신호를 CancellationToken으로 전달하는 정상 종료 절차를 구성하지 않습니다.

### 4. TCP 접속 확인

서버를 실행한 상태에서 별도 PowerShell 창에 입력합니다.

```powershell
$client = [System.Net.Sockets.TcpClient]::new()
try {
    $client.Connect('127.0.0.1', 7777)
    $client.Connected
} finally {
    $client.Dispose()
}
```

`True`가 출력되고 서버 창에 `Client connected` 및 `Client disconnected` 로그가 나타납니다.
이 절차는 TCP 접속만 확인하며 핸드셰이크나 게임 동작 성공을 의미하지 않습니다.

## 설정

| 설정 | 현재 값 | 변경 위치 및 조건 |
| --- | --- | --- |
| 바인딩 주소 | `IPAddress.Any` | `Server/YuJanggiServer.cs`의 `Address`, 변경 후 빌드 |
| 포트 | `7777` | 같은 파일의 `Port`, 변경 후 빌드 |
| 프로토콜 패키지 버전 | `0.1.0` | `YuJanggi.Server.V2.csproj`의 PackageReference |
| 로컬 패키지 소스 | 환경별 경로 | `NuGet.Config`의 `local-protocol`, 복원 전 설정 필요 |

현재 주소·포트를 바꾸는 환경변수, 명령줄 옵션, appsettings 설정은 없습니다.
`IPAddress.Any`는 모든 IPv4 인터페이스에 바인딩하므로 로컬 전용으로 실행하려면
소스에서 바인딩 주소를 변경해야 합니다.

## 메시지 처리 흐름

```text
Program.Main
  → YuJanggiServer.RunAsync
  → TcpConnectionListener.AcceptAsync
  → 클라이언트별 HandleClientAsync
  → TcpClientConnection.ReceiveAsync
  → 헤더 읽기 → 본문 길이 검증 → 본문 읽기 → ClientMessage 복원
```

프레임은 big-endian Int32 길이 헤더와 UTF-8 JSON 본문으로 구성됩니다.
본문은 1~4096바이트이며 길이에 헤더는 포함하지 않습니다. `ReadExactlyAsync`로
TCP 데이터가 여러 번에 나뉘어 도착하는 경우를 처리합니다.

`TcpClientConnection.SendAsync`는 `ServerMessage`를 직렬화·프레이밍한 뒤 전송하며
`SemaphoreSlim`으로 연결별 쓰기를 직렬화합니다. 현재 수신 처리에서는 이 메서드를 호출하지 않습니다.

프로토콜 0.1.0의 핸드셰이크 DTO는 사용 가능하지만 서버가 버전 비교를 수행하지 않습니다.
버전 불일치 응답, 일반 오류 응답, 알 수 없는 요청의 처리 정책은 아직 구현되지 않았습니다.
메시지 생성 예제와 DTO 계약은 [프로토콜 README](https://github.com/SeokJinYoo98/YuJanggi.Protocol.V2#readme)를 참고하세요.

## 프로젝트 구조

```text
Program.cs                         # 서버 진입점
Server/YuJanggiServer.cs            # 연결 수락과 클라이언트 수신 루프
Transport/TcpConnectionListener.cs  # TCP 리스너
Transport/TcpClientConnection.cs    # 프레임 송수신과 연결 해제
View/NetworkView.cs                # 현재 비어 있는 클래스
NuGet.Config                       # 로컬 패키지 소스 및 매핑
YuJanggi.Server.V2.csproj           # .NET 실행 프로젝트
```

## 개발 및 제한

현재 서버 저장소에는 테스트 프로젝트가 없습니다. `dotnet build`와 TCP 접속 확인을
기본 점검으로 사용하며 프로토콜 저장소의 테스트는 메시지 계약 검증을 위한 별도 테스트입니다.

- 연결 종료 후 `_connections`와 `_clientTasks`의 항목을 제거하는 처리가 아직 없습니다.
- 클라이언트 작업 전체를 기다리며 종료하는 절차와 작업 실패 관찰이 아직 없습니다.
- 수신 루프에서 취소와 IOException은 처리하지만 잘못된 JSON 등의 모든 예외를 처리하지는 않습니다.
- 인증, TLS, DB, 게임 세션 및 운영 배포 구성은 아직 제공하지 않습니다.

## 문제 해결

| 증상 | 확인할 내용 |
| --- | --- |
| 프로토콜 패키지 복원 실패 | 0.1.0 nupkg 존재 여부, `local-protocol` 경로와 매핑 |
| 기동 시 포트 사용 오류 | TCP 7777을 다른 프로세스가 사용 중인지 확인 |
| 연결되지만 응답이 없음 | 현재 요청 처리와 응답이 미구현인 상태에서는 예상된 동작 |
| 수신이 계속 대기함 | 선언한 길이만큼 본문을 보냈는지 확인; 줄바꿈은 메시지 구분자가 아님 |
| 헤더·본문 처리 실패 | big-endian 길이 헤더 및 1~4096바이트 본문 제한 확인 |
