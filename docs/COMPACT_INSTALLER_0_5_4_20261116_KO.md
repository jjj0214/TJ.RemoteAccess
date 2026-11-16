# TJ.RemoteAccess 0.5.4-preview - 단일 EXE / 공용 .NET 10 LTS 런타임

작성: 2026-11-16. 정식 배포가 아닌 사용자 최종 PC 수용 전 단계.

## 변경 사항

기존 0.5.3 Preview는 .NET 9 Self-contained Server/Viewer/Setup과 C# GUI Installer 각각에 런타임이 중복 포함됐다.

신규 0.5.4 Compact 패키지 구성:
- 하나의 네이티브 Inno Setup 6 설치 EXE. 사용자 PC에 .NET SDK, 별도 런타임, PowerShell 또는 네트워크 필요 없음
- Server/Viewer/Setup은 .NET 10 FDD(Framework-dependent) Win-x64
- Runtime은 Microsoft Windows Desktop Runtime 10.0.15 단 한 벌로, 설치 루트 Runtime 폴더에 위치
- 앱 호스트는 AppHostDotNetSearch=AppRelative 및 AppHostRelativeDotNet=../Runtime로 고정; 시스템에 설치된 .NET과 무관
- 선택 가능한 구성: Both / Server / Viewer. Setup과 Runtime은 공유 필수 구성
- GPL 소스의 동일 Git 커밋 snapshot(source.zip), LICENSE, THIRD_PARTY_NOTICES.md를 설치 EXE 안에 포함
- 구형 0.5.3 Preview GUI 설치기는 별도 호환용으로 유지

.NET 9 지원이 2026-11-10 종료되어 신규 Compact 설치 패키지는 .NET 10 LTS만 사용한다. 기존 Lab/M3.Service 테스트용 .NET 9 코드는 이번 패키지에 포함하지 않는다.

## 설치 방법

1. GitHub Actions TJ.RemoteAccess Compact Installer의 성공한 최신 커밋 아티팩트를 다운로드
2. ZIP 압축 해제 후 TJ.RemoteAccess-0.5.4-preview-Compact-Setup-win-x64.exe 실행
3. Server + Viewer / Server / Viewer 구성 선택 후 설치
4. 설치 경로: %LOCALAPPDATA%\Programs\TJ.RemoteAccess (현재 사용자)
5. 기존 Server 트레이는 X가 아니라 Exit TJ.RemoteAccess로 종료. Viewer와 Setup도 종료
6. 기존 인증서, TLS 서버 PIN, 비밀번호 검증자, Viewer 프로필은 %LOCALAPPDATA%\TJ.RemoteAccess에 그대로 유지. Setup init을 다시 실행하지 않는다
7. 제거: Windows 설정의 설치된 앱 또는 시작 메뉴의 Uninstall TJ.RemoteAccess 선택. 개인 설정은 삭제하지 않는다

Inno Setup의 Restart Manager와 사용자 모드 설치를 활용한다. 레지스트리 시스템 설정, 기존 TightVNC, Windows Service, 방화벽, Winlogon/UAC 정책은 변경하지 않는다.

## 런타임 출처 및 무결성

빌드 스크립트는 Microsoft .NET 10 공식 release-metadata/10.0/releases.json을 HTTPS로 읽고 정확한 10.0.15 WindowsDesktop win-x64 ZIP URL과 SHA-512를 확인한다. URI/버전/해시가 일치하지 않으면 패키지를 생성하지 않는다.

동봉하는 runtime-provenance.json에는 URL, SHA-512, 릴리즈 버전, 소스 Git 커밋, Runtime 원본 크기를 기록한다.

IL Trimming/ReadyToRun은 WinForms/Reflection/인증 호환성 검증 전까지 사용하지 않는다. 네이티브 설치기를 사용하므로 별도 Self-contained Installer 런타임은 필요하지 않다.

## 자동 검증과 사용자 최종 수용

- 로컬 .NET 9 SDK: 기존 Preview/Lab/Agent xUnit, PowerShell AST, Python 계약 검증
- GitHub Windows SDK10: Server/Viewer/Setup FDD 게시 및 .runtimeconfig/deps/apphost 확인
- SHA-512로 검증된 공용 Runtime 딱 한 벌 포함 확인
- DOTNET_ROOT가 잘못된 경로라도 Setup.exe service-host status가 공용 Runtime으로 실행되는지 시험
- Native Inno Setup EXE 무인 Viewer-only 설치, Both 업데이트, 설치 파일/런타임 확인, 제거 및 개인 설정 보존 시험
- Size report JSON과 Installer SHA-256을 산출물로 함께 제공
- 최종 CI 아티팩트에 GPL 해당 소스 `source.zip` 별도 동봉, 무인 설치·제거 실패 시 Inno 설치 로그도 진단 아티팩트로 보존
- 단일 설치 EXE의 실제 크기는 `compact-size-report.json`의 `bytes` 값으로 확인하며, 런타임 실제 설치 크기는 `runtimeBytes`로 구분
- 실제 WinForms 원격 GUI/3명 동시 접속/Win10~11 장비 테스트는 최종 사용자 수용으로 남김

| 최종 PC 시험 | 기대 |
|---|---|
| C01 설치 EXE 더블클릭 | 관리자 권한, PowerShell 또는 별도 .NET 설치 요구 없음 |
| C02 Server/Viewer/Both 선택 | 정확한 구성요소 설치 |
| C03 인터넷 끊은 상태 설치 | Runtime 포함으로 정상 실행 |
| C04 기존 Server 프로필 유지 | TLS 지문과 비밀번호 변경 없음 |
| C05 Viewer 접속 | TLS·비밀번호 6자·뷰어 최대 3명 정책 유지 |
| C06 Server 트레이 동작 | X는 트레이 숨김, Exit는 완전 종료 |
| C07 재설치 / 업데이트 | 개인 설정 유지 및 Runtime 공용 |
| C08 제거 후 재설치 | 실행 파일만 제거하고 개인 프로필 보존 |
| C09 원격 UAC/Winlogon | 기존과 같이 차단 (미지원) |

주의: Inno Setup은 기존 폴더 교체 스크립트의 완전한 다중 폴더 트랜잭션 롤백과 동일한 보장을 제공하지 않는다. 설치 실패 또는 전원 차단 시험은 테스트 PC에서 확인한다.

## 개발용 패키징

새 Compact 빌드는 .NET 10 SDK, Inno Setup 6, Microsoft 공식 런타임 ZIP 다운로드가 필요하다. 현재 개발 PC에는 .NET SDK 9.0.318만 설치되어 있어 .NET 10 게시와 네이티브 패키지의 실기 검증은 GitHub Actions Windows Runner에서 수행한다.

  powershell.exe -NoProfile -ExecutionPolicy Bypass -File .\tools\Build-TJRemoteAccess-CompactInstaller.ps1 -Version 0.5.4-preview -RuntimeVersion 10.0.15

이 명령은 빌드 전용이며 사용자 PC 설치에는 사용하지 않는다. 최종 설치 파일은 unsigned Preview로, 정식 MSI/서명/서비스 설치를 대체하지 않는다.
