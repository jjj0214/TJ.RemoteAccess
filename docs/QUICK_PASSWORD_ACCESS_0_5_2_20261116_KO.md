# TJ.RemoteAccess 0.5.2-preview — IP + 비밀번호 간편 접속 (2026-11-16)

## 목적과 접속 정책

목표는 TightVNC처럼 Viewer에서 **서버 IP와 비밀번호만 입력**하여 원격 화면/입력 기능을 이용하는 것이다. Viewer 측 PFX/Client 인증서 파일을 요구하지 않는다.

**TLS 1.2/1.3 암호화는 항상 사용한다.** 인증 없는 접속이나 비암호화 Password/RFB 포트는 제공하지 않는다. 첫 연결에서 자기서명 서버 인증서의 지문을 서버 PC와 비교·승인해야 하며, 이후 같은 IP:Port에 대한 지문이 변경되면 자동 재등록 없이 거부한다.

- 인증서 로그인: 기존 포트 `15544`, 기존 Viewer별 인증서 방식 **유지**.
- 간편 비밀번호 로그인: `15545` (TLS 별도 포트, 서버 기본 `15544 + 1`). **서버에서 Password access를 설정해야만 활성화**. 기본 비활성.
- Server 동시 Viewer 3명, Controller 1명 제한 유지. Password 로그인은 **ViewOnly 기본값**; Server의 Password access 설정에서 원격 입력을 명시적으로 허용할 수 있다.
- 비밀번호 6~128자, UTF-8 최대 256바이트, PBKDF2-SHA256 600,000 iterations+32-byte salt verifier만 서버 사용자 설정에 저장. Viewer 비밀번호 **저장하지 않음**. IP별 로그인 오류 5회/분 및 전체 20회/분 도달 시 2분 제한.
- Server 인증서 SHA-256은 `%LOCALAPPDATA%\TJ.RemoteAccess\viewer-profiles.json`에 Host+Port별 저장. 공개 지문만 보존; 비밀번호나 Viewer 개인키를 저장하지 않음.
- 최초 연결만 신뢰 확인, 이후 동일 Host:Port 접속은 저장된 핀으로 엄격 검증. 서버 인증서가 변경되면 **접속이 차단되는 것이 정상**. 기존 프로필을 삭제하고 새 서버 신원을 수동으로 재확인해야 한다.

## 설치·업데이트

코드/스크립트: `feature/chatgpt-m2-engine-foundation`.

1. 기존 Server를 트레이 `Exit TJ.RemoteAccess`로 완전히 종료하고 Viewer도 종료.
2. 각 테스트 PC에서 최신 소스 사용. 개발 PC/빌드 환경의 .NET 9 SDK가 필요.

```powershell
git pull --ff-only
dotnet build .\TJ.RemoteAccess.sln -c Release
powershell -NoProfile -ExecutionPolicy Bypass -File .\tools\Install-TJRemoteAccess-Preview.ps1 -Mode Both
```

원격 PC에서 오프라인 설치가 필요하면 `Build-TJRemoteAccess-Preview-OfflinePackage.ps1`으로 0.5.2-preview self-contained ZIP 제작 후 ZIP SHA-256을 **별도로 확인**하고, 기존 `Install-TJRemoteAccess-Preview-Offline.ps1`을 사용한다.

**기존 TLS 인증서/프로필 유지:** 기존 `server.json`이 있으면 `Setup init`을 다시 실행하지 않는다. 설치기는 TightVNC, Lab 인증서, 방화벽 정책 또는 Windows Service를 변경하지 않는다.

## Server PC: Password Access 활성화

1. `TJ.RemoteAccess.Server.exe --allow-lan` 실행, 트레이 `TJ` 아이콘 우클릭 → **Password access...** 선택.
2. 6자 이상 새 비밀번호 및 확인 입력. 필요한 경우에만 `Allow remote keyboard / mouse` 체크(기본은 보기 전용).
3. **Set / change password** 선택. 서버가 이미 접속 대기 중이라면 현재 TLS 접속을 종료하고 자동으로 다시 시작해 새 비밀번호 전용 포트(`기본 15545`)를 적용한다. 중지 상태였다면 트레이에서 `Start listening`.
4. 첫 연결 Viewer가 표시한 64자리 SHA-256 지문을 비교하려면 Server의 `Password access...` → `Copy Server pin`에서 확인.
5. 비밀번호 접속을 중단하려면 `Disable password`. 기존 비밀번호 세션은 철회되며, 활성 Server는 자동으로 TLS 리스너를 다시 시작한다.

## Viewer PC: 간편 접속

1. `TJ.RemoteAccess.Viewer.exe` 실행.
2. 상단 **Quick connect (IP + password)** 클릭 → Server IP, 포트(기본 15545), 비밀번호 입력 → Connect.
3. **최초 연결**일 때만 Viewer가 공개 TLS 지문을 조회하고 연결을 종료한다. 이 지문을 Server PC의 `Copy Server pin`과 비교한다. 일치하면 Yes, 일치하지 않거나 확인할 수 없으면 No.
4. Yes를 누른 뒤 Viewer가 **새 TLS 연결을 만들면서 앞에서 확인한 지문을 다시 검사**한다. 이 검사가 성공해야 비밀번호를 전송한다. 서버가 승인해 Welcome을 보내야만 지문을 프로필에 저장한다.
5. 다음 접속부터는 같은 IP:Port의 저장된 서버 지문을 자동 검증하고 비밀번호만 입력하면 된다. 사용자에게 Viewer 인증서/PFX가 필요 없다.
6. 기존 인증서 등록 Viewer는 그대로 `Add server`, `Connect`로 사용 가능. 고급 기능 `Add password server`의 수동 인증서 지문 등록 기능도 유지.

**참고:** 최초 공개 지문 검사는 외부 인증기관의 서버 신원 보증을 대체하지 않는다. 처음부터 가짜 서버와 연결될 수 있으므로 반드시 실제 Server PC에서 지문을 확인한다. 전용 VPN/사설망에서 사용하며 인터넷에 공개 포트를 직접 열지 않는다.

## 자동 검증과 마지막 실기 체크리스트

소스코드/빌드 단계:
- Viewer 인증서 없는 TLS 비밀번호 로그인, 비밀번호 보관 금지, 기본 ViewOnly, 로그인 차단 정책 유지
- TLS 지문만 조회하고 **어떤 애플리케이션 패킷도 전송하지 않는** 최초 접속 사전 단계
- 첫 연결 사용자가 신원을 확인한 이후에만 2차 TLS pinned 접속에서 비밀번호 전송
- 지문 불일치 거부, 인증 성공 이후에만 IP:Port별 지문 저장, 기존 지문 무단 교체 거부
- 비밀번호 정책 변경 시 TLS 리스너 재시작 조건
- `TJ.RemoteAccess.sln`, `TJ.RemoteAccess.Lab.sln` Release 및 Windows xUnit/Python/native 회귀

최종 사용자 테스트 (독립 Windows 테스트 PC에서만):

| ID | 확인 항목 | 기대 결과 |
|---|---|---|
| Q01 | Server Password Access 기본 상태 | 기본 Disabled, 별도 TLS password 포트 미개방 |
| Q02 | Server에서 6자 이상 비밀번호 설정 | 별도 포트 15545 TLS 활성, 재시작 요구 없이 적용 |
| Q03 | 신규 Viewer에서 IP+Password만 입력 | Viewer 인증서 없이 최초 서버 지문 확인 화면 표시 |
| Q04 | 최초 지문 확인에서 **No** | 비밀번호 미전송, 서버 프로필 미등록 |
| Q05 | Server PC에서 SHA 지문 확인 후 **Yes** | 2차 TLS 연결, 비밀번호 인증 후 영상 수신 |
| Q06 | Viewer 재실행 후 동일 서버 접속 | 지문 확인 화면 없이 비밀번호만 입력, 저장된 신원 유지 |
| Q07 | 잘못된 비밀번호 5회 | 인증 거부 및 해당 IP 약 2분 제한 |
| Q08 | Password ViewOnly 상태에서 키보드·마우스 | 입력 거부. Server에서 명시적 입력 허용 시 Controller 1명만 |
| Q09 | Viewer 3명 동시 접속, 4번째 시도 | 3명 성공, 4번째 거부; 1명만 제어권 보유 |
| Q10 | Server Password 변경 또는 Disable | 기존 세션 해제, 변경된 정책 자동 적용 |
| Q11 | Server 인증서 교체 (격리된 테스트 PC) | 저장된 SHA와 달라 접속 차단. 자동 신뢰 갱신 금지 |
| Q12 | 기존 인증서 Viewer 접속 | TLS 15544의 기존 방식 정상 동작, TightVNC 서비스 영향 없음 |
| Q13 | 트레이 X/최소화/Exit, 잠금·해제/UAC | 기존 트레이 동작 및 보안 데스크톱 차단 유지 |

개발 자동 테스트가 PASS여도 위 **실제 WinForms GUI 조작 / 두 PC 네트워크 / 실제 잠금·UAC** 동작은 자동 수용 완료를 의미하지 않는다. 이 결과를 바탕으로 최종 사용자 확인이 필요하다.

## 정식 TightVNC 완전 대체와 별개로 남은 항목

Windows Service/SessionAgent 실제 LocalSystem 설치·재부팅 복구, Winlogon/UAC 보안 데스크톱의 설계/실기, 정식 MSI/서명/롤백, 3 Viewer 장시간 및 270 PPM 성능은 0.5.2-preview의 범위가 아니다. 이번 기능은 사용 편의성 향상이며 TLS/Viewer 인증서 기존 보안 기능을 삭제하지 않는다.
