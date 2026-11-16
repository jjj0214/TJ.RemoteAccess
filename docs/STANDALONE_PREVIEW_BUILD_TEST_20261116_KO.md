# TJ.RemoteAccess 독립형 PREVIEW 검증 가이드 (2026-11-16)

## 1. 제품 목표와 완료 조건

최종 목표: TightVNC의 실행 파일과 서비스가 없어도 독립 TJ.RemoteAccess Server, Viewer, Setup, Windows ControlService 및 SessionAgent만으로 Windows PC의 원격 화면과 입력, 인증, 다중 접속, 시작/중단, 설치/업데이트/원복을 제공하는 것.

**현재는 0.5.0-preview 개발 빌드로, 완성된 TightVNC 대체 제품이 아니다.** 아래 실험 외 현장/고객사에서는 적용하지 않는다. 제품에 GPL 대응 소스/라이선스를 포함하지만 코드 서명 및 정식 MSI는 남았다.

이 Preview의 기능 경계:

| 항목 | 현재 기능 | 검증 상태 |
|---|---|---|
| 독립 Server, Viewer, Setup | .NET 9 앱호스트, TightVNC API/설치 의존 없음 | 0.5.0-preview Release 컴파일/자동 테스트, 실제 2 PC Preview 수용 미실행 |
| 사용자 세션 화면/입력 | 인증된 TLS Server, Viewer 최대 3 / Controller 1 | 기존 M2 Lab 사용자 수용을 근거로 기능 유지; 별도 Preview 새 인증서 2 PC 재검증 필요 |
| 인증 및 설정 | 프로필+pin, 개인 Viewer 인증서, 선택적 TLS password | 보안/프로토콜 테스트; 기존 Lab과 다른 저장소, 포트 사용 |
| 서비스 기반 감독 | LocalSystem ControlService ↔ 사용자 Agent 보안 IPC, 사용자 소유의 visible Server | 코드 + xUnit/독립 일반 사용자 프로세스 IPC 테스트; 실제 Service→Agent→GUI 실기 미검증 |
| 서비스 재시작 | Auto 서비스, 로그인·잠금 해제 시 새 Agent, 잠금·로그오프 시 종료 | 이벤트 정책 테스트, Windows 실제 SCM/재부팅 미검증 |
| Winlogon/잠금/UAC 화면 원격제어 | **미지원**; 인증된 사용자 `Default` 데스크톱만 허용 | 보안 데스크톱 거부 정책만 테스트 |
| TLS 관리, 패키징 | 5개 EXE 포함 서비스 Preview ZIP, 별도 사용자 Preview ZIP, GPL source.zip, SHA-256 | PowerShell Parser/정적 검사, 실제 Package Build+Install+Rollback 미검증 |
| Native GPL TightVNC fork EngineHost 및 M4 FileTransfer | 독립 C++ gate 기반은 컴파일; 완전 통합/외부 Master 연동 없음 | 미완료 |
| 공장 생산 영향 | CPU/GPU/장시간/270 PPM/3 Viewer 시험 없음 | 미완료 |

## 2. 완전히 분리된 프로그램 구성

- `TJ.RemoteAccess.sln`: Product Preview Server, Viewer, Setup, M3 ControlService/Agent, 기존 Lab 회귀 xUnit 및 **독립 Preview xUnit** 통합 빌드
- `TJ.RemoteAccess.Lab.sln`: 기존 Lab 그대로 유지, 별도 config 및 실행 파일
- `TJ.RemoteAccess.Server.exe`: **가시적인 사용자 세션**에서 TLS 서버, 화면 공유/키보드/마우스. 기본 port 15544, password TLS 선택 시 +1(15545)
- `TJ.RemoteAccess.Viewer.exe`: 여러 Server/Viewer 인증서 프로필, 3명 세션 구분
- `TJ.RemoteAccess.Setup.exe`: 현재 Windows 사용자 인증서 및 등록/인증 관리, service-host 동의
- `TJ.RemoteAccess.M3.ControlService.exe`: LocalSystem 서비스 `TJRA.ControlService`. 기본 status-only. Windows 보안 데스크톱에서 입력/화면 직접 접근하지 않음
- `TJ.RemoteAccess.M3.Agent.exe`: 서비스가 현재 콘솔 사용자의 토큰으로 만든 보안 파이프 인증 SessionAgent. 관리자 정책과 사용자 등록이 둘 다 있을 때만 가시적 Server를 Job 내에서 실행 및 감독

**저장·포트 분리**:
- Product Preview 인증서/서버/프로필: `%LOCALAPPDATA%\TJ.RemoteAccess` 및 `Cert:\CurrentUser\My`.
- 기존 Lab 설정: `%LOCALAPPDATA%\TJ.RemoteAccess.Lab` (유지).
- Product Preview TLS: TCP 15544(비밀번호 옵션 15545); Lab: TCP 15444(15445); TightVNC 5900은 변경하지 않음.
- 서비스 설치: `%ProgramFiles%\TJ.RemoteAccess\ServicePreview`; 운영자 정책도 같은 ACL 보호 폴더. 프로필/비밀번호/관리자 정책은 CLI 인자나 임의 네트워크로 전달하지 않음.

## 3. A단계: 사용자 기반 Preview만으로 자체 원격제어 시험

**최초에는 격리된 테스트 PC 두 대를 이용하고 Windows Service는 설치하지 않는다.**

Windows .NET 9 SDK가 있는 개발 PC에서 깨끗한 커밋 소스 준비:

```powershell
dotnet build .\TJ.RemoteAccess.sln -c Release
powershell -NoProfile -ExecutionPolicy Bypass -File .\tools\Install-TJRemoteAccess-Preview.ps1 -Mode Both
```

또는 `tools/Build-TJRemoteAccess-Preview-OfflinePackage.ps1` (커밋된 clean Git worktree 필요)으로 self-contained ZIP을 만들어 별도 SHA-256 확인 후 원격 PC에서 `Install-TJRemoteAccess-Preview-Offline.ps1 -PackagePath "<ZIP>" -ExpectedPackageSha256 "<SHA256>" -Mode Server|Viewer`로 설치한다.

Server PC **최초 1회**(기존 `%LOCALAPPDATA%\TJ.RemoteAccess\server.json`이 없는 경우만):

```powershell
& "$env:LOCALAPPDATA\Programs\TJ.RemoteAccess\Setup\TJ.RemoteAccess.Setup.exe" init
```

- PFX와 별도로 출력되는 PFX 암호는 안전하게 관리하고, 인증된 수단으로 Viewer PC에 전달한 뒤 임시 PFX 삭제.
- Server `%LOCALAPPDATA%\TJ.RemoteAccess\server.json`의 `ListenAddress`를 테스트 LAN의 IP로 변경. 같은 PC만 테스트한다면 127.0.0.1 유지.
- 기본 인증서 포트 15544, 별도 사용자 암호 사용 정책은 기존 설계를 변경하지 않았다.
- Server(사용자 로그인 완료 후):

```powershell
& "$env:LOCALAPPDATA\Programs\TJ.RemoteAccess\Server\TJ.RemoteAccess.Server.exe" --allow-lan
```

  Server GUI에서 Start. Viewer PC는 다음과 같이 실행:

```powershell
& "$env:LOCALAPPDATA\Programs\TJ.RemoteAccess\Setup\TJ.RemoteAccess.Setup.exe" import-client "C:\Temp\TJRA-Pairing\client-transfer.pfx" "시험 서버" "192.168.0.100"
& "$env:LOCALAPPDATA\Programs\TJ.RemoteAccess\Viewer\TJ.RemoteAccess.Viewer.exe"
```

Viewer에서 등록 프로필 선택 후 Connect. 운영 PC의 방화벽을 설치기에서 자동 개방하지 않으므로 격리 테스트 네트워크에서 Viewer IP만 수동 허용한다. **기존 Lab PFX를 독립 Preview로 복사하지 말고 새 프로필을 사용**.

사용자 로그온 후 자동 시작만 원하면 Server 설치 및 최초 init 완료 후:

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File .\tools\Set-TJRemoteAccess-Preview-Startup.ps1 -Action Enable -AllowLan
```

이 예약 작업은 로그온할 때만 실행된다. 잠금 화면/로그온 이전의 무인 제어를 제공하지 않는다.

## 4. B단계: 격리 Windows VM에서 서비스 기반 **감독** 검증

**현재 설치/롤백 스크립트는 PowerShell 구문 파서 및 Python 계약만 검증했다. 실제 실행 검증 전이므로 스냅샷 복원 가능한 폐기형 Win10/11 x64 VM 전용이다.**

1. 개발 VM에서 소스 커밋 후 clean HEAD 확인, `tools/Build-TJRemoteAccess-ServicePreviewPackage.ps1`을 PowerShell에서 실행. 생성 `out\packages\TJ.RemoteAccess.ServicePreview-0.5.0-preview-win-x64.zip`과 out-of-band SHA-256을 별도로 검증.
2. **관리자 권한으로** 격리 VM에 ZIP 전달 및 아래 설치:

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File .\Install-TJRemoteAccess-ServicePreview.ps1 -PackagePath "<ZIP>" -ExpectedPackageSha256 "<SHA256>" -ApproveServicePreview -Start
```

이 단계에서는 서비스는 등록·시작되더라도 **desktop-host-policy.json이 없어서 상태 전용 Agent만 시작**한다. Probe:

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File .\Test-TJRemoteAccess-ServicePreview.ps1
```

3. 두 가지 **독립적** 승인 작업을 거쳐야 현재 로그인 사용자의 Server 시작을 요청한다.
   - 관리자: 서비스를 중단하고 설치한 패키지를 `-Upgrade -EnableSupervisedDesktop -Start`로 명시적 교체하면 서비스 설치 폴더에 운영자 정책이 생성된다. 기존 설치 폴더/서비스 소유권이 일치해야만 교체됨.
   - 해당 Windows 사용자: 관리자 PowerShell이 아닌 정상 로그인 세션에서 제품 Setup을 `service-host enable --confirm-local-owner`로 실행. 먼저 사용자가 `Setup.exe init`으로 새 TLS Server 인증서를 만든 뒤 실제 IP 설정을 해야 함.
   - 승인 순서는 어느 쪽이 먼저여도 되지만 **둘 다 설정한 후 서비스를 재시작하거나 사용자 세션을 재연결**해야 현재 Agent의 서버 실행 여부를 새로 평가한다.
   - 철회: 정상 로그인 사용자에서 `TJ.RemoteAccess.Setup.exe service-host disable` 실행. Agent가 다음 상태 검사 시 자식 Server 중지.

4. 정상 연결된 뒤 보안 테스트: PC 잠금·해제, Ctrl+Alt+Del, UAC, 로그오프·재로그인, 다른 사용자 세션 전환, 서비스 중단/재시작, Viewer-2/3 동시 접속(4번째 차단), 잘못된 client pin/비밀번호, password policy 비활성화, 리스너/인증서 변경. 사용자 로그인/기본 데스크톱 밖에서는 화면/입력 차단이 정상.
5. 잘못된 ZIP SHA, ZIP 교체, 조작 매니페스트, 서비스 소유권 불일치, 중복 설치, `-Upgrade` 실패 후 이전 버전 복원 확인.
6. `Uninstall-TJRemoteAccess-ServicePreview.ps1 -ApproveServicePreviewRemoval` 후 SCM/디렉터리만 제거되고 원본 TightVNC 및 사용자 인증서·프로필은 그대로인지 확인.

**주의:** `DesktopHostStarted` 상태는 프로세스 생성을 뜻할 뿐 TLS 연결, 해상도, 키보드/마우스 제어가 검증됐다는 뜻은 아니다. 누락된 Winlogon/UAC 지원을 자동 권한 우회로 대체하지 않는다.

## 5. 정식 TightVNC 대체 완료에 남은 게이트

- Windows 10/11 LocalSystem 서비스의 실제 `CreateProcessAsUser` 및 사용자 profile/certificate/elevation/Job/SCM 재연결 검증
- 잠금·로그온·UAC/보안 데스크톱을 정식 지원할지 별도 정책·보안 설계 및 Win10/11 실제 VM 확인 (현 구조는 **의도적으로 차단**)
- 서비스와 네이티브 `EngineSessionGate`의 실제 입력 sink/재생 공격 억제 및 권한 철회 통합, 사용자 동의 UI, 인증서 갱신/폐기/감사 정책
- UI 설치관리자, Windows MSI, 서비스별 설치 복구, 서명 및 신뢰 유통, 관리자 정책 파일 ACL 심층 검증
- 3 Viewer 장시간과 270 PPM 검사 Tact/CPU/GPU/네트워크·화면 지연 검증
- GPL 파생 저작권 및 소스 대응, 고객 운영환경 승인과 TightVNC 제거 전 rollback
- PIE.FileTransfer Master 통합은 **별도 프로젝트**이며 TJ.RemoteAccess 자체 독립 사용의 선행 필수 요건이 아님

정식 출시는 이 항목까지 통과한 이후에만 승인한다.
