# TJ.RemoteAccess Lab — 3 Viewer 공유 화면 캡처와 성능 측정 (2026-11-12)

## 구조 변경

이전: 각 Viewer 세션의 SendFrames가 개별적으로 화면 캡처+JPEG 압축. 접속자 3명일 때 동일 화면이 3번 캡처되는 구조였다.

개선: Server별 **1개 CaptureFramesShared 생산 루프**에서 화면 캡처/JPEG 압축을 수행한 뒤, 최신 JPEG 프레임을 공유 저장소에 기록한다. 최대 3명 Viewer는 자기 TLS 스트림으로 최신 프레임만 전송받는다.

- 각 Viewer 인증서/ACL, TLS 암호화/송신 잠금은 여전히 서로 독립적
- 오래된 프레임은 전송 대기열에 쌓이지 않고 건너뜀
- 최신 프레임은 immutable 참조로 공유하므로 Viewer별 JPEG 복사본을 만들지 않음
- Viewer 0명일 때 화면 캡처하지 않음. 서버 재시작 시 공유 저장소도 교체
- 연결 끊김/서버 Stop 시 이전 세션 입력을 해제하는 기존 정책 유지
- 영상 전송 간격은 원칙적으로 약 160ms + JPEG 압축 시간으로 제한. 실제 FPS와 대기 시간은 GPU/CPU/네트워크 영향

## 성능 비교 방법

각 PC에서 설치된 Server/Viewer를 최신 커밋으로 업데이트. 기존 인증서, IP, server.json은 유지.

서버 PC에서 Viewer를 **1명만** 연결한 뒤 PowerShell:

    powershell -NoProfile -ExecutionPolicy Bypass -File .\tools\Measure-TJRemoteAccess-Lab.ps1 -DurationSeconds 30 -IntervalSeconds 5

동일 서버에 **3명** 접속한 상태에서 같은 명령을 실행한다.

PowerShell JSON 출력에서 Server 프로세스의 MachineCPUPercent, WorkingSetMB, PrivateMemoryMB, Threads, Handles를 비교한다. 첫 샘플은 CPU 기준점이 없어 MachineCPUPercent=null일 수 있다.

- CPU 사용률은 시스템의 논리 프로세서 수로 정규화한 값
- Viewer별 CPU도 표시하지만 서로 다른 PC에서 실행 중이면 각 PC에서 개별 수행
- 프로그램 우선순위나 서비스/방화벽을 조정하지 않는 **읽기 전용** 샘플러
- 데이터는 표준 출력으로만 내보내므로 사용자가 원할 때만 파일로 리다이렉트한다

## 검증 항목

1. Viewer 1명/2명/3명 시 화면과 키보드·마우스 정상
2. 3명 상태에서 Viewer 한 대 네트워크 단절 시 나머지 2대의 영상 유지
3. 4번째 Viewer 연결 차단 유지, Controller 1명 정책 유지
4. 로그인 사용자 PC에서 Server Stop→Start 후 stale 화면이 표시되지 않는지
5. 1명 대비 3명 상태의 CPU/작업 집합 메모리가 비정상적으로 증가하지 않는지
6. 원본 TightVNC 및 검사 프로그램에 영향을 주지 않는지
7. 30분 이상의 연속 연결에서 접속 슬롯 회수 및 메모리 증가 경향

## 검증 경계

자동 xUnit 검증은 공유 프레임 참조, 시퀀스·최신 상태, 3개 Reader의 동시 읽기/안전한 프레임 건너뛰기를 포함한다. 실제 Windows GDI 화면 캡처 부하와 네트워크 왕복 지연, 디스플레이 재구성, 장시간 메모리 증감은 **실기 검증 전**이다. 실제 성능 수치는 추정/기록하지 않았다.
