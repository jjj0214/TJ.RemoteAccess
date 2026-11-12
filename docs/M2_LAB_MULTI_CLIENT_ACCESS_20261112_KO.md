# TJ.RemoteAccess Lab — 개별 Viewer 인증·차단 및 권한 관리 (2026-11-12)

## 범위

- 사용자 로그인 상태에서만 실행되는 독립 .NET 9 LAB.
- 서버별/Viewer별 고유 인증서, 허용 목록, 보기 전용/제어 가능 권한.
- 기존 Server·Viewer의 인증서와 server.json, viewer.json, viewer-profiles.json 설정을 보존한다.
- 현재 포트 15444, TLS 1.2/1.3, 원본 TightVNC와 공존을 유지한다.
- 서비스 자동 실행, 잠금/로그온 화면, UAC, 인터넷 공개용 제품이 아니다.

## 1. 최신 소스 및 설치

Server와 Viewer PC 각각 기존 Git 작업 폴더에서:

    git fetch origin
    git switch feature/chatgpt-m2-engine-foundation
    git pull --ff-only origin feature/chatgpt-m2-engine-foundation

서버/Viewer/Setup 실행 파일을 종료하고 Server PC에서는:

    powershell -NoProfile -ExecutionPolicy Bypass -File .\tools\Install-TJRemoteAccess-Lab.ps1 -Mode Server

Viewer PC에서는:

    powershell -NoProfile -ExecutionPolicy Bypass -File .\tools\Install-TJRemoteAccess-Lab.ps1 -Mode Viewer

**기존에 정상 접속했다면 Setup init을 다시 실행하지 않는다.**
프로필 및 CurrentUser 인증서는 설치 폴더 밖에서 유지된다.

## 2. Server PC에서 Viewer별 새 인증서 발급

Server PC PowerShell에서:

    $setup = "$env:LOCALAPPDATA\Programs\TJ.RemoteAccess.Lab\Setup\TJ.RemoteAccess.Lab.Setup.exe"
    & $setup list-clients
    & $setup pair-client "Test-Viewer-2"

보기 전용 Viewer를 발급하려면:

    & $setup pair-client "Observer-Viewer-3" --view-only

- 실행 결과의 Client ID를 기록한다.
- 출력되는 Viewer별 새로운 Pairing 폴더에는 다음 세 파일이 있다:
  * client-transfer.pfx — 암호화된 Viewer 개인키. 암호는 콘솔에 최초 1회만 표시
  * viewer.json — 인증서 지문 및 서버 인증서 고정값
  * server-public.cer — 서버 공개 인증서
- 이 세 파일을 Viewer PC의 같은 폴더에 **안전하게 복사**한다.
- PFX 암호는 파일과 별도 경로로 전달한다.
- Viewer의 Add server에서 client-transfer.pfx를 가져오고 서버 IP로 접속한다.
- 새 인증서는 Server PC의 Windows 인증서 저장소에 설치하지 않고, 임시 암호화 PFX로만 생성한다. 각 Pairing 폴더는 각각 고유한 인증서를 사용한다.
- Viewer에서 정상 연결 확인 후 임시 PFX 사본을 수동으로 안전하게 삭제한다. 프로그램은 자동 삭제하지 않는다.

**주의:** 기존 Pairing 폴더의 동일한 PFX를 여러 Viewer에서 공유하면 접속 권한을 Viewer별로 차단할 수 없다. 기존 Legacy 인증서는 계속 작동하지만, 개별 관리가 필요한 Viewer부터 위 명령으로 별도 페어링해야 한다.

## 3. 권한 조회·변경·차단

Server 창의 **Client access** 버튼에서 Client 목록을 확인하고 다음 버튼을 사용할 수 있다:

- Disable — 해당 인증서만 차단
- Enable — 차단된 인증서를 수동으로 다시 허용
- View only — 그 Viewer는 원격 입력 제어 불가
- Allow control — 제어 권한 부여
- Refresh — 외부에서 명령행으로 변경한 권한을 다시 표시

동일한 동작을 콘솔에서도 실행할 수 있다:

    & $setup list-clients
    & $setup disable-client "<전체 Client ID>"
    & $setup enable-client "<전체 Client ID>"
    & $setup client-control "<전체 Client ID>" view-only
    & $setup client-control "<전체 Client ID>" allow

Server는 TLS 인증, Hello 프로토콜 승인, 각 입력, 화면 전송에서 권한을 검사한다. Server가 실행 중인 상태에서 특정 인증서를 차단하거나 제어 권한을 내리면 해당 Viewer 연결을 종료하고 키보드·마우스 입력 상태를 해제한다. 권한 조회는 최대 약 500ms 동안 캐시되므로 차단은 즉각적인 원자 연산이 아니라 짧은 시간 내 적용된다. 네트워크 정체나 Windows 디스플레이 캡처 지연 시 실제 연결 종료는 더 늦을 수 있다.

보기 전용으로 처음 접속한 Viewer는 Controller 자리가 비어 있어도 입력 제어를 할 수 없다. 제어 권한 변경 후 이미 연결된 Viewer의 역할을 자동 승격시키지 않으며 재접속이 필요하다.

## 4. 기존 설치 안전 마이그레이션

서버 최초 실행 시 server.json의 AllowedClientSha256 값이 server-clients.json의 "Legacy paired Viewer" 항목으로 이전된다.

- 기존 서버 인증서 지문 및 접속 허용 상태 유지
- 기존 PFX 파일 및 Viewer 프로필 유지
- 기존 3인 접속 제한 유지
- 마이그레이션 표식 server-clients.migrated가 생성된다.
- 이후 server-clients.json이 삭제되거나 손상되어도 기존 AllowedClientSha256으로 조용히 복구하지 않는다. **접속 차단(Fail Closed)** 후 운영자 확인 필요
- server-clients.json.bak는 수동 복구 참고 자료이며 자동으로 신뢰되지 않는다.

현재 인증서가 30일 유효하며 자동 갱신은 아직 제공되지 않는다. 만료 전 새 Viewer PFX를 발급·등록·접속 확인하고 이후 오래된 Client ID를 Disable하는 방식을 사용한다. **서버 인증서 교체**는 모든 Viewer에서 서버 공개키 지문 재확인이 필요하므로 별도 안전한 절차로 개발해야 한다.

## 5. 읽기 전용 진단

Server 및 Viewer PC에서:

    powershell -NoProfile -ExecutionPolicy Bypass -File .\tools\Test-TJRemoteAccess-Lab.ps1

출력 확인:
- ServerClientRosterStatus = Loaded
- ServerAllowedClientCount
- ServerDisabledClientCount
- ServerViewOnlyClientCount
- ServerCertificateStatus / ViewerCertificateStatuses
- CertificateAttentionCount

스크립트는 개인키/PFX를 출력하거나 서비스·방화벽을 변경하지 않는다. 이 스크립트의 PowerShell 실 실행은 사용자 PC에서 검증 필요하다.

## 6. 사용자 수용 테스트 제안

1. 기존 Viewer는 재등록 없이 기존 프로필에서 화면과 입력 정상
2. Viewer B에 신규 PFX 발급·등록 후 다른 인증서 지문으로 접속
3. Viewer C에 --view-only 발급·등록 후 화면만 보이고 입력 차단
4. Server Client access에서 Viewer B Disable → B 접속 끊김
5. Viewer A와 C의 연결은 계속 유지
6. Viewer B 재접속이 실패하고 Enable 후 재접속이 성공
7. Controller를 View only로 변경하면 입력 해제 및 세션 종료
8. 3인 동시 접속 및 4번째 접속 거절, TightVNC 공존 유지
9. 서버 재실행 후 Client 목록과 권한 유지
10. Test-TJRemoteAccess-Lab.ps1의 Client 카운트/만료 상태 확인

## 7. 자동 검증 경계

- JJJ Dev .NET 9 Release 빌드·xUnit 실제 Windows loopback TLS 인증/거부·프로필 회귀 테스트는 자동 실행.
- GUI Client access 조작, Setup pair-client 전체 CLI 실행, Server-Viewer LAN 조작, PFX 복사/삭제 및 설치 롤백은 자동 실행하지 않음.
- 기존에 사용자가 제공한 8/8 PASS는 이 개별 Viewer 인증 기능 적용 **이전 빌드**의 결과다. 새 기능은 사용자 수용 테스트가 필요하다.
