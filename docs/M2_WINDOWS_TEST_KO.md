# TJ.RemoteAccess M2 Windows 빌드 및 시험 안내 (2026-11-06)

현재 위치: D:\Repository\TJ.RemoteAccess-ChatGPT-M2-Engine
브랜치: feature/chatgpt-m2-engine-foundation

**주의:** 아래 Fork는 TightVNC 2.8.90의 공개 GPL 소스를 복제해
문자열 기반의 초기 이름 분리를 적용한 시험용 빌드입니다.
Windows 서비스, Named Pipe, Shared Memory, Hook DLL, 설치/제거 및
RFB 보안 경계는 아직 실기에서 확인되지 않았습니다.
운영 설비, 현장 생산 PC 및 외부망에 설치/실행하지 마세요.

## 1. 가장 먼저 실행: M1/M2 Windows 테스트

Visual Studio 2022 / MSVC C++ / Windows 10(11) SDK가 필요합니다.
Visual Studio에서 tests\TJRA.M2.Tests.sln 을 열고 Release/x64 빌드를
하거나 개발자 PowerShell에서 아래 명령을 실행합니다.

    cd D:\Repository\TJ.RemoteAccess-ChatGPT-M2-Engine
    msbuild tests\TJRA.M2.Tests.sln /p:Configuration=Release /p:Platform=x64

빌드가 완료되면 실행 검증까지 수행하세요.

    powershell -NoProfile -ExecutionPolicy Bypass -File .\tools\Run-M2CoreTests.ps1

이 단계는 네트워크나 VNC 서비스를 변경하지 않습니다.
두 테스트 EXE의 PASS 출력 및 Python unittest PASS 출력을 확인하세요.

## 2. TightVNC 파생 Server/Viewer 소스 생성 및 Release x64 빌드

JJJ Dev에 있는 공식 2.8.90 GPL ZIP(검증된 SHA-256)을 사용합니다.
회사 전용 소스는 읽거나 복사하지 않습니다. 확인된 공개 ZIP을
입력 데이터로 사용할 뿐 PIE.FileTransfer 저장소를 수정하지 않습니다.

    powershell -NoProfile -ExecutionPolicy Bypass -File .\tools\Build-M2Fork.ps1 -RetargetV143

이 명령이 수행하는 작업:

1. 공식 GPL ZIP SHA-256과 압축 해제 파일별 내용을 검증합니다.
2. 원본 TightVNC는 third_party/tightvnc-2.8.90-gpl에 보존합니다.
3. CMake/CTest가 없으면 VS2022 MSBuild로 테스트 EXE를 빌드 및 실행하고 Python 테스트를 진행합니다.
4. Windows SDK 8.1이 없으면 원본 Server/Viewer 빌드를 BLOCKED_TOOLCHAIN으로 기록합니다.
   Windows SDK 8.1이 설치된 경우 원본 빌드는 격리된 출력 복사본에서 시도합니다.
   현재 개발 PC의 원본 빌드는 MSB8036 오류가 확인됐습니다.
5. 별도 out/m2-fork/tightvnc-2.8.90-tjra 폴더에 파생 시험 소스를 생성합니다.
6. 파생 소스에서 실행물 이름, 일부 서비스/설정/IPC 리터럴을 분리하고
   v143 빌드 도구체인을 적용해 Server, Viewer, Control EXE 빌드를 시도합니다.
7. 성공 여부와 남은 이름 충돌을 로그/JSON으로 기록합니다.

결과 확인 파일:

    out\m2-evidence\M2-build-results.json
    out\m2-evidence\Original-Server.log
    out\m2-evidence\Original-Viewer.log
    (원본 로그는 Windows SDK 8.1이 설치되어 빌드를 시도한 경우에만 생성됩니다.)
    out\m2-evidence\TJ-Server.log
    out\m2-evidence\TJ-Viewer.log
    out\m2-evidence\TJ-Control.log
    out\m2-fork\tightvnc-2.8.90-tjra\TJRA_FORK_MANIFEST.json

실행파일이 만들어졌다고 두 서버 병행 설치나 보안을 합격 처리하면 안 됩니다.
원본 트리와 Fork 트리의 빌드 결과가 분리되어 있으므로 오류가 발생하면
위 로그를 전달하여 실제 원인에 맞게 원본 호환성/파생 기능을 각각 수정합니다.
Fork가 이미 존재하면 덮어쓰지 않고 중지합니다.

## 3. Viewer 화면 표시 확인 (단일 PC 루프백)

시험 PC에서 기존 TightVNC가 localhost:5900으로 수신 중이며
파생 Viewer EXE 빌드가 성공했을 때만 아래 명령을 실행합니다.

    powershell -NoProfile -ExecutionPolicy Bypass -File .\tools\Launch-M2ViewerLoopback.ps1

이 명령은 새 VNC Server를 구동하지 않으며 기존 TightVNC 서비스 설정을
변경하지 않습니다. TJ.RemoteAccess.Viewer.exe가 127.0.0.1:5900으로
접속하도록 실행합니다. 사용 중인 TightVNC 비밀번호가 요구됩니다.
화면이 나타나고 정상 입력 및 연결 종료가 되는지 직접 확인하세요.
명령 실행 후 창이 열렸다는 사실만으로 렌더링 PASS로 기록하지 마세요.

## 4. 파생 Server 독립화 검증 (별도 승인된 격리 Windows PC)

이전 검증이 PASS여도 이 단계는 자동 수행되지 않습니다.
원본과 파생 코드는 내부 객체/레지스트리/프로세스 이름을 더 확인해야 합니다.
다음 항목이 모두 충족될 때에만 수동 시험을 시작하세요.

- 원본과 파생 Server+Viewer x64 빌드가 PASS일 것
- Fork manifest의 충돌 후보를 감사하고 필요한 수정이 끝날 것
- NIC 차단 또는 동일 수준으로 격리한 Windows 테스트 VM일 것
- 파생 서버가 독립 설정, 고유 RFB 포트, 루프백 전용 수신을 사용하도록
  소스/로그로 확인할 것
- 기본 서비스명 tvnserver와 신규 TJ.RemoteAccess.Server가
  같은 것을 가리키지 않을 것
- 원본 설치/제거 및 운영 구성에 영향을 주지 않을 것

위 검증 전에 -install, -remove, sc stop, net stop 또는 MSI 실행은
하지 마세요. 현재 빌드는 서비스 독립화 완료품이 아닙니다.
읽기 전용 환경 스냅샷은 아래 명령으로 얻을 수 있습니다.

    powershell -NoProfile -ExecutionPolicy Bypass -File .\tools\Inspect-M2Coexistence.ps1

Windows 로그인/잠금/UAC, 일반 1명, 유지보수 3명 및 4번째 거부의
실제 동작 검증은 별도의 물리/가상 시험 PC에서 실시해야 합니다.

## 현재 확인된 결과

- JJJ Dev MSBuild Release x64: 두 M1/M2 C++ 테스트 실행파일 컴파일 PASS
- 테스트 EXE 실제 실행: NotTested (현재 JJJ Dev 도구 미지원)
- ZIP SHA-256: PASS
- 원본/파생 TightVNC Server 및 Viewer 실행 바이너리: NotTested
- 파생 Viewer 화면/입력, 동시 실행, Windows Secure Desktop: NotTested
- mTLS Gateway/특권 입력 프로세스/IPC ACL: 구현 전
- 외부 공개 Push/Release 및 PIE.FileTransfer 소스 변경: 미진행

테스트 결과를 전달할 때는 명령의 전체 출력과 M2-build-results.json,
TJ-Server.log, TJ-Viewer.log를 함께 제공해주세요.
