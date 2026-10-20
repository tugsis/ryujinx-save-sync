# RyuSync

Windows 11과 SteamOS에서 Ryujinx의 User Save를 WebDAV 또는 Google Drive로 동기화하기 위한 프로젝트입니다.

현재는 **기획서와 인터랙티브 UI 초안** 단계입니다. 실제 세이브 탐색, 파일 전송, Steam 런처와 백엔드는 아직 구현하지 않았습니다.

## 기획

[PLAN.md](PLAN.md)에 아키텍처, 자동 세이브 탐색, 버전·충돌 관리, Steam 실행 전·후 동기화, 웹 수동 동기화와 개발 단계를 정리했습니다.

## 설정 페이지 초안

[온라인 초안](https://ryusync-settings.creamyshark11.chatgpt.site)은 소유자 전용으로 배포되어 있어 다른 사용자는 접근하지 못할 수 있습니다.

로컬에서는 별도 빌드 없이 실행할 수 있습니다.

```sh
python3 -m http.server 8080 --directory settings-preview/dist
```

Windows에서는 환경에 따라 `python3` 대신 `py`를 사용하세요. 브라우저에서 `http://localhost:8080`을 엽니다.

- WebDAV / Google Drive 설정 전환
- 게임 자동 탐색 및 게임 선택 시뮬레이션
- 동기화 설정을 브라우저에 저장
- 연결 테스트와 동기화 작업의 예시 기록

실제 서버 요청이나 파일 변경은 수행하지 않습니다. 비밀번호는 저장하지 않지만 다른 설정은 브라우저 저장소에 남으므로 예시 값으로 확인하세요.

## 파일 구성

```text
PLAN.md                         제품·개발 기획서
settings-preview/dist/          빌드 없이 실행하는 HTML·CSS·JavaScript
settings-preview/.openai/        기존 Sites 배포 식별 정보
LICENSE                         기존 저장소의 Apache-2.0 라이선스
```

`dist` 디렉터리는 현재 UI 초안의 원본이므로 버전 관리에 포함합니다.

## 확인

```sh
node --check settings-preview/dist/app.js
```

현재 백엔드 통합 테스트는 없습니다.
