# TJ.RemoteAccess 4개 기능 개발 및 검증 결과 (2026-11-18)

## 대상과 경계
- 기능 기준 커밋: `fad7160ee53c3d862292cf85307fd22d6552c97e` (`feature/chatgpt-product-ux-m3`).
- 후속 서비스 CI 수정 브랜치: `feature/chatgpt-m3-service-final`.
- 본 문서는 GitHub Windows Runner와 로컬 JJJ Dev에서 실행한 자동 검증의 증거만 기록한다. 사용자 PC 2대의 GUI/현장 수용, 정식 배포, TightVNC 제거를 의미하지 않는다.

## 1. 인증된 Viewer/Server 파일 송수신
- Server 측 현재 사용자 승인 후 최대 64 MiB의 단일 파일 업로드/다운로드, HTTPS가 아닌 기존 TLS session 위 전송.
- 파일명 경로 순회/예약명/기존 대상 덮어쓰기 차단, 임시 파일과 SHA-256 전체 검증 후 rename, 중단 시 삭제.
- 일회성 수신 승인 및 Controller 권한 검증, 허가 취소/세션 종료 시 전달 중지. 한 연결당 동시 전송 1개.
- Lab xUnit 프로토콜·수신 상태·오류 경로·전송 청크 프레이밍 roundtrip 검증 포함. GUI에서 실제 다른 PC의 대용량 전송은 검증 안 됨.
- 미지원: 파일 이어받기, 폴더 전송, 64 MiB 초과 전송.

## 2. Viewer Connection Options 및 화면 조작
- Fit/50/75/100/125/150/200%, 패닝 스크롤, 로컬 Pause/Resume, PNG snapshot, Ctrl+Shift+Esc, 마우스 휠.
- View-only, Fullscreen, 인증서 프로필 제한 자동 재접속, JPEG Quality 25–90, FPS 2–20; 재접속 시 기존 TLS 핀 및 권한 유지.
- 화면 품질은 공유 캡처에 적용되어 다른 Viewer도 영향을 받는다. 3 Viewer/1 Controller 정책 유지.
- 잠금화면/UAC/Winlogon/Ctrl+Alt+Del은 미지원.

## 3. Windows Service + SessionAgent
- ServicePreview는 Compact per-user EXE와 분리된 관리자용 독립 ZIP이다. LocalSystem이 보호된 Program Files 경로에서 구동되고, 정책·사용자 2중 동의 없이는 원격 GUI Server를 실행하지 않는다.
- 승인된 현재 로그인 사용자 세션의 SessionAgent만 감독·재시작하며, 사용자 전환·로그오프 시 이전 세션 폐기. Windows 로그인 전/잠금 중 원격제어와 UAC/Winlogon 제어는 지원하지 않는다.
- Windows Service CI 재실행 #2 PASS: https://github.com/jjj0214/TJ.RemoteAccess/actions/runs/44216713935
  - 빌드/소스 ZIP+SHA256
  - 실제 SCM 설치, LocalSystem 시작, 리스너 OFF 확인 및 경로 ACL
  - 동일 버전 upgrade, 제거, 원래 리소스 보존
- 이전 실패 #1은 SCM 설치 단계에서 발생. 검증 코드의 잘못된 ACL 판정(Modify에 읽기 권한 포함)을 수정했으며 #2 최종 PASS.

## 4. 설치·최종 검증 범위
- Compact Installer CI #15 PASS: https://github.com/jjj0214/TJ.RemoteAccess/actions/runs/44208497265
- Core CI #34 PASS: https://github.com/jjj0214/TJ.RemoteAccess/actions/runs/44208497344
- 후속 워크트리 Release/Preview/Lab: Release PASS; Preview 54/54 PASS; Lab 187/187 PASS (새 파일전송 프레이밍 roundtrip 포함).
- Python 전체 테스트는 pristine TightVNC GPL 소스 폴더 미존재로 기존 1건 실패/4건 skip: 완전 통과로 표기 금지.
- **2대의 실제 Windows PC 실기 GUI 수용은 미실행**. LAN TLS/비밀번호 접속, 송수신 승인·취소, 배율/마우스 스크롤 좌표, 잠금·로그오프·재시작, 3 Viewer·1 Controller, 24시간 안정성, TightVNC 공존을 실제 테스트해야 한다.
- 기존 PC의 방화벽/TightVNC/서비스/설정은 임의 변경하지 않는다. 사용자 검증·승인 이전까지 main 병합과 정식 릴리즈는 보류.
