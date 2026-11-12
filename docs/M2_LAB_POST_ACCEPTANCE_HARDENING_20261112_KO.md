# TJ.RemoteAccess Lab — 실기 검증 결과와 안정화 패치 (2026-11-12)

**후속 변경:** 아래 단일 Viewer 인증서 제한은 이 당시의 상태입니다.
개별 인증서·폐기·공유 캡처 개선은 docs/M2_LAB_MULTI_CLIENT_ACCESS_20261112_KO.md 및 docs/M2_LAB_SHARED_CAPTURE_PERFORMANCE_20261112_KO.md에 별도 기록했습니다.

## 1. 사용자 제공 실기 테스트: 8/8 PASS

2026-11-12 기존 feature/chatgpt-m2-engine-foundation 커밋 f06bd426 빌드로 다음 사항을 사용자 PC에서 확인했다.

1. 기존 서버 프로필 자동 복원 및 재접속
2. Add server 인증서 페어링 및 새 서버 정상 연결
3. 서버 A와 B 전환 시 화면/키보드/마우스 정상
4. Viewer 재시작 후 서버 프로필과 IP 유지
5. 동일 PFX 재등록 시 중복 프로필 미생성
6. 연결 종료 및 재연결 시 입력과 영상 정상
7. Controller 1명 + ViewOnly 2명, 4번째 접속 거부
8. 업데이트 뒤 기존 TightVNC 정상 공존

**판정:** 로그인 Windows 데스크톱 공유 기능의 주요 Lab 수용 시나리오 통과. 네이티브 GPL fork, Windows Service/UAC, 운영·보안 배포 인증은 포함하지 않는다.

## 2. 실기 검증 이후 추가 코드 보완

- Server: Hello/키보드/마우스/KeepAlive는 입력당 최대 32바이트로 제한. JPEG 최대 6MiB 한도를 작은 제어 입력에 재사용하지 않음.
- Server: 동시 핸드셰이크 슬롯은 TLS 및 프로토콜 Hello/3명 세션 등록 완료 시까지 유지.
- Server: Viewer 입력 또는 KeepAlive가 25초간 없으면 연결을 종료하고 점유 세션 회수. 영상 전송이 10초 이상 정체될 때도 세션 정리.
- Server: Stop 버튼 누르면 기존 세션 슬롯과 입력 상태 해제. Stop 이후 이전 세션 메시지를 최종 입력 경로에서 거부.
- Viewer: 25초 영상 무응답 시 수신 취소, 연결 정리.
- Viewer: UI가 늦어도 디코딩된 Bitmap이 무한정 쌓이지 않도록 최대 1개 화면 업데이트만 대기.
- Protocol: 송신 시 헤더/페이로드를 잠금 아래 분리 전송하고 수신 시 타입/페이로드를 직접 읽어 JPEG 이중 배열 복사를 제거. Version 1 wire format 유지, 동시 전송 직렬화 테스트 통과.
- 읽기 전용 PowerShell: 사용자 인증서 저장소에서 서버/Viewer별 개인키 존재와 잔여 유효일, 7일 이내 만료 경고 상태를 출력. 인증서/설정/서비스/방화벽 변경 없음.

## 3. 자동 검증

- Lab .NET 9 솔루션 Release: **PASS, 오류 0, 경고 0**
- xUnit TLS, 패킷 제한·취소·송신 순서 및 다중 프로필 회귀 테스트: **35/35 PASS**
- Python 코드/배포 계약 검사: **55/55 PASS**
- M2 네이티브 C++ Release x64 빌드: **PASS**
- Git diff --check: **PASS**

이번 테스트는 UI/입력/실제 LAN/3명 장기 성능과 PowerShell 설치 전체 실행을 대체하지 않는다. 사용자의 8/8 PASS는 이번 안정화 코드 적용 **이전** 버전 결과다.

## 4. 사용자가 새 커밋으로 재검증할 항목

양쪽 PC에서 Git Pull 후 Lab Server/Viewer/Setup을 종료하고 기존 Install-TJRemoteAccess-Lab.ps1 -Mode Server / -Mode Viewer로 바이너리를 갱신한다. 인증서나 프로필 파일을 삭제하거나 init을 재실행하지 않는다.

1. 서버 A/B 전환, 영상·마우스·키보드가 이전처럼 동작하는지
2. Controller 접속 상태에서 Server Stop→Start 후 이전 세션 입력이 차단되고 새 Viewer 연결이 되는지
3. Viewer 강제 종료 또는 네트워크 단절 시 약 25초 후 제어 세션 슬롯이 해제되는지(네트워크 조건별 오차 있음)
4. 창 최소화/복원 및 3개 Viewer 연결 시 메모리/화면/입력 정상인지
5. 다음 인증서 진단 결과에서 만료 정보가 확인되는지

       powershell -NoProfile -ExecutionPolicy Bypass -File .\tools\Test-TJRemoteAccess-Lab.ps1

진단의 ServerCertificateStatus, ViewerCertificateStatuses, CertificateAttentionCount 확인. 인증서 개인키, PFX 파일, 비밀번호를 공개 채팅에 첨부하지 않는다.

## 5. 아직 완료되지 않은 제품화 범위

- Windows Service와 현재 로그인한 사용자 GUI 세션 간 인증·권한·IPC ACL
- Winlogon, 잠금 화면, UAC 보안 데스크톱의 지원 설계 및 별도 검증
- Viewer별 인증서 발급·폐기 및 갱신. 현재 Server는 **단일 허용 Client 인증서 지문**을 사용하고 시험용 PFX는 30일간 유효
- 화면 전송 장기 부하, 3명 동시 성능, 고해상도/다중 모니터, 휠·단축키/한영키·클립보드
- TightVNC GPL fork guarded-v4 엔진과의 통합/서비스 공존, 서명 설치·정식 업데이트/롤백
- 고객·설비·외부 인터넷 배포 전에 필요한 보안 감사와 GPL 준수 검토

Lab은 소유한 격리 테스트 PC 용도로 유지하며, 이번 안정화로 운영 배포가 승인되는 것은 아니다.
