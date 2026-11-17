# TJ.RemoteAccess 0.5.5-preview — Compact Server / Viewer 기능 확장

작성: 2026-11-17 · 격리 테스트 전용. 정식 TightVNC 대체·고객 현장 운영 버전이 아님.

## 1. 빌드/설치

`main`의 0.5.4 Compact와 별개로 `feature/chatgpt-m3-viewer-access-ux`에서 신규 기능을 개발했다.
Windows 10/11 x64의 .NET 10 SDK, PowerShell 7, Inno Setup 6가 있는 개발 PC:

```powershell
pwsh -NoProfile -ExecutionPolicy Bypass -File .\tools\Build-TJRemoteAccess-CompactInstaller.ps1 -Version 0.5.5-preview -RuntimeVersion 10.0.15
```

출력: `out\compact-dist\TJ.RemoteAccess-0.5.5-preview-Compact-Setup-win-x64.exe` 및 SHA-256, GPL `source.zip`, Runtime provenance, 용량 보고.
설치기는 현재 사용자 전용·미서명·단일 EXE이며 별도 전역 .NET 설치가 필요 없다. Microsoft 공식 .NET Core + WindowsDesktop Runtime ZIP은 각각 SHA-512로 검증하여 하나의 사설 Runtime 폴더로 통합한다.

- Server/Viewer/Both 설치 옵션 유지.
- Server 시작 메뉴는 `--allow-lan`로 실행하며, 정확한 현재 사용자 Compact Server 설치 경로에서 직접 실행해도 LAN 허용 의도를 인식한다.
- 새로 `Setup init`하여 생성하는 **제품** 서버의 ListenAddress는 `0.0.0.0`. 이전에 저장한 `server.json`은 자동 변경하지 않는다. 원본 Lab Server는 계속 127.0.0.1 기본값.
- 설치기에는 사용자가 직접 선택하는 **로그인 후 Server 자동 실행** 옵션이 있다. 기본 해제. 선택 시 HKCU Run `--allow-lan --auto-start`를 등록하며 제거 시 해당 값 삭제. 서비스 설치·방화벽 예외·서버 인증서 자동 변경·TightVNC 수정 없음.
- 최초 Server는 여전히 **현재 사용자 인증서와 설정이 필요**하다. 새 PC는 `TJ.RemoteAccess.Setup.exe init`를 현재 사용자 세션에서 명시적으로 실행한다. PIN·인증서·비밀번호 검증자는 기존 설정 유지.

LAN 바인딩은 원격 접근을 허용할 수 있으므로 제한된 VPN/사내망에서 사용하고 Windows 방화벽·네트워크 접근 정책은 관리자가 별도로 검토한다. Server가 인증서·암호 설정 없이 접속을 받아들이는 것은 아니다.

## 2. Server 권한 관리

Server 메인 버튼 및 트레이에 단일 **Access and security** 진입점을 추가했다.
- **Authorized viewers** 탭: 등록된 인증서 Viewer, 2/3번 Viewer 생성, 활성화/비활성화, ViewOnly/Control 권한.
- **Password + TLS** 탭: TLS 인증서 지문, 별도 password TLS 포트, 비밀번호 활성화·변경·폐기. 인증서 방식과 별개 포트를 유지한다.
- 비밀번호 정책 변경 시 접속 리스너를 정지·재시작하며 기존 패스워드 세션을 폐기한다. 기존 3 Viewer / 1 Controller 제한 및 TLS PIN, 접속 거절 정책은 유지한다.

## 3. Viewer 사용 편의성

- **Zoom**: Fit / 50 / 75 / 100 / 125 / 150 / 200%. Fit은 화면 맞춤, 확대 시 스크롤.
- **Pause image / Resume / refresh**: 로컬 화면만 멈추며 TLS 읽기와 keepalive는 계속 유지. 재개하면 최신 프레임을 갱신. Server 캡처 자체를 중지하지 않는다.
- **Full screen**: 전체 화면, 종료 버튼과 Esc.
- **View only**: Viewer가 입력·파일 전송을 내보내지 않음. 서버의 Control 권한보다 우선하여 제한.
- **Send keys**: Ctrl+Esc, Alt+Tab, Win+D, Ctrl+Alt+End. Ctrl+Alt+Del/Winlogon/UAC 보안 데스크톱 입력은 지원하지 않으며 억지로 우회하지 않는다.
- **Mouse wheel**: 명시적으로 권한이 부여된 Controller만 전송.
- **Connection options**: 보기 전용 및 화면 맞춤 변경. Server/Viewer의 기존 Saved server, Quick connect, TLS 인증서 PIN과 IP+비밀번호 선택 기능을 유지. 프레임 캡처는 모든 Viewer가 공유하므로 RFB 호환 인코딩/색상 깊이 및 per-Viewer JPEG 품질 협상은 아직 지원하지 않는다.

**이 구현은 TightVNC RFB Viewer를 재사용하는 것이 아니다.** 현재 제품은 독립 인증 TLS + JPEG 프레임 기반이며 원본 TightVNC와의 통신 프로토콜 호환을 제공하지 않는다.

## 4. 양방향 파일 송수신

- Viewer→Server: Viewer **Send file** → Server 사용자에게 SaveFileDialog 표시 → 명시 승인 후 TLS 전송.
- Server→Viewer: Viewer **Receive file** → Server 사용자가 전송 파일을 OpenFileDialog로 선택 → Viewer의 SaveFileDialog에서 명시 승인 후 TLS 전송.
- 기존 상호 인증 또는 핀 인증된 TLS 연결의 **실제 Controller 역할**과 최종 Server 권한을 파일 작업 시마다 재검증. 별도 raw TCP 파일 포트 없음.
- 한 파일 최대 **512MiB**, 블록 최대 32KiB, GUID/바이트 오프셋 및 파일명 유효성 검사, 완료 후 SHA-256 비교, 임시 `.tjra-*.part` 사용, 불완전 작업 취소 시 삭제.
- 송신 측 임의 경로를 수신 경로로 지정할 수 없다. 다운로드 파일의 로컬 저장 위치는 수신자가 선택한다.
- 프로그램 종료/권한 폐기/네트워크 중단에서 전송을 취소한다. 일괄 폴더 전송, 재시작 이어받기, 동시 다중 파일, 전송 속도 UI, 원격 탐색기 및 원자적 다중파일 롤백은 **미구현**.
- 인터넷 등 신뢰되지 않은 망에 개방하지 말고, 업로드/다운로드 파일은 별도 백신 및 출처 확인 권장.

## 5. M3 감독 및 안전 경계

- 기존 LocalSystem ControlService는 **별도의 관리자 소유 서비스 경로**로만 설치해야 한다. Compact per-user 설치기는 관리자 권한이나 서비스 설치를 요구하지 않는다.
- 사용자 SessionAgent의 로컬 HMAC·PID·SessionId·로그온 SID·익명 상속 파이프 키 검증을 유지한다.
- 정책·동의가 모두 있는 Agent는 Compact 설치기 아래의 정확한 `Server\TJ.RemoteAccess.Server.exe`를 사용자 토큰으로 감독하는 경로를 찾을 수 있다.
- 일시적인 Agent IPC 종료에는 동일 콘솔 세션과 관리자 정책을 재확인 후 최대 2회 지연 재시도한다. 인증·권한 실패는 재시도하지 않으며 화면 잠금/로그오프는 즉시 폐기한다.
- **불포함:** 로그온 전 화면·UAC/Winlogon 원격 제어, 서비스의 별도 보안 데스크톱 캡처/입력 엔진, 자동 승격 설치, M3 실제 LocalSystem 서비스/재부팅 실기 검증. 또한 기본 서비스는 status-only로, 사용자와 관리자의 별도 승인 없이는 원격 Server를 감독하지 않는다.

## 6. 검증 및 최종 PC 수용

자동화: Release x64 전체 솔루션 빌드, Preview/Lab xUnit, 실제 Windows loopback 상호 TLS 파일 송수신(업로드·다운로드), 프로토콜 부정 검증, M3 재시도/잠금·권한 경계, Python 계약 검사. GitHub Windows CI에서 .NET 10 Compact 단일 설치기, 무인 설치/업데이트/제거 및 SHA-256/용량을 다시 확인해야 한다.

실제 PC 검증 체크리스트:

1. 기존 `server.json`/PIN/Viewer 프로필 백업 후 0.5.5 Compact 설치·재설치, 0.5.4에서 설정 유지.
2. Server 바로가기 또는 설치된 EXE 직접 실행 시 LAN 허용 오류 없음. 방화벽은 별도 관리.
3. 비밀번호 및 인증서 Viewer 각각 정상 접속; 3명 동시 접속 중 1명만 원격 제어.
4. Server 관리창에서 client enable/disable/control, password on/off/rotate 및 기존 연결 폐기 확인.
5. Viewer Fit/배율/전체 화면/일시정지/재개, 포인터·키·휠·특수키, 보기 전용 차단 확인.
6. 업로드/다운로드 각각 원본 SHA-256과 수신본 SHA-256 일치. 거부, 권한 폐기, 네트워크 중단, 잘못된 해시, 512MiB 초과 취소/임시 파일 정리 검증.
7. 선택형 로그인 후 시작 및 프로그램 종료/재로그인 테스트; M3는 격리 VM에서 서비스/Agent/정책을 **별도 승인**해 검증.
8. 기존 TightVNC 서비스·포트 5900이 유지되고 공존하는지 확인. 사용자 PC의 설정이나 기존 TightVNC를 자동 제거하지 않는다.

**안정화 완료의 기준:** 위 항목 실기 PASS, .NET 10 설치기 CI PASS, 코드 서명/배포 적합성 검토를 마치기 전까지 정식 Release 및 TightVNC 운영 대체를 보류한다.
