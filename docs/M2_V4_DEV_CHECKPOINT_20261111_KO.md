# TJ.RemoteAccess M2 guarded-v4 개발 및 검증 결과

작성일: 2026-11-11
작업 환경: JJJ Dev Windows PC
작업 브랜치: feature/chatgpt-m2-engine-foundation
상태: **시험용 소스 및 컴파일 검증 완료 / 운영 배포 불가**

## 1. 원본·파생 소스 검증

- 원본: 공식 TightVNC 2.8.90 GPL ZIP. SHA-256 `f3f76393a5865e1f1919c78c3eb43b9c937cf738927c2bfc920ecbd3913b061c`
- 원본 1,264개 소스 파일 검증 PASS. 원본 디렉터리는 수정하지 않음
- 이전 파생본(v1, guarded-v2, guarded-v3) 보존
- 최종 시험용 파생본:
  `out/m2-fork/tightvnc-2.8.90-tjra-guarded-v4/upstream`
- 빌드 도구: VS2022 MSBuild, Release x64

## 2. 파생 소스 보안 차단

`tools/m2_fail_closed.py`, `tools/prepare_m2_fork.py`에 실제 적용한 9개 fail-closed 규칙:

1. 기존 Main RFB 리스너 생성 차단
2. 기존 HTTP 리스너 생성 차단
3. 기존 접근권한이 넓은 Control Pipe 생성 차단
4. 추가 RFB 리스너 reload 차단
5. 추가 RFB 리스너 startUp 차단
6. 기존 RFB 비밀번호 인증만으로 세션 승인 거부
7. 키보드 직접 OS 입력 전달 차단
8. 마우스 직접 OS 입력 전달 차단
9. 기존 RFB 파일 전송 핸들러 생성 차단

- Windows 객체 및 훅 식별자 분리: `TJ.RemoteAccess.HookLoader.exe`, `TJ.RemoteAccess.ScreenHooks32.dll`, `TJ.RemoteAccess.ScreenHooks64.dll` 참조/출력 일치
- 보안 감사 규칙을 기준으로 기존 파생본 17건에서 guarded-v4 3건으로 감소: `RAW_RFB_TCP_LISTENER`, `LEGACY_RFB_ADMISSION`, `SHARED_MIRROR_DRIVER_WINDOW`
- 주의: 위 3건은 보수적 정적 탐지이며, 그 밖에 보안 기능 완성 또는 운영 안전성 증명을 의미하지 않음
- 매니페스트: `mandatory_interlocks` 9건, `hook_artifact_isolation` 4건, `production_deployment_allowed=false`

## 3. 실제 VS2022 Release x64 빌드

| 대상 | 결과 | 오류 | 경고(호출별) |
|---|---|---:|---:|
| Server | PASS | 0 | 44 |
| Viewer | PASS | 0 | 110 |
| Control (.lib) | PASS | 0 | 0 |
| HookLoader | PASS | 0 | 1 |
| ScreenHooks64 DLL | PASS | 0 | 1 |

기존 TightVNC 코드의 C4819, C4091, C4996 등 경고가 남아 있으며 경고 집계는 프로젝트 의존성 빌드 포함 호출별 값임.

## 4. 바이너리 SHA-256 검증

| 산출물(x64/Release) | Bytes | SHA-256 |
|---|---:|---|
| TJ.RemoteAccess.Server.exe | 958464 | d095518ed5cab7b26e0fc52694d1c5f54fc47344ea7f9c94f415986349536243 |
| TJ.RemoteAccess.Viewer.exe | 1029632 | b1e058073c77975543aa318ba8d86a6d571d620320b0a430f928a810b950e88b |
| TJ.RemoteAccess.Control.lib | 17551122 | 5aec2b2d9817ac6ad1060f926a241a9c74ad12e9b7eeeb49dcd672b72b116fa2 |
| TJ.RemoteAccess.HookLoader.exe | 187392 | 288694914ad616b530968230b413d6396463e8b2a799fb0f86f1a3eb773c4a4c |
| TJ.RemoteAccess.ScreenHooks64.dll | 126464 | fe114f1acef32bae7882c893fcae6eec8189bdde4fd0a48280b95ea1c61ce445 |

Control은 실행 파일이 아닌 정적 라이브러리임.

## 5. 테스트 및 PowerShell 검증 범위

- `project_test` Python unittest: **41/41 PASS**, skipped=0
- Python 테스트는 실제 GPL 원본 SHA-256, 전체 원본 파일 검증, 원본 보존, guarded-v4 생성, 필수 규칙, 빌드 산출물 PE/COFF 형식 및 해시 차별성을 검증
- `project_build` 호출로 Windows C++ 프로젝트 5종 실제 Release x64 빌드 완료
- `project_analyze_log`로 Server/Viewer 컴파일 오류 0 확인
- `Build-M2Fork.ps1`: 경로를 guarded-v4로 갱신하고 보안 interlock 9개 및 Hook 이름 분리 4개의 매니페스트 재사용 조건을 강제
- **PowerShell 스크립트 자체는 JJJ Dev MCP에 임의 PowerShell 실행 도구가 없어 직접 실행하지 못했음.** Python 기반 정적 스크립트 계약 테스트는 통과했지만 PowerShell 런타임 동작을 직접 증명한 것은 아님
- 앞서 사용자가 직접 `Run-M2CoreTests.ps1` 실행 결과 PASS 제공. 이번 빌드의 native EXE 직접 실행은 별도 승인 조건에 따라 수행하지 않음
- 실제 Server/Viewer 원격 화면/입력, 서비스 공존, 레지스트리/IPC 접근 권한, 네트워크 리스너 실기 검증은 수행하지 않음

## 6. 후속 개발 게이트

- 신뢰된 mTLS 또는 동등한 클라이언트 인증/전송 계층과 임시 attach 토큰
- RFB 연결 수명주기와 EngineSessionGate 실제 연결, 신원 고정, disconnect key-release
- 최종 특권 입력 에이전트와 IPC ACL, 우회 입력 경로 추가 감리
- 미러 드라이버/윈도우 객체 및 Hook loader 독립 구동 검증
- 격리 Windows 테스트 PC에서 실제 설치/동시 실행/화면/키보드/마우스 및 로그온·UAC 검증
- GPL 배포 준비 및 PIE.FileTransfer 외부 연동 계약

**운영 설비, 고객사 PC, 외부망에 설치하지 말 것. 서비스 설치/시작, 방화벽 변경, 릴리즈 공개는 미실시.**
