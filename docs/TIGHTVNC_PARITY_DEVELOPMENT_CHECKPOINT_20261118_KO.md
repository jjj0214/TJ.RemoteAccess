# TJ.RemoteAccess - TightVNC 기능 동등화 개발 및 검증 체크포인트

검증일: 2026-11-18
브랜치: `feature/chatgpt-tightvnc-parity`
기준: TightVNC 2.8.90 GPL 원본과 현재 TJRA 독립 TLS 기반 Viewer/Server. 동일한 RFB 프로토콜은 아님.

## 이번 브랜치에서 반영한 기능
- 기존 개발 중이던 Transfer Files 양측 탐색창, 개별 동의 파일·폴더 양방향 전송, 원격 파일 목록/생성/이름 변경/삭제, 수동 텍스트 클립보드 송수신을 보존.
- 송신 파일당 상한 64MiB -> 4GiB, 배치 총 512MiB -> 8GiB (128항목 제한 유지).
- 송신에 고정 48KiB 청크와 2차 SHA-256 검증 적용; 전송 사이 원본 변경 시 성공 완료 메시지를 차단.
- Local 드라이브 목록/파일 정렬, 전송 진행률 및 속도/ETA, 항목별 PASS/FAILED 이력, 개별 실패 후 다음 파일 시도 추가.
- 원격 Received 승인 범위 안에서 단일 파일 Copy/Paste 지원. Server에서 복사 매번 승인; 원자적 이름 확정, 덮어쓰기 금지, 취소·권한 회수 체크.
- Server -> Viewer 대용량 송신을 별도 작업으로 분리해 기본 입력 수신 루프 차단 완화; TLS 쓰기는 직렬화. 실패 시 세션 중단 및 부분 파일 삭제.
- Compact Installer CI가 해당 기능 브랜치 푸시도 빌드하도록 워크플로 대상 브랜치 추가.

## 이 단계에서 실제 실행한 검증
- Windows Release `TJ.RemoteAccess.sln`: PASS, 경고 0/오류 0.
- Preview xUnit: 62/62 PASS.
- Lab xUnit: 213/213 PASS (72MiB 전송, 원본 변경, Remote Copy 포함).
- Python unittest: PASS (기존 native fork artifact Skip의 해소는 확인되지 않음).
- Windows Release C++ `TJRemoteAccess.Core.Tests.vcxproj` / `TJRemoteAccess.EngineGate.Tests.vcxproj`: 양쪽 빌드 PASS. 실행 테스트 통과는 별도.
- 원본 TightVNC 서버·파일 전송·Viewer·설정 소스 실제 파일 경로 대조; 기존 기능 매핑 문서 참고.
- 실제 Windows WinForms 레이아웃, 2-PC 원격 조작·전송, .NET 10 Compact Installer 실 설치/업그레이드/제거 결과는 아직 PASS가 아님.

## 남은 기술 범위 (완료로 표시하면 안 됨)
- 일시정지/재개, 전송 중단 위치 이어받기, 개별 재시도 버튼, 드래그앤드롭, 원격 대용량 복사의 입력 지연 회귀, 모든 실패 시 재시도 정책.
- 사용자 승인 원격 Root 선택 및 권한 편집 UI, IP ACL/연결 요청 승인, 자동 클립보드 동기화 여부, 화면 변경 영역 delta·CopyRect, 추가 커서/코덱 옵션.
- 4GiB 실파일 장시간 테스트, 24시간 3 Viewer/1 Controller 부하·메모리 검증, DPI 100/125/150 및 Win10/Win11 사용자 UI, 설치 인스톨러 CI 성공 산출물 검증.
- Native TightVNC GPL fork RFB 실기 호환성 별도; 안전상 UAC·Winlogon 우회/평문 RFB 포트 자동 개방은 제외.
- Windows Service 패키지 실제 설치는 지시상 제외.

## 실제 PC 최종 검증 체크리스트
1. Win10/11 환경의 Compact Server/Viewer/Setup 선택 설치, 업그레이드, 제거, TLS 인증서/Viewer pin/기존 TightVNC 보존.
2. Viewer 프로필/PFX 및 opt-in password TLS 각각 연결·해제·재연결, 1/2/3 Viewer 정상/4번째 거부, Controller 하나만 허용.
3. Transfer Files 버튼 -> 로컬/원격 탐색, Server 최초 탐색 승인/거절, 0B/큰 파일/복수 파일/폴더 업·다운로드, 해시와 파일수 일치.
4. 파일이 열려 있음/쓰기권한 없음/디스크 부족/중복 이름/하위 폴더 링크/중간 인터넷 끊김/권한 회수 시 실패 이력 표시와 누락 감지.
5. 원격 파일 복사/이름 변경/삭제별 Server 승인, 승인 Received 외 경로 접근 차단, 전송 취소 시 부분파일 청소.
6. 4GiB 이내 대용량 파일 수신 중 화면 갱신·키보드·마우스 드래그 지연, 파일 전송 속도·ETA와 메모리/CPU 측정.
7. Win10/11 DPI 100%,125%,150% 화면, 작은 창에서 버튼 잘림/스크롤, 장시간 사용 후 Viewer/Server 정상 종료.
8. 설치 파일 SHA-256 및 GPL 대응 source.zip, unsigned 알림, TightVNC 병행 구동·설치 보존.

운영 PC 변경, Windows Service 배포, `main` 병합, 공개 Release 발행은 별도 승인이 필요합니다.
