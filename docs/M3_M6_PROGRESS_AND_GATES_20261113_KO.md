# TJ.RemoteAccess M3–M7 개발 및 검증 상태 (2026-11-13)

대상 브랜치: feature/chatgpt-m2-engine-foundation

## 범위와 결론

현재 사용자가 실제로 원격 제어를 확인한 실행 환경은 Windows에 로그인한 사용자 세션의 Lab Server/Viewer이다. 후속 코드에는 다음 항목이 추가되었다. 자동 테스트 통과는 Windows Service 또는 보안 데스크톱 원격제어 완성을 의미하지 않는다.

| 항목 | 진행 | 미검증/차단 |
|---|---|---|
| M2 안정성 | 로그인 세션의 잠금·로그오프·원격 세션 해제 시 Server 중지 및 입력 해제; 제한된 감사 로그 | 잠금, 로그오프, 3명 동시 화면 공유 실기 |
| M3 IPC | Win32 Named Pipe의 원격 접속 차단, 정확한 로그온 SID DACL, 커널 PID/세션 확인, 랜덤 nonce와 32바이트 키의 HMAC 인증, 최대 1KiB status-only 명령 | 실제 별도 Windows Service와 사용자 SessionAgent 실행, Agent 키를 전달하는 비공개 핸들, 입력 Engine Gate 적용 |
| M4 | 로컬에 이미 신뢰 등록된 서버의 프로필 ID만 받는 외부 Viewer 실행, Setup JSON 프로필 목록 | PIE.FileTransfer Master에서 실행하는 실제 통합 코드 |
| M5 | 읽기 전용 CPU/메모리/프레임 측정 스크립트 | 270 PPM 검사 중 영향, 3 Viewer 장기 부하 |
| M6 | 대응 GPL 소스, 파일별 SHA-256 manifest를 포함한 오프라인 ZIP 생성 및 원복 설치 스크립트 | 실제 PowerShell 패키징·설치, 코드 서명, MSI, 관리자 권한 설치 |
| M7 | 원본 TightVNC는 변경/제거하지 않음 | 독립 native fork의 고객 운영 환경 검증 및 TightVNC 마이그레이션 |

비밀번호 접속 정책은 이번 작업에서 변경하지 않았다.

## M3 IPC 보안 보강

같은 Windows 계정을 사용하는 다른 로그인 세션에 파이프 접근권을 부여하지 않기 위해 기존 사용자 계정 SID 대신 정확한 로그온 SID (S-1-5-5-X-Y)를 허용한다. Win32 GetTokenInformation(TokenGroups) API에서 SE_GROUP_LOGON_ID 그룹을 읽는다. 이는 일부 환경에서 WindowsIdentity.Groups에 나타나지 않는 로그온 SID를 안전하게 얻기 위함이다.

로그온 SID가 유효하지 않으면 인증을 허용하지 않는다. 계정 SID, LocalSystem SID를 인터랙티브 Agent 권한으로 대신 쓰지 않는다. Named Pipe에서는 로그온 SID DACL 외에도 PIPE_REJECT_REMOTE_CLIENTS, OS가 보고한 접속 PID 및 Session ID, 매 세션 32바이트 비밀키의 challenge-response HMAC을 모두 검증한다. 키가 누락되거나 크기가 맞지 않으면 IPC 패킷을 보내기 전 거부한다.

중요: 별도 서비스가 Agent를 실제 실행하는 단계에서는 서비스가 대상 프로세스의 토큰으로부터 SID를 조회하고, 비밀키를 안전한 상속 핸들로 제공해야 한다. 그 작업은 아직 구현되지 않았다. 현재 M3 IPC는 화면과 키보드 입력 명령을 허용하지 않는 status-only 경계이다.

## M4 외부 Viewer 실행 인터페이스

Setup 실행 파일의 list-profiles-json 인자로 로컬 프로필 목록을 조회한다. 각 항목의 32자리 ProfileId를 Viewer의 --profile-id 인자로 전달하고, 즉시 연결하려면 --connect를 추가한다. 임의 호스트, 인증서 지문, PFX 비밀번호 및 서버 접속 비밀번호를 명령행으로 전달하지 않으며, 로컬에서 찾을 수 없는 프로필이면 연결을 거부한다.

이는 PIE.FileTransfer Master에 연동 가능한 인터페이스 기반이지만 Master 연동 구현·실기 확인과는 별개다.

## M6 오프라인 시험 패키지

소스 작업 트리를 커밋해 깨끗이 만든 뒤 다음 경로의 도구를 사용한다.

- tools/Build-TJRemoteAccess-Lab-OfflinePackage.ps1: self-contained win-x64 Lab Server/Viewer/Setup, LICENSE, source.zip, SHA-256 manifest가 포함된 ZIP과 SHA256 파일 작성
- tools/Install-TJRemoteAccess-Lab-Offline.ps1: 신뢰된 경로에서 전달받은 ZIP SHA-256을 인자로 받아 ZIP 경로/중복/심볼릭 링크/개별 파일 해시 검증 후 설치 및 실패 시 원복
- tools/Set-TJRemoteAccess-Lab-Startup.ps1: 명시적으로 현재 사용자 Interactive Logon 예약 실행 등록/해제. Windows 서비스, 비로그인 세션, UAC 제어와 무관
- tools/Measure-TJRemoteAccess-Lab.ps1: Windows 성능 측정, 시스템 설정 변경 없음

오프라인 패키지는 미서명 시험용이다. 스크립트의 정적 테스트는 통과했지만 실제 PowerShell 실행과 설치 수용은 아직 하지 않았다. ZIP은 출처 확인 없이 실험 외 PC나 고객사에 배포하지 않는다.

## 이번 코드 검증

- .NET 9 Lab Release 빌드: PASS, 오류/경고 0건
- Windows xUnit: 114/114 PASS (실제 Win32 named pipe, HMAC, PID/세션, fail-closed 포함)
- Python unittest: PASS
- Native M2 C++ Release x64 컴파일: PASS
- 기존 사용자의 Lab 2-PC 원격제어 동작 확인은 과거 빌드에 대한 확인으로 별도 기록

## 남은 제품화 게이트

1. LocalSystem ControlService와 실제 사용자 세션 Agent를 안전하게 실행하고 IPC 키를 별도의 비공개 상속 핸들로 전달
2. Agent 사용자 승인, 세션 재연결/로그오프/잠금 처리, 모든 화면 및 입력 sink에 EngineSessionGate 강제
3. 원본 TightVNC 2.8.90 fork의 독립 서비스/레지스트리/포트/IPC/Hook DLL과 raw RFB 차단을 두 프로그램 공존 VM에서 검증
4. Windows 10/11 로그인 화면·UAC의 독립 권한 및 감사 모델, 실제 데스크톱 전환 검증
5. PIE.FileTransfer Master 통합, 270 PPM 및 3 Viewer 장기 성능 시험, 실제 설치/업데이트/롤백과 코드 서명
6. 고객 승인 및 GPL 파생 소스 의무 검토 후 단계별 마이그레이션

위 게이트를 해결하기 전에는 일반 사용자 Lab 원격제어와 Windows 서비스 기반 무인 원격제어를 같은 완성도로 취급하지 않는다.
