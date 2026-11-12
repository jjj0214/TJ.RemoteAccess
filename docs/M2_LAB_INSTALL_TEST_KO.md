# TJ.RemoteAccess M2 Lab - Windows 빌드/설치/원격제어 시험 안내

**상태:** 독립 .NET 9 LAB 원격제어 구현 (운영 배포 제품 아님).
**환경:** Windows 10/11 x64, VS2022/.NET 9 SDK, 로그인된 사용자 데스크톱.
**중요:** TightVNC GPL 파생 엔진 guarded-v4의 fail-closed 차단은 그대로 유지. Lab Server/Viewer는 별도의 시험용 .NET 엔진이다. Windows Service, UAC, 잠금/로그온 화면, 자동 실행, 파일 전송, 클립보드, 270 PPM 성능 요구는 포함하지 않는다.

## 1. 동일한 테스트 PC에서 설치 및 원격제어

일반 PowerShell:

    cd D:\Repository\TJ.RemoteAccess-ChatGPT-M2-Engine
    powershell -NoProfile -ExecutionPolicy Bypass -File .\tools\Install-TJRemoteAccess-Lab.ps1 -Mode Both

스크립트는 Server, Viewer, Setup을 self-contained win-x64 단일 실행 파일로 publish하고 사용자 시작 메뉴 바로가기를 생성한다. 성공 메시지는 PUBLISH PASS 3건, CURRENT-USER INSTALL PASS. 3개 EXE는 %LOCALAPPDATA%\Programs\TJ.RemoteAccess.Lab\{Server,Viewer,Setup} 아래에 배치된다.

초기 서버/클라이언트 인증서 페어링(최초 1회):

    & "$env:LOCALAPPDATA\Programs\TJ.RemoteAccess.Lab\Setup\TJ.RemoteAccess.Lab.Setup.exe" init

출력 PFX 비밀번호는 다른 테스트 PC에 클라이언트 인증서를 전달할 때 쓰며 별도로 저장되지 않는다. Pairing 폴더 및 client-transfer.pfx는 민감한 비밀 자료다. 인증서 유효기간은 30일이며 기존 설정을 자동으로 덮어쓰지 않는다.

Windows 시작 메뉴에서 TJ.RemoteAccess Lab Server 실행 후 Start (interactive session) 클릭. 기본 바인딩은 127.0.0.1:15444. TJ.RemoteAccess Lab Viewer를 실행하여 Host 127.0.0.1, Port 15444로 Connect 클릭. 첫 뷰어는 Controller, 추가 2명은 ViewOnly다. 4번째는 거부된다. Viewer를 종료하면 서버는 눌린 키·마우스 상태를 해제한다. 기존 보기 전용 세션은 제어자가 종료된 후 다시 연결해야 새로운 Controller가 될 수 있다.

## 2. 서로 다른 두 PC의 LAN 시험

서버 PC에서 위 설치/초기화 후 다음 두 파일을 보안 수단을 사용해 Viewer PC에 옮긴다.

    %LOCALAPPDATA%\TJ.RemoteAccess.Lab\Pairing\viewer.json
    %LOCALAPPDATA%\TJ.RemoteAccess.Lab\Pairing\client-transfer.pfx

서버 개인키는 복사하지 않는다. Viewer PC에서도 같은 저장소를 준비하고:

    powershell -NoProfile -ExecutionPolicy Bypass -File .\tools\Install-TJRemoteAccess-Lab.ps1 -Mode Viewer
    & "$env:LOCALAPPDATA\Programs\TJ.RemoteAccess.Lab\Setup\TJ.RemoteAccess.Lab.Setup.exe" import-client "C:\Temp\TJRA-Pairing\client-transfer.pfx"

import-client 실행 시 48자리 PFX 암호를 입력한다. viewer.json은 PFX와 같은 폴더에 있어야 한다. 성공 후 임시 PFX와 암호 복사본을 제거한다.

서버 PC의 %LOCALAPPDATA%\TJ.RemoteAccess.Lab\server.json 내 ListenAddress를 실제 서버 테스트 LAN IP로 변경한 후 명시적으로 실행:

    & "$env:LOCALAPPDATA\Programs\TJ.RemoteAccess.Lab\Server\TJ.RemoteAccess.Lab.Server.exe" --allow-lan

서버 창에서 Start 클릭. 서버 IP로 바인딩되며 인증되지 않은 평문 RFB 접속은 제공하지 않는다. 서버 PC 방화벽이 차단한다면 테스트 네트워크에서 Viewer IP에 한정된 TCP15444 규칙을 **별도로 승인 후 수동** 허용한다. Installer는 방화벽을 변경하지 않는다.

Viewer PC의 %LOCALAPPDATA%\TJ.RemoteAccess.Lab\viewer.json 내 Host 또는 Viewer UI Host 입력에 서버의 LAN IP를 입력하고 Connect. 연결은 클라이언트/서버 인증서 SHA-256 고정 검사에 성공해야만 성립한다.

## 3. PS 읽기 전용 점검

    powershell -NoProfile -ExecutionPolicy Bypass -File .\tools\Test-TJRemoteAccess-Lab.ps1

설정·파일·TCP15444 수신 상태만 보고한다. 서비스 설치나 실제 GUI 제어는 수행하지 않으므로 이 결과만으로 기능 성공을 판정하지 않는다.

원본 TightVNC 공존은 기존 Inspect-M2Coexistence.ps1로 별도 확인한다.

## 4. 실제 테스트 체크

- Server에서 Start 후 127.0.0.1:15444 LISTEN 확인.
- Viewer Connect -> Connected (Controller), JPEG 화면 수신 및 프레임 카운트 증가.
- Viewer 내 마우스 이동/클릭 및 일반 키보드 입력 정상.
- ViewOnly 뷰어 2개에서 화면은 보이되 입력은 무시됨.
- 4번째 뷰어 접속 거절.
- 다른 클라이언트 인증서 또는 바뀐 서버 인증서로 접속 불가.
- Controller 종료 시 서버 입력 상태 해제 및 기존 TightVNC 프로세스/설정 영향 없음.

잠금 화면, UAC 보안 데스크톱, GPU 고부하, 다중 모니터, 키보드 조합/한영 변환, 마우스 휠, 무인 서비스 및 장기 유지보수는 미검증 또는 미지원이다. 테스트 PC의 일반 데스크톱에서만 시험하고 고객/설비/외부 인터넷에 배포하지 않는다.

## 5. 사용자별 Lab 프로그램 제거

    powershell -NoProfile -ExecutionPolicy Bypass -File .\tools\Uninstall-TJRemoteAccess-Lab.ps1

Lab 설치 폴더·시작 메뉴만 제거한다. 기존 TightVNC, Windows 서비스, 방화벽은 변경하지 않는다. CurrentUser 인증서 저장소 및 Pairing 파일은 별도 수동 검토/정리 대상이다.

## 6. 개발 검증의 의미

- Lab Server/Viewer/Setup은 .NET 9 Release x64 빌드 성공.
- xUnit: 프레임 길이·입력 유효성·인증서/역할 검증·TLS 상호 인증/비허용 클라이언트 거부를 JJJ Dev Windows 루프백에서 실행한 결과 PASS.
- GUI의 실제 화면·키보드·마우스·사용자별 설치 스크립트 전체 런타임은 JJJ Dev의 제한된 도구로 직접 실행하지 못했다. 사용자의 격리 테스트 PC 실기 검증이 필요하다.
- 운영판의 Windows Service/UAC, mTLS PKI 갱신, 특권 최종 입력 에이전트, GPL 파생 엔진과의 통합, IPC ACL, 릴리즈 서명 및 장기 성능 검증은 완료되지 않았다.

**LAB 시험 가능성 점검 문서일 뿐, 운영·보안 인증이나 출시 승인이 아니다.**
