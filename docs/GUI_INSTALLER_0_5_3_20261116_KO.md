# TJ.RemoteAccess 0.5.3-preview — 설치 창 및 6자 비밀번호 검증 (2026-11-16)

## 변경사항

- 비밀번호 최소 길이 **6자**, 최대 **128자**, UTF-8 최대 256바이트. 5자는 차단한다. 제어문자 불허. Server/Viewer/인증 패킷의 길이 정책 일치.
- TLS 1.2/1.3 유지, 첫 서버 SHA-256 확인, Viewer 비밀번호 저장 금지, IP별 5회/전체 20회 실패 제한, 기본 ViewOnly, Viewer 최대 3명/제어자 1명 유지. 6자는 상대적으로 짧으므로 **12자 이상 긴 비밀번호 사용을 권장**한다.
- **TJ.RemoteAccess.Installer.exe**: .NET 9 자체 포함, 일반 Windows 10/11 x64에서 더블클릭으로 시작하는 GUI 설치/업데이트/제거 도구. **MSI가 아니라 EXE 기반 설치 창**이다. 사용자 PC에 별도 PowerShell 실행이나 .NET SDK 설치가 필요 없다.
- 구성요소: Both(Server+Viewer+Setup), Server+Setup, Viewer+Setup. 현재 사용자 단위 `%LOCALAPPDATA%\Programs\TJ.RemoteAccess`만 설치·업데이트한다.
- 설치 시 설치본을 바로 덮어쓰지 않고 다음 순서로 수행한다.
  1. 실행 중인 TJRA 프로세스 확인, PID 표시(트레이 X 대신 Exit 필요).
  2. ZIP 전체 SHA-256을 **지정한 기대 해시**와 비교(진행률 표시).
  3. ZIP 경로 안전성/심볼릭 링크/중복/총 압축해제 상한/manifest 스키마 검증.
  4. manifest에 기록된 **모든 파일**의 SHA-256을 스트리밍 검증하면서 선택한 구성요소만 임시 스테이징.
  5. 기존 구성요소를 .backup-... 으로 이동 후 검증된 구성요소 교체. 도중 실패 시 이전 구성요소 복구.
  6. Start 메뉴 바로가기 생성. 개인 인증서/설정/비밀번호 변경하지 않음.
- 진행 상황 및 실패 원인은 화면과 `%LOCALAPPDATA%\TJ.RemoteAccess\InstallerLogs\Installer-YYYYMMDD-HHMMSS.log`에 기록한다. 취소 버튼은 검증/추출 및 업데이트 경계에서 정상 중단·복구하도록 설계했다.
- TightVNC, 이전 Lab, Windows Service, 레지스트리/방화벽/UAC 및 다른 사용자 계정은 변경하지 않는다.
- GUI 제거는 선택한 프로그램 파일과 전용 시작 메뉴 항목만 제거한다. TLS 인증서, Viewer 프로필, 서버 비밀번호는 의도적으로 남긴다. 자동 시작 작업을 등록했다면 Windows 작업 스케줄러에서 별도로 해제해야 한다.

## 실제 사용자 설치 순서 — PowerShell 불필요

새 코드가 GitHub Actions에서 성공적으로 패키징된 후 아래 위치의 빌드 아티팩트 다운로드:

- 저장소: [TJ.RemoteAccess GitHub Actions](https://github.com/jjj0214/TJ.RemoteAccess/actions/workflows/preview-installer.yml)
- Workflows: **TJ.RemoteAccess Preview GUI Installer**
- 아티팩트: `TJ.RemoteAccess-0.5.3-preview-GUI-Installer-win-x64`

1. 외부 다운로드 ZIP을 **완전히 압축 해제**한다. ZIP 안에서 EXE를 바로 실행하지 않는다.
2. 다음 파일이 같은 폴더에 있어야 한다.
   - `TJ.RemoteAccess.Installer.exe`
   - `TJ.RemoteAccess-0.5.3-preview-win-x64.zip`
   - `TJ.RemoteAccess-0.5.3-preview-win-x64.zip.sha256`
   - `LICENSE`, `README_INSTALL.txt`
3. 기존 `TJ.RemoteAccess.Server.exe`가 트레이에 있다면 **트레이 우클릭 → Exit TJ.RemoteAccess**. Viewer/Setup도 종료한다. 실행 중이면 설치 창이 PID를 안내하고 중단한다.
4. `TJ.RemoteAccess.Installer.exe` **더블클릭**. 기본 패키지가 자동 선택되며, 없으면 Browse로 ZIP을 선택한다.
5. SHA-256 값이 .sha256 파일에서 채워진다. 다른 경로로 공유된 정상 해시와 비교한다. *ZIP과 같은 폴더의 해시만으로 악성 패키지 신뢰를 보장하지는 않는다.*
6. 구성요소 Both / Server / Viewer 선택 → **Install / Update**. 진행률·진행 단계가 화면에 표시된다. 완료 창에서 로그 경로 확인.
7. Server PC만 쓰려면 Server+Setup, 원격조작 PC는 Viewer+Setup 선택. 기존 Server 프로필이 있다면 Setup init **재실행 금지**.
8. 제거는 같은 Installer 창의 **Remove programs**를 이용한다. 인증서/비밀번호/프로필은 남는다.
9. 기존 서비스 기반 설치 도구(별도 ServicePreview)와 혼동하지 않는다. 이 GUI 설치기는 **사용자 기반 Preview** 설치기다.

## 이전 Install-TJRemoteAccess-Preview-Offline.ps1이 반응 없었던 이유

기존 PowerShell 스크립트를 매개변수 없이 실행했다면 `PackagePath`와 `ExpectedPackageSha256`이 **필수 매개변수**이므로 입력을 요청하는 숨겨진 PowerShell 프롬프트에서 대기했을 가능성이 있다. 또한 인수를 올바르게 넣었다 해도 큰 ZIP에 대한 전체 SHA-256 계산 → 압축 전체 추출 → 파일별 SHA-256 재읽기를 수행하면서 상세 진행 상태를 출력하지 않았으므로 장시간 무응답으로 보였을 수 있다. 당시 실행 인수와 로그가 없어 실제로 어느 원인인지는 **확정할 수 없다**.

- 기존 PS 스크립트를 **인수 없이 실행해도 입력 대기하지 않고 즉시 사용법 오류**가 표시되도록 필수 매개변수 프롬프트를 없앴다.
- 해당 스크립트도 `[1/4] ZIP 해시`, `[2/4] manifest`, `[3/4] 스트리밍 검증/선택 추출(0~100%)`, `[4/4] 설치` 단계와 10% 로그를 표시하도록 변경했다. 기존 `ZipFile.ExtractToDirectory`로 전체 압축을 풀지 않게 변경했다.
- 이제 사용자 설치에는 스크립트가 아닌 **별도 GUI EXE**를 사용한다.
- 과거 Ctrl+Break로 중지했다면 `%LOCALAPPDATA%\Programs\TJ.RemoteAccess`에 `.stage-*` 폴더가 남아 있을 수 있다. `Server.backup-*` 등 백업 폴더까지 발견되면 **삭제하기 전에** 로그와 기존 설치 경로를 확인한다. 설치 중 실제로 어느 단계였는지 알 수 없어 자동 삭제하지 않는다.
- GUI 설치기의 진행 로그를 통해 어느 단계에서 지연되는지 추적할 수 있다. 취소 후에는 기존 설치본이 정상인지 확인한다.

## 자동 검증 vs 마지막 수동 확인

| 번호 | 시험 | 예상 |
|---|---|---|
| P01 | Server 비밀번호 5자 등록 | 거부 |
| P02 | Server 비밀번호 **6자** 등록 및 Viewer 접속 | 접속 가능, TLS/지문 검증 유지 |
| P03 | 배포 ZIP 압축 해제 후 GUI Installer 더블클릭 | Setup 창 표시, PowerShell 창 뜨지 않음 |
| P04 | Both / Server / Viewer 선택 후 Install | 선택한 구성요소만 설치, 진행률과 단계 표시 |
| P05 | SHA-256을 잘못 입력한 뒤 Install | 설치 전 거부, 기존 프로그램 유지 |
| P06 | Server 트레이에서 X만 누른 상태로 업데이트 | PID가 표시되고 종료 전 설치 거부 |
| P07 | 트레이 Exit 후 업데이트 | 정상 교체, 기존 비밀번호와 서버 인증서/프로필 유지 |
| P08 | 대형 ZIP 검증 중 취소 | 기존 설치 유지, 진행 로그에 취소 기록 |
| P09 | Viewer 서버 지문 변경/비밀번호 오류/동시 3명 | 기존 Fail-Closed/3명 제한 유지 |
| P10 | Remove programs | 바이너리만 제거, 인증서/설정 유지 |
| P11 | 기존 TightVNC 및 별도 Lab 실행 | 변경 없이 공존 |
| P12 | InstallerLogs에서 실패 단계 확인 | 각 단계와 예외 내용 기록 |

**자동 테스트가 실제 UI 실행·실제 ZIP 빌드·MSI 설치·서비스 재부팅 테스트를 대체하지는 않는다.** CI 패키지 성공 및 위 실기 수용 전에는 고객사에 설치하지 않는다. 이번 결과는 독립형 **사용자 세션 Preview**이고 Winlogon/UAC, 정식 서명 MSI 및 Windows Service 완전 대체는 별도 단계다.

## 0.5.3-preview ZIP 패키징 오류 수정 (2026-11-16)

이전 `Build-TJRemoteAccess-Preview-OfflinePackage.ps1` 사용 시 발생한

```text
Archive missing manifested file: Server/TJ.RemoteAccess.Server.exe
Offline application package build failed.
```

오류는 **매니페스트의 `/` 경로와 ZIP 내부의 파일 경로가 다를 수 있었던 패키징 문제**이다. 기존에는 Windows `Compress-Archive` 출력에 의존하면서 정확한 상대 경로를 강제하지 않았고, 이후 문자열 완전 일치 검사를 수행했다. 실패 당시의 ZIP이 보존되지 않았으므로 실제 압축 항목 표기는 단정하지 않는다.

수정에서는 `New-TJRemoteAccess-PreviewZip.ps1`을 이용해 `ZipArchive.CreateEntry`에 **`Server/TJ.RemoteAccess.Server.exe`처럼 정확한 `/` 파일명을 전달**하고, 검증 시 정확한 이름·파일 개수·크기를 확인한다. 누락되면 샘플 ZIP 경로를 오류에 포함한다.

자동 회귀 테스트는 Windows PowerShell 5.1에서 실제로 다음을 수행한다.

- 임시 Git 저장소 생성 및 테스트 Server/Viewer/Setup 실행 파일 생성
- `Build-TJRemoteAccess-Preview-OfflinePackage.ps1 -SkipBuild` 실행
- `git archive`, `source.zip`, `manifest.json`, 최종 오프라인 ZIP 및 `.sha256` 생성
- ZIP 파일 경로·내용·바이트 길이·개별 SHA-256·최종 ZIP SHA-256 검증

테스트는 **가짜 작은 EXE로 전체 패키징 흐름을 시험**하므로, 실제 self-contained Publish와 전체 크기의 사용자용 GUI 묶음은 GitHub Actions 성공 여부를 추가 확인해야 한다.

빌드 실패 후 출력 파일이 남았다면, 동일 버전의 기존 ZIP을 덮어쓰지 않는 정책 때문에 재실행이 거부될 수 있다. 이때 파일을 바로 삭제하기보다 실패 산출물인지 확인·백업한 뒤 다시 빌드한다.

개발 PC `D:\Repository\TJ.RemoteAccess`에서 실행할 경우 `main` 대신 아래 브랜치의 수정 커밋을 먼저 가져와야 한다.

```powershell
git fetch origin
git switch feature/chatgpt-m2-engine-foundation
git pull --ff-only origin feature/chatgpt-m2-engine-foundation
```

설치는 **GitHub Actions의 새로운 성공한 Run에서 GUI Installer 아티팩트**를 다운로드한 뒤 진행한다. 실패한 과거 Actions 아티팩트를 재사용하지 않는다.

## 개발자/CI 패키지 생성

테스트 완료 후 **깨끗한 Git HEAD**에서만:

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File .\tools\Build-TJRemoteAccess-Preview-GuiPackage.ps1 -Version 0.5.3-preview
```

이 명령은 **빌드 전용**이다. 사용자 PC에서는 실행하지 않는다. 출력:
`out\packages\TJ.RemoteAccess-0.5.3-preview-GUI-Installer-win-x64.zip` 및 `.sha256`.

GitHub Actions workflow는 해당 스크립트를 사용해 사용자용 설치 EXE 및 내부 오프라인 ZIP을 묶어 테스트 아티팩트로 업로드한다. 배포 패키지는 GPL `LICENSE`와 기존 `source.zip`을 포함한다.
