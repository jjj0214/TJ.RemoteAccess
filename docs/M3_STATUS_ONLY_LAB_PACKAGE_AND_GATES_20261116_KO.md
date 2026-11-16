# TJ.RemoteAccess 독립형 개발 — M3 상태 전용 서비스와 패키징 (2026-11-16)

## 목표와 현재 경계

최종 목표는 TightVNC 없이 TJ.RemoteAccess만으로 Windows PC에 설치하고, 원격 화면·키보드·마우스 제어, 부팅 후 자동 운영, 다중 Viewer, 인증/승인, 설치·업그레이드·복구가 가능한 독립 제품이다.

**현재 사용자에게 검증된 원격제어 엔진은 대화형 M2 .NET Lab Server/Viewer다.** M3 신규 서비스는 현재 **status-only**이다. Windows Service로 설치되어도 원격 화면 캡처, 입력 실행, TLS/RFB 리스너, Winlogon/UAC 제어 기능을 제공하지 않는다. 기존 Lab 설치나 TightVNC를 교체해서는 안 된다.

## 이번 개발 범위

0. `TJ.RemoteAccess.Lab.sln`에 M3 두 프로젝트를 추가하고, 같은 소스 디렉터리의 다중 SDK 프로젝트가 공유 `obj`로 서로 충돌하지 않도록 `src/TJRemoteAccess.Core/Directory.Build.props`에서 중간 빌드 경로를 분리.
1. M3 서비스의 단일 Agent 세션 수명주기: 잠금/로그오프/세션 연결 해제 시 기존 Agent 취소, 실제 활성 콘솔 SessionId와 이벤트 SessionId가 일치할 때만 새로운 로그온/잠금 해제로 재시작. 직전 Agent 완전 종료 후 새 키·파이프·프로세스로 재인증, 겹치는 Agent 금지. RemoteConnect 및 잘못된/불명확한 Windows 세션 이벤트는 차단.
2. 한정된 로컬 IPC: exact logon SID DACL, Win32 PIPE_REJECT_REMOTE_CLIENTS, 커널 PID/세션 확인, 32바이트 상속 익명 파이프 키, HMAC challenge-response, heartbeat. 원격 화면/입력 메시지는 금지.
3. status-only 서비스 진단 파일: `<ServiceExeFolder>\service-status.json`에 상태, 인증된 Agent PID/SessionId, UTC 시간과 오류 형식만 기록. 원격 비밀번호/키/PFX/화면/사용자명은 기록하지 않음. 진단 파일은 권한 판단에 절대 사용하지 않음.
4. `tools/Build-TJRemoteAccess-M3-LabPackage.ps1`: clean Git HEAD 기준 Release self-contained win-x64 Server-Service 및 Agent, 대응 GPL source.zip, LICENSE, 파일별 SHA-256 manifest를 포함한 미서명 ZIP 생성. Windows PowerShell Parser 문법 검사 포함.
5. `tools/Install-TJRemoteAccess-M3-Lab.ps1`: **별도 격리 VM 전용**. 신뢰된 ZIP SHA-256 인자와 파일별 manifest 검증, 관리자 권한 및 명시적 `-ApproveStatusOnlyLab`, 중복/기존 설치 소유권 확인, 별도 `TJRA.ControlService` 수동 서비스 생성, 교체 실패 시 롤백.
6. `tools/Uninstall-TJRemoteAccess-M3-Lab.ps1`: M3 Lab 매니페스트/서비스 경로 사전 확인, 별도 동의 후 해당 서비스와 디렉터리만 제거.
7. `tools/Test-TJRemoteAccess-M3-Lab.ps1`: 읽기 전용 설치, LocalSystem 계정, Agent PID/Windows SessionId, 진단 상태와 TightVNC 공존 상태 표시.

## 자동 테스트 검증 범위

- Windows .NET 9 Lab, M3 ControlService, M3 Agent Release 컴파일.
- Windows xUnit: 기존 TLS/Viewer/IPC 검증 + 서비스 세션 revoke/rebind/동시 실행 방지 + 세션 이벤트/콘솔 ID 매칭 부정 테스트 + **실제 익명 파이프/Named Pipe의 동일 프로세스 및 독립 사용자 프로세스 Agent HMAC 부트스트랩, PID/SessionId 검증과 Heartbeat** + 진단 상태 파일 테스트. 독립 프로세스 검증은 LocalSystem/SCM이 아니다.
- Python unittest: 기존 GPL/Lab/보안 테스트 + M3 설치 계약/부정적 검증 정적 검사.
- Native C++ EngineGate MSBuild Release x64 컴파일 및 git diff --check.
- 2026-11-16 현재 .NET 9 Lab/M3 Service/M3 Agent 통합 Release 빌드 오류·경고 0건, xUnit **138/138 PASS**, Python unittest PASS, Native M2 C++ Release 빌드 PASS. Windows Service 런타임/PowerShell 설치는 미실행.
- IMPORTANT: 실제 서비스 설치, `CreateProcessAsUser`, LocalSystem → 로그인 사용자 토큰, SCM 재부팅, installer PowerShell 런타임 및 롤백은 JJJ Dev 도구의 빌드/자동 테스트 범위 밖에 있으며 **실행 검증 전**이다. Python 정적 검사는 PowerShell의 실행/설치 성공을 보장하지 않는다.

## 격리 Windows VM 수용 테스트 순서 — 제품 출시 전 게이트

이 절차는 **프로그램 개발자가 원격 시스템을 오염시키지 않는 실험용 VM에만** 수행한다. 실제 PC/설비/운영 환경에서 실행하지 않는다.

1. 소스를 커밋해 clean worktree 확보하고 빌드: `powershell -NoProfile -File .\tools\Build-TJRemoteAccess-M3-LabPackage.ps1`
2. ZIP과 **별도 신뢰된 수단으로 검증한** ZIP SHA-256을 격리 VM으로 전달. 미서명 파일을 검증 없이 허용하지 않는다.
3. 관리자 PowerShell에서 `Install-TJRemoteAccess-M3-Lab.ps1 -PackagePath "<ZIP>" -ExpectedPackageSha256 "<SHA256>" -ApproveStatusOnlyLab -Start`로 명시적 시험 설치. 시작 유형은 **Manual**.
4. `Test-TJRemoteAccess-M3-Lab.ps1` 출력에서 ServiceState=Running, LocalSystemIdentity=true, LastServiceEvent.State=AgentAuthenticated, AgentProcesses의 PID 및 세션 일치 확인. **이미 로그인된 대화형 세션이 없으면 AgentAuthenticated가 아닌 것이 정상**.
5. 잠금·잠금 해제, 로그오프·재로그인, 사용자 세션 전환, 서비스 중단/재시작을 반복해 이전 Agent 제거/새 세션 PID 변경, 키 재사용 금지, Input/TLS 리스너 없음 확인.
6. 새 패키지 `-Upgrade`(필요시 `-Start`) 및 잘못된 해시/손상 ZIP/중복 서비스/실행 중 파일 변경을 거절하는지 확인. 이전 M3 서비스가 운영 중이면 업데이트가 이를 중단할 수 있으므로 VM에서만 진행.
7. `Uninstall-TJRemoteAccess-M3-Lab.ps1 -ApproveStatusOnlyLabRemoval` 후 M3 서비스만 삭제되고 기존 TightVNC, Lab 설정, Firewall 규칙은 유지되는지 확인.

설치 성공이나 AgentAuthenticated 기록은 **원격제어 가능함을 뜻하지 않는다**. 아직 status-only 구성이므로 Viewer를 이 서비스에 접속시키면 안 된다.

## 추가 개발 필수 게이트

| 단계 | 필요 작업 | 통과 기준 |
|---|---|---|
| M3 Engine | 기존 Lab TLS 접속과 독립 native EngineSessionGate를 통합하고 사용자 명시적 화면 공유/입력 동의, revoke·키 해제 후 처리 | 실제 서비스↔Agent↔Viewer 영상/입력 + 강제 차단 보안 시험 |
| Windows 보안 데스크톱 | 로그인/잠금/UAC 지원 여부별 위협 모델, 권한 및 정책 합의, Windows 10/11 VM 실제 검증 | 권한 상승 우회 없이 명확한 허용/차단 동작 |
| M4 | Master의 `list-profiles-json` 및 `--profile-id --connect` 연동. 독립 프로그램/프로세스 계약 유지 | 실제 PIE.FileTransfer Master GUI 실행·재연결 |
| M5 | 3 Viewer 장기 운영, 270 PPM Tact/CPU/GPU/SSD 영향 측정, 24시간 이상 복구 | 허용 오차/누락/장애 재연결 게이트 통과 |
| M6 | 제품 MSI/코드 서명, 인증서 갱신·폐기, 서비스 권한/ACL/rollback, 소스 대응 GPL 검증 | 깨끗한 Win10/Win11 VM 설치·업데이트·제거 |
| M7 | TightVNC와 서비스·레지스트리·포트·Hook 완전 독립성, 이전/복구 | 고객 승인 하에 점진 전환·복구 실기 |

**종합:** 이번 변경은 M3 status-only 수명주기/배포 준비를 개선한 개발 체크포인트이지, 정식 TightVNC 대체 버전이나 최종 사용자 수용 테스트 완료가 아니다.
