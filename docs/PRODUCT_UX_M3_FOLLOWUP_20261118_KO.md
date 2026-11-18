# TJ.RemoteAccess 4개 기능 범위 후속개발 검증지시서 (2026-11-18)

> 개발 브랜치: `feature/chatgpt-product-ux-m3`. 기존 TightVNC 및 `main`은 보존한다.
> 기능 구현 및 자동 회귀 통과와 실제 PC/SCM 수용 검증은 별도로 기록한다. 임의 운영 배포 금지.

## 1. Viewer↔Server 파일 송수신

- 기존 TCP 15544(인증서 TLS) 및 15545(별도 비밀번호 TLS) 연결의 인증 후 채널만 사용. 평문/독립 파일전송 포트는 열지 않는다.
- **실제 Controller 권한을 가진 Viewer만 전송**. 서버는 각 파일마다 해당 PC 사용자의 허가를 얻고, 다운로드는 서버 사용자가 직접 선택한 로컬 파일만 제공한다. UI 버튼/상태 및 완료/거부 회신 추가.
- 송수신 한도 **64 MiB/파일**, 청크 **48 KiB**, 하나의 연결당 진행 중 전송 1건. 악성/예약 파일명, 경로 순회, 재분석 지점(reparse point), 기존 파일 덮어쓰기, out-of-order chunk 차단. `*.partial`에 작성 후 전체 SHA-256이 일치하는 경우에만 원자적 파일명 변경. 실패/연결 종료 시 부분파일 정리. 서버 수신 폴더: 현재 사용자 `%LOCALAPPDATA%\TJ.RemoteAccess\Received`.
- 파일 송수신 전송률, 프레임 영향 및 동시 3 Viewer에서 실제 GUI 클릭 검증은 실기에서 확인 필요. 미지원: 이어받기(resume), 다중 파일/폴더 일괄 전송, 무제한 크기.
- Controller 및 정상 사용자 세션인지 지속 확인. 세션 잠금, 인증 취소, 연결 해제 시 자동 허가나 오래된 전송 재개 금지.

## 2. Viewer Connection Options

- `Connection options...`: View only(서버가 제어권 부여하지 않음), 전체화면, 인증서 프로필에서만 최대 3회 제한 자동 재연결, JPEG 품질 25~90, FPS 2~20.
- 서버는 3 Viewer/1 Controller 제한을 유지한다. JPEG/FPS는 동일 화면 공유 프레임 생산기 1개에 적용되므로 다른 Viewer에도 영향을 준다. View only 모드 변경은 새 접속 시 유효.
- 원격 마우스 휠/키 전송은 TLS Controller 권한 및 실제 Windows Default desktop 확인 뒤에만 적용된다. Ctrl+Shift+Esc는 Controller만. Winlogon, Ctrl+Alt+Del, UAC, 잠금화면은 지원하지 않는다.
- Fit, 50/75/100/125/150/200%, 스크롤, 화면 표시 일시정지/재개, PNG 저장. Pause는 **Viewer 표시만 멈추며 서버 캡처는 계속**. 마우스 스크롤 위치별 좌표/드래그는 실제 GUI 검증 필요.

## 3. M3 Windows Service + 사용자 SessionAgent 통합

기존 `M3ControlService`와 `M3SessionAgent`는 이름 있는 로컬 파이프에서 세션·PID/HMAC 인증을 수행하고 세션 교체/로그오프 때 이전 Agent를 폐기한다. `M3SupervisedDesktopHost`는 사용자 승인과 관리자 정책이 **모두** 활성화된 경우에만 정상 로그인된 Default desktop에서 독립 Server를 실행한다.

- 별도 **ServicePreview** ZIP은 .NET 9 self-contained, 관리자 보호된 `%ProgramFiles%\TJ.RemoteAccess\ServicePreview`에만 설치한다. 일반 사용자 권한으로 설치되는 Compact EXE와 섞어서 LocalSystem으로 실행하면 안 된다.
- ServicePreview 기본은 LocalSystem 자동 시작이나 화면/원격 접속은 **비활성**. 설치·업데이트·제거 시 관리자 승인과 사전에 별도 검증한 ZIP SHA-256 필수.
- 관리자 정책 `-EnableSupervisedDesktop`와 로그인 사용자 `Setup.exe service-host enable --confirm-local-owner`가 모두 있어야 사용자 화면 Server 관리 시작. 제어 화면은 잠금, 로그오프, 사용자 전환 때 중지. 자동 기동 정책은 사용자가 따로 승인해야 한다.
- 이미 로그인된 세션 Agent가 있을 때만 원격 접속. **Windows 로그인 전, 화면 잠금 중, UAC/Winlogon, 로그인 없이 원격 접근은 지원하지 않는다.** "서비스 자동 시작"과 "무인 보안 데스크톱 제어"는 전혀 다른 사항이다.
- Windows CI에서 **두 동의 모두 OFF인 상태**로 실제 SCM Service 설치, 시작, 보호된 경로·ACL, 리스너 미노출, 동일 버전 업그레이드, 제거를 검증한다. 실제 두 동의를 ON으로 한 상호작용 PC 재접속은 격리 Windows PC 수용 항목으로 남긴다.

## 4. 최종 설치 및 통합 검증 게이트

A. 정적/단위: Release 빌드 무오류/경고, Preview·Lab 테스트, Python 계약 테스트, 파일전송 승인·무결성 및 제한. C++ native Regression과 PowerShell AST parse.
B. **Compact Installer Windows CI**: .NET 10 런타임 SHA-512, Server/Viewer/Setup 한 EXE, Viewer-only 및 Both 설치·업데이트, 자동 최초 Server TLS 인증서 생성, 설정 보존, 제거, GPL 소스/미서명 고지, SHA-256·실제 바이트 수.
C. **Service Preview Windows CI**: 별도 관리자 보호 서비스 ZIP, LocalSystem 시작, Consent OFF일 때 원격 포트 미오픈, Agent 상태, 업그레이드·제거, 기존 TightVNC/방화벽 미변경.
D. **격리 2-PC 수용 테스트(코드·자동 CI만으로 대체 불가)**: 실제 Server/Viewer 접속/인증서와 비밀번호(지문 비교), 파일 0B/다중 청크/최대 크기/거부/손상/취소, 스크롤/확대/전체화면, 3 viewers·1 controller, 사용자 잠금 해제·로그오프·서비스 재시작, 설치 후 설정 보존. Windows 10/11 두 버전 각각 검증하고 UAC 제어 차단 확인.
E. 장기 24시간 성능·프로세스/메모리·검사기 Tact 영향, 재부팅/자동 재연결, TightVNC 공존/롤백 검증 후 사용자 승인으로 `main` 병합·정식 Release 준비.

## 주의

본 개발은 GPL 파생 프로젝트의 프리뷰 기능 개선이다. 미서명, 보안 감사·코드 서명·MSI 및 고객사 현장 안정성 수용 전에는 TightVNC 대체 완료라고 표시하지 않는다. 검사기/고객사 PC의 방화벽, TightVNC, 인증서, 사용자 설정, Windows Service를 원격으로 임의 변경하지 않는다.
