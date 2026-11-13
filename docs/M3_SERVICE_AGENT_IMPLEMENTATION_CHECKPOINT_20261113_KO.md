# TJ.RemoteAccess M3 서비스/Agent 구현 체크포인트 (2026-11-13)

## 명확한 적용 범위

대상: `feature/chatgpt-m2-engine-foundation` / JJJ Dev Windows x64.
이 체크포인트의 코드는 **status-only 서비스/Agent 개발기**다. 정식 무인 원격접속, 화면/입력 연결, 잠금/로그온/UAC, 고객사 및 설비 사용은 아직 지원하지 않는다.

### 신규 구성
- `src/TJRemoteAccess.Core/TJ.RemoteAccess.M3.ControlService.csproj`: LocalSystem Session 0 전용 Windows Service. 화면 캡처, 원격 포트, 입력 API 없음.
- `src/TJRemoteAccess.Core/TJ.RemoteAccess.M3.Agent.csproj`: 사용자 세션 status-only Agent. 승인된 서비스 파이프 PID를 Win32 커널 조회로 확인한 후 기존 M3 HMAC challenge-response 사용.
- `M3WindowsSessionLauncher.cs`: WTS active-console 사용자 토큰, 해당 로그온 SID DACL, 한정된 익명 파이프 읽기 핸들 상속 (`STARTUPINFOEX`/handle list), 32-byte 일회용 HMAC 키, 프로세스 PID/세션 검증 및 중단 시 자식 종료.
- `tests/m3_agent_bootstrap_tests.cs`: 부정확한 실행 인자 및 핸들 문자열을 거부하는 테스트.

### 실제 자동 검증 범위
- 각 신규 프로젝트 Release x64 컴파일
- 기존 xUnit (추가 부트스트랩 인자 테스트 포함)
- Python unittest / git diff --check
- **서비스 설치, StartService, WTS 사용자 토큰, STARTUPINFOEX 상속, 서비스→Agent 상호 인증의 실제 프로세스 통합 테스트는 수행하지 못했다.** 따라서 아직 M3.2 통과로 간주하지 않는다.

### 남은 필수 개발/검증
1. **M3.2 실기**: 별도 격리 Windows VM에서 LocalSystem 서비스 수동 실행, 정확한 Agent PID/SessionId, inherited-only key, 잘못된 키/토큰/세션 부정 테스트. 서비스/Agent 생명주기 및 이벤트 기록 검증.
2. **M3.3 설치**: 서명/무결성 확인, 서비스 설치·중복·실패 롤백·재부팅·계정 ACL, 원본 TightVNC 독립성 검증. 설치 스크립트는 아직 없음.
3. **M3.4 보안 입력**: 별도 Agent 동의 UI, 독립 EngineSessionGate, 각 화면/입력 sink 적용, Viewer 승인/철회, 종료 시 입력 해제. 현재 M3 IPC는 의도적으로 상태 메시지 이외 금지.
4. **보안 데스크톱**: Windows 10/11 Winlogon/UAC는 별도 권한/감사 설계 및 VM 실기 후 구현. 보안 우회 금지.
5. **M4**: 기존 `--profile-id --connect` 및 `list-profiles-json`을 PIE.FileTransfer Master에 실제 연동. TJRA와 PIE.FileTransfer 저장소 분리 유지.
6. **M5**: 3 Viewer 장기 테스트, 270 PPM 검사, RTX3080/4080, CPU/GPU/메모리/Tact, 잠금·로그오프·재부팅.
7. **M6**: 실제 오프라인 ZIP 패키징/설치·복원 테스트, 제품화 서명/설치관리자, GPL 대응 소스.
8. **M7**: 고객 승인 후 TightVNC와 동시 운영→선별 전환→검증 후 제거. 롤백 경로 유지.

### 주의
- 기존 Lab Server/Viewer는 수정하지 않으며 기존 TightVNC, 방화벽, 서비스 설정도 수정하지 않는다.
- 비밀번호 접속 정책은 별도 논의 전 변경하지 않는다.
- 신규 Agent 파일에는 개인키/암호를 기록하지 않고 현재 Service↔Agent IPC는 remote keyboard/mouse 및 화면 데이터를 지원하지 않는다.
- 이 문서만으로 실제 사용자 최종 수용 테스트를 시작할 수 없다. 위 제품화 게이트가 먼저 통과해야 한다.
