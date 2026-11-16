# TJ.RemoteAccess 0.5.1-preview 트레이 및 독립형 원격제어 수용 검증 (2026-11-16)

이 문서는 독립형 `TJ.RemoteAccess.Server.exe`의 **트레이 동작과 기존 원격제어 기능**을 함께 확인하기 위한 테스트 계획이다. **서버/Viewer 기능의 정상 동작을 의미할 뿐 정식 Windows Service, Winlogon/UAC, 고객사용 MSI 준비 완료를 뜻하지 않는다.**

## 설치 전 주의

- Windows 10/11 x64 테스트 PC 2대, 추가 Viewer PC 2대(총 3명 시험 시)와 격리 테스트 LAN. 기존 TightVNC 설정/서비스와 별도 포트 사용.
- 기존 서버 설정이 있으면 **`Setup init`을 재실행하거나 PFX를 재발급하지 않는다**. 설정은 `%LOCALAPPDATA%\TJ.RemoteAccess`에 유지.
- 업데이트 전 서버 화면에서 X만 누르면 트레이로 숨겨질 뿐 프로세스는 계속 실행된다. **트레이 우클릭 → `Exit TJ.RemoteAccess`**를 선택한다. Viewer/Setup도 종료 후 업데이트.
- 별도 빌드: `dotnet build .\TJ.RemoteAccess.sln -c Release`. 사용자용 업데이트:
  `powershell -NoProfile -ExecutionPolicy Bypass -File .\tools\Install-TJRemoteAccess-Preview.ps1 -Mode Both`.
- 최초 설치 시 Server의 `ListenAddress`를 테스트 LAN IP로 지정하고 `--allow-lan`으로 실행. 서버 인증서와 Viewer TLS pin 확인.
- 정상 접속 시험은 `TJ.RemoteAccess.Server.exe`, `TJ.RemoteAccess.Viewer.exe`를 사용한다. Lab 실행 파일은 다른 테스트 제품이다.

## A. 트레이 UX 자동/수동 수용 체크

| ID | 시험 | 정상 판정 |
|---|---|---|
| T01 | Server 실행, 미시작 상태 | 회색 `TJ` 아이콘, `Stopped`, `Start listening` 활성 |
| T02 | `Start listening` | 파란색 아이콘, `Listening`, Viewer 0/3, `Stop listening` 활성 |
| T03 | 첫 Viewer TLS 연결 | 초록색 아이콘, Viewer 1/3, Controller 상태 일치 |
| T04 | Viewer 2·3 연결, Viewer 4 거부 | Viewer 3/3, 제어자 최대 1명, 네 번째 연결 거부 |
| T05 | Server 창 X 닫기 | 작업 표시줄 창만 숨고 트레이 표시 유지, 기존 Viewer 제어 지속 |
| T06 | 트레이 TJ 아이콘 더블클릭 | 기존 Server 창 복원, 연결/프로필/인증서 그대로 |
| T07 | 최소화 후 트레이 복원 | 동일 동작; 새 Server 프로세스 생성 없이 복원 |
| T08 | Server 실행 중 EXE 재실행 | **두 번째 Server 또는 TLS 리스너가 생성되지 않고 기존 창 복원** |
| T09 | `Client access...`, `Password access...` | 트레이에서 창을 복원하고 해당 설정 UI 표시; 기존 설정 유지 |
| T10 | `Stop listening` | 회색, 모든 Viewer 종료, 눌린 키/마우스 상태 해제 |
| T11 | `Exit TJ.RemoteAccess` | Viewer 연결 시 확인 표시, 취소하면 유지, 확인하면 완전 종료/아이콘 제거 |
| T12 | `--allow-lan --auto-start` 실행 | 자동 접속 대기 후 트레이 상주, 불필요한 시작 알림 팝업 없음 |
| T13 | LAN 포트와 TightVNC 공존 | Preview TCP 15544 및 선택적 15545, TightVNC 기본 5900 변경 없음 |
| T14 | 잠금/UAC/로그오프 시도 | 기존 TLS 화면/입력 차단. Windows 보안 데스크톱 원격 제어 **실패가 정상** |
| T15 | 서비스 감독 메뉴 | 현재 사용자만 선택적 동의 가능; 관리자 정책 없는 서비스는 자동 Server 시작 불가 |
| T16 | 잠금→잠금 해제 및 명시적 중지 | 잠금 이전에 Listening이었던 경우만 정상 Default 데스크톱 확인 후 10초 내 재시작 시도. 명시적 Stop/로그오프/연결 해제는 자동 재시작 금지 |

트레이 표시/복원과 서비스 실행 동작은 개발 장비에서 실제 WinForms UI를 자동 클릭하여 검증한 것이 아니다. 반드시 사용자 실기 결과로 완료 처리한다.

## B. 회귀·보안·장시간 확인

- Viewer 프로필 저장/재시작, Viewer-2/3 인증서 구분, ViewOnly 입력 거부, Controller 연결 해제 시 키/마우스 해제.
- 비밀번호 접속은 기존 TLS pin/기본 비활성 정책 유지; 비활성·비밀번호 오류·한도 초과 후 접속이 거부되는지 확인.
- 3명 접속 장시간 유지 동안 Viewer 1·2·3 전환/재연결 및 CPU/메모리/GDI/USER 객체 추세 기록.
- 읽기 전용 측정:

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File .\tools\Measure-TJRemoteAccess-Preview.ps1 -Target Server -DurationSeconds 600 -IntervalSeconds 10 -Label "3Viewer" -SummaryOnly
```

- 270 PPM 설비 검사 Tact는 **설비 실제 데이터**로 비교해야 하며, 위 도구만으로는 영향이 없다고 판정할 수 없다.
- Windows Service는 별도의 폐기형 VM에서 `docs/STANDALONE_PREVIEW_BUILD_TEST_20261116_KO.md`의 B단계 게이트로 검증해야 한다. 실행 전에 반드시 VM 스냅샷을 만든다.
- Windows 로그인 화면, UAC, 정식 MSI/코드 서명/서비스 업그레이드 롤백은 아직 독립적 게이트가 남아 있다.

## 검증 후 기록

테스트 PC Windows 버전/로그인 세션, TJRA Preview 버전, Server IP/포트(비밀번호·키·PFX는 기록 금지), T01~T16 PASS/FAIL, 이상 시 JSON 로그와 화면을 수집한다. **고객사/생산설비 배포는 정식 제품 수용 게이트 전까지 금지한다.**
