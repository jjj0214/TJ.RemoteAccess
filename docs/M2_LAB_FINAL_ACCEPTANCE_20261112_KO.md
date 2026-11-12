# TJ.RemoteAccess Lab — 두 PC 최종 수용 테스트 (2026-11-12)

**대상:** feature/chatgpt-m2-engine-foundation / Windows 10~11 x64 테스트 PC.
**범위:** 독립 .NET 9 LAB 원격제어. TightVNC GPL 파생 Server/Viewer를 교체한 운영판이 아니다.
**안전:** 로그인된 일반 데스크톱에서만 실행한다. 서비스/UAC/잠금·로그온 화면/인터넷 공개/고객·설비 운영은 미지원이다.

## A. 각 PC의 최신 코드·프로그램 업데이트

기존 저장소에서:

    git fetch origin
    git switch feature/chatgpt-m2-engine-foundation
    git pull --ff-only origin feature/chatgpt-m2-engine-foundation

실행 중인 LAB Server/Viewer/Setup을 종료한 뒤, 서버 PC에는:

    powershell -NoProfile -ExecutionPolicy Bypass -File .\tools\Install-TJRemoteAccess-Lab.ps1 -Mode Server

Viewer PC에는:

    powershell -NoProfile -ExecutionPolicy Bypass -File .\tools\Install-TJRemoteAccess-Lab.ps1 -Mode Viewer

한 PC에서 Server+Viewer를 둘 다 시험하면 -Mode Both. 설치기는 모든 실행 파일을 별도 스테이징 폴더에 빌드한 뒤 바꾸며 업데이트 실패 시 이전 실행 파일 폴더를 복원한다. 인증서/프로필은 설치 폴더 바깥이므로 유지된다.

## B. 서버 인증서: 기존 Server에서는 init 재실행하지 않기

서버 PC의 server.json이 없을 때만 최초 1회:

    & "$env:LOCALAPPDATA\Programs\TJ.RemoteAccess.Lab\Setup\TJ.RemoteAccess.Lab.Setup.exe" init

이미 정상 연결되던 Server에서는 다시 init하지 않는다. 서버의 기존 인증서와 승인된 클라이언트 인증서 지문을 그대로 사용한다.

새 서버 PC의 다음 두 파일을 보안 수단으로 Viewer PC의 같은 폴더에 복사한다:

    %LOCALAPPDATA%\TJ.RemoteAccess.Lab\Pairing\viewer.json
    %LOCALAPPDATA%\TJ.RemoteAccess.Lab\Pairing\client-transfer.pfx

서버 개인키는 전송하지 않는다. 서버 최초 설정 시 표시된 PFX 비밀번호가 필요하다.

## C. Viewer에서 새 Server 등록 (기존 설정 자동 보존)

Viewer를 실행하면 이전 단일 서버 설정인 viewer.json은 삭제 없이 "Previous server"로 자동 이전된다.
이전에 오류 대응 과정에서 생성한 viewer.json.backup-* 파일들도 최초 실행 시 검사하여
유효한 과거 서버 설정을 "Recovered server" 프로필로 추가한다. 원본 백업은 변경하지 않는다.

Add server 버튼 → 서버에서 복사한 client-transfer.pfx 선택 → 프로필 이름, 서버 IP, PFX 암호 입력 → 등록. 다른 이전 서버와 인증서는 그대로 남는다.

Viewer의 서버 선택 드롭다운에서 접속 대상 변경. Edit로 서버 이름/IP/포트 수정, Identity로 고정된 TLS SHA-256 인증서 지문 확인, Remove로 프로필만 삭제. 기존 클라이언트 인증서나 viewer.json은 삭제하지 않는다.

CLI로도 여러 서버를 추가할 수 있다:

    & "$env:LOCALAPPDATA\Programs\TJ.RemoteAccess.Lab\Setup\TJ.RemoteAccess.Lab.Setup.exe" import-client "C:\Temp\TJRA-Pairing\client-transfer.pfx" "테스트 서버 2" "192.168.0.100"

이전 버전에서 발생했던 Viewer configuration already exists 오류 없이 프로필을 추가한다. Viewer가 실행 중이면 Refresh 버튼으로 새 목록을 다시 읽는다. 기존 Client 인증서와 서버 지문은 덮어쓰지 않는다.

저장된 목록 확인:

    & "$env:LOCALAPPDATA\Programs\TJ.RemoteAccess.Lab\Setup\TJ.RemoteAccess.Lab.Setup.exe" list-profiles

사용이 끝난 임시 PFX 복사본과 비밀번호 기록은 보안상 안전하게 정리한다.

## D. LAN 접속

서버 PC 설정 경로:

    %LOCALAPPDATA%\TJ.RemoteAccess.Lab\server.json

ListenAddress를 실제 서버 LAN IP로 설정. 인증서 지문 값들은 수정하지 않는다. 그 뒤:

    & "$env:LOCALAPPDATA\Programs\TJ.RemoteAccess.Lab\Server\TJ.RemoteAccess.Lab.Server.exe" --allow-lan

Server UI Start 클릭. Viewer에서 새 서버를 선택해 해당 IP:15444로 연결한다. 방화벽이 막으면 테스트 환경에서 Viewer PC IP에 한정해 TCP 15444를 수동 허용한다. 설치기는 방화벽을 변경하지 않는다.

Viewer GUI Host/Port를 바꾸고 Connect하면 선택된 서버의 주소만 저장되며 인증서 지문은 바뀌지 않는다.

## E. 최종 체크리스트

- 기존 Server 프로필 자동 이전 및 기존 서버 재접속
- Add server → 새 서버 등록, 드롭다운으로 Server A/B 전환
- 두 서버 각각 TLS 인증과 화면 표시·마우스/키보드 입력 정상
- Edit로 IP/포트 변경 후 Viewer 재실행 시 변경사항 유지
- 동일 PFX 재가져오기 시 중복 프로필 미생성
- Identity에서 서로 다른 Server 지문 확인; 잘못된 PFX/설정 조합은 거부
- Remove 시 선택된 프로필만 삭제되고 다른 프로필/인증서 유지
- 연결 종료→다른 서버 접속→다시 원서버 연결 반복 시 화면/입력 안정
- Controller 1명 + ViewOnly 2명 접속, 네 번째 거부
- Viewer 업데이트 재설치 후 프로필과 TLS 설정 유지, 기존 TightVNC 정상

각 PC에서 읽기 전용 진단:

    powershell -NoProfile -ExecutionPolicy Bypass -File .\tools\Test-TJRemoteAccess-Lab.ps1

출력의 SavedServerCount, ProfileStatus, SelectedServerName, Tcp15444Listeners 등을 확인한다. 오류 시 명령 출력과 GUI 오류 메시지만 공유한다. PFX 개인키 파일이나 비밀번호는 채팅에 업로드하지 않는다.

## F. 제한과 되돌리기

Lab 제거는 Uninstall-TJRemoteAccess-Lab.ps1이며 프로그램/바로가기만 제거하고 인증서/프로필은 유지한다. 실제 Windows 서비스, UAC, 잠금화면, 자동 업데이트, 인증서 재발급·폐기, 정식 설치 서명은 미완성이다.

자동 빌드·TLS/프로필 테스트 통과만으로 GUI 원격제어까지 재검증되었다고 판단해서는 안 된다. 두 PC에서 실제 수용 확인 후 후속 결함을 수정한다.
