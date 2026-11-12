# TJ.RemoteAccess Lab - 비밀번호 접속과 Viewer GUI 자동 발급

2026-11-12 | feature/chatgpt-m2-engine-foundation | Windows 10/11 x64 LAB

## 1. 인증 방식

| 기능 | 기존 Viewer 인증서 | 새 비밀번호 |
|---|---|---|
| 서버 포트 | TCP 15444 | TCP 15445 |
| 통신 암호화 | TLS 1.2/1.3 필수 | TLS 1.2/1.3 필수 |
| 서버 인증서 SHA-256 지문 | 고정 및 검증 필수 | 고정 및 검증 필수 |
| 사용자 인증 | 별도 Viewer 클라이언트 인증서 | 매 연결마다 비밀번호 입력 |
| 기본 사용 | 기존 설정 그대로 | **비활성화** |
| 접속 권한 | 기존 등록 정책 | 기본 **보기 전용** |

**중요:** 비밀번호 접속은 TLS를 제거한다는 의미가 아니라 클라이언트 인증서 없이 비밀번호로 로그인한다는 의미다. 암호화되지 않은 연결이나 기존 TightVNC의 RFB 비밀번호 인증은 제공하지 않는다. Viewer는 접속하기 전에 Server의 64자리 SHA-256 인증서 지문을 신뢰된 경로에서 확인해야 한다. TOFU 자동 신뢰는 없다.

비밀번호는 서버 전체에서 공유한다. 특정 Viewer만 차단하거나 사용자별 권한을 관리하려면 기존 Viewer별 클라이언트 인증서를 권장한다.

## 2. 설치 업데이트

두 PC 모두 GitHub의 feature/chatgpt-m2-engine-foundation 브랜치를 Pull하고 실행 중인 Lab을 종료한다.

Server PC:

    powershell -NoProfile -ExecutionPolicy Bypass -File .\tools\Install-TJRemoteAccess-Lab.ps1 -Mode Server

Viewer PC:

    powershell -NoProfile -ExecutionPolicy Bypass -File .\tools\Install-TJRemoteAccess-Lab.ps1 -Mode Viewer

**기존 정상 서버에서는 Setup init 재실행 금지.** 인증서, server.json, 기존 Viewer PFX/프로필이 보존된다. 원본 TightVNC 및 Windows 서비스/방화벽 설정은 설치 스크립트가 변경하지 않는다.

## 3. Server에서 비밀번호 접속 켜기

1. Server 프로그램 실행. LAN 서버는 기존대로 --allow-lan 옵션으로 시작한다.
2. 상단 **Password access** 버튼 클릭.
3. 비밀번호 2회 입력: 12~128자, UTF-8 최대 256바이트. 다른 서비스와 중복되지 않는 길고 무작위인 비밀번호 권장.
4. **Allow remote keyboard / mouse**는 기본 꺼짐(View only). 필요할 때만 켠다.
5. **Set / change password** 클릭, 이후 Server **Stop → Start**로 별도 포트 활성화.
6. 대화상자의 64자리 서버 인증서 SHA-256 지문을 Viewer PC에 신뢰된 경로로 전달한다.

15444 인증서 접속은 계속 동작한다. 비밀번호 접속은 서버 기본 포트+1, 즉 기본 15445에서만 동작한다. LAB 격리 LAN에서 포트가 막힌 경우 Viewer IP만 수동 방화벽 허용하며 설치기에서 자동 개방하지 않는다.

**비활성화:** Server의 Password access → Disable password. 이미 인증한 비밀번호 세션도 정책 검사 시 차단되며, Stop → Start를 누르면 15445 리스너가 사라진다. 비밀번호를 다시 켜려면 새 비밀번호를 설정해야 한다.

## 4. Viewer에 비밀번호 서버 등록하기

1. Viewer 상단 **Add password server** 클릭.
2. 이름, 서버 IP/호스트, 15445 포트 입력.
3. Server에서 확인한 SHA-256 지문 64자리 입력. 또는 **Load .cer**로 서버 공개 인증서를 읽고 지문과 용도를 확인한다. 해당 지문/공개 인증서의 출처는 신뢰된 경로로 별도 확인한다.
4. 등록 후 서버 선택 목록의 **[Password TLS]** 항목을 선택.
5. Connect → 비밀번호 입력. Viewer는 비밀번호를 저장하지 않아 다음 연결 때 다시 물어본다.

기존 클라이언트 인증서 및 Viewer 프로필은 보존된다. 서버 지문 불일치/만료, 비밀번호 오류 시 접속이 거절된다.

## 5. Server GUI에서 Viewer-2 / Viewer-3 자동 생성

Server → **Client access** → **Generate Viewer(s)**.

- Viewer name / prefix = Viewer
- Number of Viewers = 2
- Starting number = 2
- Control permission = 필요 시만 켜기(기본 보기 전용)

Generate를 클릭하면 Viewer-2와 Viewer-3용 서로 다른 클라이언트 인증서/PFX가 자동 생성된다. 결과 창에 Viewer별 Client ID, Pairing 폴더, PFX 암호가 **한 번만** 표시된다.

각 Pairing 폴더의 파일:

    client-transfer.pfx
    viewer.json
    server-public.cer

각 Viewer PC로 해당 폴더를 안전하게 복사한 뒤 기존 Viewer **Add server**에서 PFX를 선택하고 암호 입력. 파일/암호는 채팅, 공개 저장소, 공용 네트워크 공유에 올리지 않는다. 기존 Viewer-2/3의 인증서/허용 목록을 덮어쓰지 않고 새 항목으로 생성하므로 필요하면 오래된 Client ID를 Disable한다.

기존 Setup CLI pair-client/list-clients/disable-client/client-control 명령도 유지된다.

## 6. 보안 및 제한

- 서버는 비밀번호를 저장하지 않고 PBKDF2-SHA256 600,000회, 무작위 32바이트 솔트와 32바이트 검증값만 보관.
- 1분 내 IP별 실패 5회, 서버 전체 20회 시 약 2분 잠금.
- 비밀번호 정책 파일이 없거나 손상되면 인증 차단(Fail Closed).
- 비밀번호 변경/비활성화 시 기존 비밀번호 세션의 권한도 갱신 검사로 무효화.
- 전체 인증서/비밀번호 세션은 총 3명까지, Controller는 1명.
- Windows 서비스/잠금·로그인/UAC, 인터넷 공개, 고객 설비 운영 배포는 지원하지 않는다. 소유한 격리 시험 LAN에서만 실행.

## 7. 읽기 전용 진단

    powershell -NoProfile -ExecutionPolicy Bypass -File .\tools\Test-TJRemoteAccess-Lab.ps1

PasswordTLSStatus, PasswordCanControl, Tcp15445Listeners, Tcp15444Listeners, ServerClientRosterStatus를 확인한다. 진단 스크립트는 비밀번호, 솔트/검증값, PFX 파일을 출력하지 않는다.

## 8. 사용자 수용 체크리스트

1. 기존 클라이언트 인증서 접속(TCP 15444) 정상.
2. 비밀번호 미설정 시 15445 미수신.
3. 비밀번호 설정 후 Server Stop/Start 및 올바른 비밀번호 접속 성공.
4. 오입력·잠금·2분 후 재시도 및 비밀번호 변경/비활성화 동작.
5. 비밀번호 기본 ViewOnly / 서버에서 Control 허용 시 1명만 제어.
6. 인증서·비밀번호 Viewer 합산 3명 / 4명째 거절.
7. Client access → Generate Viewer(s)로 Viewer-2와 Viewer-3 자동 생성 후 PFX Import/접속.
8. 기존 Viewer/인증서/TightVNC 영향 없음.

자동 검증은 소스·빌드, 비밀번호 정책, 실제 Schannel TLS 루프백과 기존 인증서 경계 테스트다. 새 WinForms GUI 및 실제 2대 PC LAN 사용은 **별도 수동 테스트가 필요**하다.
