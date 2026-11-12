# TJ.RemoteAccess — M3 서비스/세션 에이전트 구현 설계 게이트 (2026-11-12)

## 현재 기준

M2 Lab은 Windows 로그인 사용자 프로세스에서 Server/Viewer가 실행된다. 2026-11-12 사용자가 기존 Lab의 실기 수용 8/8 PASS를 제공했고, 이후 개별 Viewer 인증, TLS 허용/차단 및 공유 프레임 전송 기능을 개발했다.

**이번 변경에서 Windows 서비스나 잠금·UAC 제어는 구현·설치하지 않았다.** 기존 TightVNC 2.8.90 서비스와 설치·포트·레지스트리·드라이버를 건드리지 않았다.

## M3 필수 분리 아키텍처

1. TJRA.ControlService (Windows Service / 서비스 계정)
   - TLS 1.2/1.3 종단, 서버 개인키/Viewer 공개키 지문 허용 목록, 로그/세션 메타데이터만 관리.
   - 사용자 데스크톱 화면 캡처 및 SendInput 호출을 서비스에서 직접 하지 않는다.
   - 현재 사용자의 세션에서 서명된 Agent가 승인되기 전에는 입력 명령을 전달하지 않는다.
   - 외부로 VNC/RFB 레거시 raw listener를 열지 않으며, 서비스 네트워크 포트는 Lab 15444와 별도로 구성한다.
2. TJRA.SessionAgent (인증된 대화형 사용자 세션 프로세스)
   - 사용자 로그온 세션과 실제 데스크톱에 귀속. 제한된 권한에서 캡처·입력을 수행.
   - Service와 Windows Named Pipe IPC; 기본 DACL을 사용하지 않고 LocalSystem 및 실제 사용자 Logon SID 등에 명시적으로 제한.
   - PIPE_REJECT_REMOTE_CLIENTS, 양방향 nonce/세션 ID/프로토콜 버전 검증, 최대 메시지 크기·시간 제한.
   - 사용자에게 화면공유 상태/제어자 수를 명시적으로 표시. 승인 없이 원격 세션에 입력 권한을 주지 않음.
3. TJRA.Viewer / 관리 도구
   - Viewer별 별도 TLS 인증서와 등록·차단·갱신, 서버 피어 지문 검증.
   - 관리자 접근, ViewOnly, Controller 역할 분리. 원격 연결은 기본 거부하고 명시적 권한 부여.
4. EngineHost / GPL TightVNC fork와의 경계
   - TightVNC 2.8.90 GPL 파생 컴포넌트는 격리된 EngineHost에서만 사용.
   - legacy VNC/password 인증 경로와 직접 listener, legacy 파일 전송/입력 sink를 모두 비활성화하고 Gate를 최종 OS 입력 경로에 강제.
   - 기존 TightVNC 서비스 이름, 프로세스, registry, IPC, hook DLL, mirror driver, 기본 TCP 5900과 충돌 금지.
   - GPL v2 소스 제공 의무와 개인 라이선스 범위를 공개 배포 전에 별도 검증.

## 서비스 상태 및 인가

단계: STOPPED → BOUND → TLS_AUTHORIZED → AGENT_ATTESTED → VIEW_ONLY → CONTROL_GRANTED → DISCONNECTED.

입력 승인 필수 조건: 활성 TLS 신원 + 차단되지 않은 Viewer별 인증서 + 명시적 Controller 권한 + 유효한 Win32 User Session ID + 사용자 정책 및 Agent 동의 + 세션 nonce 재검증 + 최종 EngineSessionGate 승인.

Viewer 인증서 폐기/잠금·로그오프/IPC 단절/세션 전환/서비스 중지 시 기존 입력 상태를 해제하고 더 이상 해당 Viewer 입력 패킷을 전달하지 않는다. Agent 인증 정보는 Windows 세션 종료 시 폐기한다.

## Winlogon, 잠금 화면, UAC

일반 사용자 SessionAgent에서 사용 중인 입력 API를 서비스로 옮기는 것만으로 잠금/로그온/UAC 화면을 제어할 수 없다. Windows 세션 0 격리와 보안 데스크톱 권한 경계를 유지해야 한다.

보안 데스크톱은 별도 위협 모델과 명시적 운영자 승인·권한/감사 체계 및 Windows 10/11 VM 실기 검증 후에만 구현한다. 원격 UAC 자동 승격이나 보안 프롬프트 우회는 하지 않는다.

## 단계별 제품화 게이트

- M3.1: 순수 Service↔Agent IPC 프로토콜 계약/프레임/권한/인증 테스트, 보안 DACL 부정 테스트
- M3.2: 설치되지 않는 테스트용 Service/Agent 빌드, Windows 세션 ID/로그오프/잠금 이벤트 수집
- M3.3: Windows Service 설치 스크립트와 롤백, 수동 관리자 확인, 서비스 중복 설치·포트 충돌 검증
- M3.4: EngineSessionGate 실 연동, 입력 sink 바인딩, 원본 TightVNC 완전 공존, 인증서 교체·폐기
- M3.5: 잠금/재로그인/재부팅/서비스 복구, 3명 동시 세션/감사 로그 및 장시간 성능 테스트
- M4: PIE.FileTransfer Master CLI/Named Pipe 공용 API 및 Launcher 연동
- M5: 270 PPM 검사 중 CPU/GPU/SSD/캡처 영향 측정. 원격 종료는 검사프로그램을 종료시키지 않음
- M6: 서명된 설치 패키지, SHA-256 배포 무결성, 공개 GPL 소스 및 라이선스 제공
- M7: 고객사 승인/실기 검증 후 기존 TightVNC 단계별 제거. 제거 전 rollback 경로 유지

## 배포 경계

본 문서는 향후 구현을 위한 설계입니다. M2 Lab은 현재 소유한 격리 테스트 PC에서만 사용하며, 서비스 설치/자동 시작, 잠금·UAC 제어, 무인 원격 접속, 고객 설비 사용은 **지원하지 않는다**.
