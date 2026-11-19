# TightVNC 2.8.90 -> TJ.RemoteAccess Server / Viewer 기능 대조표
2026-11-18 | 개발 기능 브랜치 `feature/chatgpt-tightvnc-parity` | **Service 패키징 제외**

## 출처 및 평가 원칙

실제 2.8.90 GPL 원본은 검증된 ZIP(sha256 `f3f76393a5865e1f1919c78c3eb43b9c937cf738927c2bfc920ecbd3913b061c`)의 추출본을 읽었다. 원본은 `TJ.RemoteAccess-ChatGPT-M2-Engine/third_party/tightvnc-2.8.90-gpl/`에 보관된다. 원본 1264개 파일 전수 SHA 검증과 M2 파생 소스 감사 이력은 별도로 확인됐다.

기능 증거:
- Viewer 메뉴: `tvnviewer/ViewerMenu.cpp` (새 연결, 세션 저장, 옵션, 연결 정보, 일시정지, 새로고침, 특수 키, 파일 전송, 툴바, 전체화면)
- Viewer 연결 옵션: `tvnviewer/OptionsDialog.cpp`, `ViewerCmdLine.cpp`, `ViewerWindow.cpp`, `ScaleManager.cpp`.
- 화면/입력: `viewer-core/RemoteViewerCore.cpp`, `CopyRectDecoder.cpp`, `TightDecoder.cpp`, `ZrleDecoder.cpp`, `RawDecoder.cpp`, `HexTileDecoder.cpp`, `CursorPainter.cpp`.
- 파일 관리자: `tvnviewer/FileTransferMainDialog.cpp`, `ft-client-lib/RemoteFileListOperation.cpp`, `RemoteFolderCreateOperation.cpp`, `RemoteFileRenameOperation.cpp`, `RemoteFilesDeleteOperation.cpp`, `UploadOperation.cpp`, `DownloadOperation.cpp`, `ft-server-lib/FileTransferRequestHandler.cpp`.
- Server 운영/접근 정책: `server-config-lib/ServerConfig.h`, `Configurator.cpp`, `IpAccessControl.cpp`, `IpAccessRule.cpp`, `tvnserver-app/RfbClientManager.cpp`, `QueryConnectionDialog.cpp`, `SessionChangesWatcher.cpp`, `TvnService.cpp`, `ExtraRfbServers.cpp`.

**상태:** 기존 = 이전 기능 브랜치 포함 / 신규 = 이 Parity 작업 브랜치에서 추가 / 부분 = 목적은 유사하지만 TightVNC와 동작 차이 / 미구현 = 없음 / 제외 = 현재 보안 설계에서 의도적 미채택. 본 문서는 소스 기반 검토이며 실기 GUI 검증 결과를 대신하지 않는다.

## Viewer 기능

| TightVNC 2.8.90 실제 기능 | TJ.RemoteAccess 대응 | 상태 / 차이 |
| --- | --- | --- |
| 새 연결 / 서버 주소·포트 | 저장된 프로필, Add/Edit/Remove, Quick IP+Password | 기존 |
| 연결 세션 .vnc 파일 저장/불러오기 | 인증서 PFX/프로필 내보내기, JSON 프로필 내부 저장 | 부분: .vnc 호환 세션 파일 없음; 암호 저장 방식은 복제하지 않음 |
| 기존 연결 정보 표시 | 주소/포트, TLS 버전, 서버 핀, 권한, 화면 크기, 시간/프레임 | **신규** |
| 수동 화면 Refresh | 인증된 TLS 제어 메시지, 최신 전체 JPEG 재전송, Viewer당 800ms 제한 | **신규**; 전체 새 캡처 요청이 아닌 최신 공유 프레임 재전송 |
| 일시정지/재개 | Viewer 표시 정지/재개; TLS 프레임 읽기는 유지 | 기존(부분: TightVNC는 화면 갱신 요청 정지) |
| Fit/고정 크기/+/−/100% | Fit + 10,15,25,50,75,90,100,125,150,200,400% | **신규 확장**; 버튼 +/- 대신 선택 콤보 |
| 전체 화면 / 툴바 숨김 | Fullscreen, Esc 복귀, 메뉴의 F8 툴바 on/off | **신규 확장** |
| Ctrl+Esc, Ctrl/Alt 고정, 특수키 | Ctrl+Esc, Alt+Tab, Ctrl+Shift+Esc, Ctrl/Alt latch | **신규**; Controller만, 키 해제·포커스 안전성 |
| Ctrl+Alt+Del / Winlogon / UAC | 지원하지 않음 | **제외**: 특별한 보안 데스크톱·권한 경계 요구 |
| 원격 마우스 휠/클릭/드래그 | TLS Controller 입력, 3 Viewer/1 Controller | 기존 |
| 좌·우 마우스 버튼 바꾸기 | Connection Options checkbox, 가운데 버튼 유지 | **신규** |
| 원격 커서 모양 업데이트/로컬 모양 선택 | 기본 로컬 커서, 원격 포인터 이벤트 처리 | 부분: 원본 RichCursor/PointerPos 옵션 없음 |
| Viewer 현지 Clipboard 자동 sync/disable | 텍스트 수동 Push/Pull; 매번 Server 승인 + Viewer 받기 승인, 16KiB | **신규 제한형**; 자동 sync/이미지/파일 복제는 제외 |
| Send/Receive 파일 | TLS + 전송당 Server 사용자 승인, SHA256 검증, 최대 4GiB/파일 | **개선**; 48KiB 스트리밍 청크, 파일 변경 시 송신 완료 거부 |
| 복수 파일 업로드 | Quick send 최대 32개, Transfer Files에서는 선택 파일·폴더를 순차 전송 | **신규 확장**; 전송당 Server 사용자 승인 |
| TightVNC Transfer Files 창 | Local/Remote 2분할, 드라이브 선택, 정렬, Windows 파일 드래그앤드롭 업로드, 진행률·속도·ETA, 파일별 성공/실패 이력 및 실패 재시도 | **개발·자동시험 적용**; 실제 Win10/11 DPI·파일 드롭 현장 검증은 최종 PC에서 진행 |
| 원격 파일 관리자 | Server 사용자 지정 공유 폴더(기본 개인 Received), 최대 10,000개/폴더 160개씩 페이징, 생성·이름변경·단일/빈 폴더 삭제, 원격 파일 복사, 폴더 업·다운로드 | **개발·자동시험 적용**; Server의 로컬 설정에서 탐색·업로드·다운로드·변경 별도 허용, 변경 작업별 재승인 |
| 대용량·중단 재개 | 4GiB/파일, 48KiB 청크, 최대 128항목·8GiB/선택 전송, SHA-256, 중단 부분파일(24시간 이내 정리), prefix SHA 검증 후 재접속 이어받기, 일시정지/재개, 취소 및 오류내역 | **개발·자동시험 적용**; 원자적 폴더 전체 동기화·무제한 대기 큐·한도 초과 파일은 미지원, 4GiB 실제 2-PC 전송 검증 필요 |
| 원격 화면 JPEG 품질 조정 | JPEG quality 25~90, FPS 2~20 | 기존; 3 Viewer 공동 캡처에 적용 |
| RFB Raw/Hextile/Tight/RRE/ZRLE/CopyRect | 자체 TLS 프로토콜 + JPEG 전체 화면, 프레임 내용 동일할 때 중복 전송 억제 | **의도적 프로토콜 차이**; 변경 영역 delta/CopyRect 및 TightVNC RFB 호환성은 미구현 |
| 8-bit/32-bit, compression level, cursor update | JPEG 품질/FPS만 설정 | 미구현 / 프로토콜 상 차이 |
| ViewOnly / Shared flag / 기존 클라이언트 연결 정책 | Viewer ViewOnly, 인증된 Viewer 최대 3 / Controller 최대 1 | 부분: Shared/Exclusive 수동 정책 미구현 |
| 재연결 / 연결 프로필 자동 재접속 | 인증서 프로필에 한해 3회 제한, 패스워드 자동 재사용 금지 | 기존 |
| Listener/Reverse VNC / Host+port CLI / 저장된 비밀번호 커맨드 | 프로필 인식 Viewer launch, 서버에 TLS 접속 | 부분/제외: 역방향 임의 접속이나 평문 비밀번호 CLI 허용 안 함 |
| 커서/화면 캡처 PNG | Viewer PNG snapshot | 기존 |
| Connection Options / Viewer 환경설정 | UI에 ViewOnly, Fullscreen, AutoReconnect, JPEG/FPS, Mouse Swap | 기존+신규 |
| 원격 벨/최소화 복구, 풀스크린 경고 | 없음 | 미구현; 우선순위 낮음 |

## Server 기능

| TightVNC 서버 실제 기능 | TJ.RemoteAccess 대응 | 상태 / 차이 |
| --- | --- | --- |
| 백그라운드 서비스/로그인 세션 관리 | 별도 M3 ServicePreview + SessionAgent; 현재 로그인 사용자 Default desktop만 | 부분; 이번 작업에서는 Service 패키지 제외 |
| 서버 원격 인증 / 관리 권한 | TLS 상호 인증/PFX Client roster, opt-in password TLS, Server fingerprint pin, 3명/1명 | 기존; 원본 DES VNC password 복제 안 함 |
| LAN/loopback/포트 설정 | Server 15544 mutual TLS, 15545 password TLS opt-in, allow-lan | 기존; 별도 HTTP 포트 없음 |
| Server tray/시작·정지/자동 실행 | 트레이, 시작/정지, 로그인 사용자 자동 시작 opt-in | 기존 |
| 클라이언트 공유 항상/never/새 연결 시 기존 끊기 | 3 Viewer + 1 Controller 제한 | 부분: per-session exclusive policy 없음 |
| IP ACL allow/deny 규칙, 포트 매핑 | PIN/역할 승인 및 Windows 방화벽 별도 운영 | 미구현; 필요 시 CIDR ACL 별도 설계 |
| 접속 전 Server Query/승인·타임아웃/기본 거부 | 인증서 PIN enrollment, 전송별 Server 사용자 승인 | 부분: 연결당 팝업 Query 없음 |
| Remote/Local Input block, local input 우선순위/timeout | Controller 권한 및 잠금 시 입력 차단 | 부분: 입력 우선순위·로컬 마우스 강제 차단 없음 |
| 연결 해제 시 잠금/로그오프 또는 그대로 | 기본 무변경, 로그오프/잠금 시 기존 원격 세션 종료 | 부분: Server 정책 선택 없음; 원격 잠금/로그오프 자동 실행은 opt-in 필요 |
| 파일 송수신 활성화/쓰기 경로 설정 | Server 사용자 지정 로컬 공유 폴더/기본 Received, 탐색·업로드·다운로드·변경 권한 분리, 작업마다 Server 승인, 재접속 시 공유 루트 변경 감지/거부, 수신 SHA-256/이름 충돌 처리 | **개발·자동시험 적용**; 드라이브 루트/UNC/reparse는 차단하고 사용자 PC UI 실기 필요 |
| 바탕화면 제거, D3D/미러 드라이버, Polling interval | 공동 JPEG 캡처, FPS 설정, 프레임 backlog 억제 | 부분: Windows 미러 드라이버/D3D API 미사용 |
| 화면 크기/멀티 모니터·커서 업데이트 | virtual screen shared JPEG, 최대 4096x2304 수신 제한 | 부분: 동적 디스플레이 변경/멀티 모니터 선택 없음 |
| RFB listener/HTTP Java Applet/reverse connect | 자체 TLS framed protocol, 인증된 원격 제어 | **제외**: 원본 보안 약점/브라우저 applet/평문 RFB 수신은 자동으로 열지 않음 |
| 로그/이벤트 기록/설정 내보내기 | TJRA Audit/상태, 개인 프로필 보존 및 보안 설정 | 부분: 로그 범위/회전/필터 설정 보강 필요 |

## 우선순위별 미완료

1. **P1 기능 사용성:** 실제 2-PC에서 새 메뉴·원격 키·배율 10~400%·컨트롤 역할·수동 클립보드 승인·다중파일 전송 검증. 프로토콜 버전/구버전 서버 혼합 시 협상·오류 UX 추가. 새 클립보드 기본 자동 동기화는 절대 켜지지 않는다.
2. **P1 파일 관리자 최종 검증:** 재접속 이어받기·일시정지·재시도·로컬 드롭·Server 개별 승인·10,000항목 페이징·4GiB/파일·8GiB 배치·권한 변경 즉시 적용이 소스 및 자동 테스트에 반영됨. 실제 2-PC 대역폭·네트워크 차단/재개·DPI·파일 잠금/접근 거부·4GiB 장시간 전송은 최종 PC에서 검증한다. 원자적 폴더 동기화와 제한 없는 배치·자동 업로드 큐는 범위 밖이다.
3. **P2 화면 엔진:** 영역별 갱신·CopyRect와 동등한 delta, 화면/모니터 선택, 해상도 변경 대응, JPEG/코덱 선택, 커서 전송, 세션별 FPS. TightVNC RFB 코덱을 그대로 쓰려면 별도 호환성/라이선스/보안 브리지 필요.
4. **P2 Server 정책:** 접속 요청 팝업/타임아웃, LAN CIDR 허용 목록, view-only 승인, Client별 권한, local-input-priority, 마지막 연결 종료 정책을 UI·테스트와 함께 추가.
5. **P3 별도 Service:** 이미 로그인 사용자에 대한 Supervisor 경로, 로그오프/재부팅/보호 데스크톱 정책 실기 검증. Windows 로그인 전/Winlogon/UAC 제어는 별도의 고권한 보안 설계 없이는 지원하지 않는다.
6. **P3 품질 및 정식 배포:** Native C++ 별도 바이너리 테스트 skip 해소, 24시간 3 Viewer 성능/메모리/검사 Tact, 안전한 공존·롤백, Windows 10/11, 서명/원본 GPL 고지, 통합 Installer CI, 사용자 승인 후 main 병합.

## 자동 검증과 릴리즈 경계

`TJ.RemoteAccess.sln` Release 빌드, Preview/Lab xUnit 및 Python 계약 테스트로 회귀 검증. 네이티브 Windows 사용자 UI, 클립보드의 실제 STA 접근, 2대 PC 파일전송, Service와 전용 Windows 데스크톱, 3 Viewer 성능은 자동 테스트만으로 통과를 주장하지 않는다. 일반 Compact 설치기에는 별도 M3 Service가 포함되지 않는다. 기존 TightVNC를 제거/중지/변경하지 않는다.
