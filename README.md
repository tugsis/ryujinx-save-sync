# RyuSync

Windows 11과 SteamOS에서 Ryujinx User Save를 WebDAV로 동기화하는 로컬 애플리케이션입니다. C#/.NET 백엔드가 파일과 작업을 관리하고, React 설정 페이지를 같은 로컬 서버에서 제공합니다.

**개발 초기 버전입니다.** 합성 세이브와 로컬 WebDAV 서버로 자동 검증했습니다. 실제 Ryujinx 빌드·NAS·SteamOS Gaming Mode의 호환성 검증은 아직 수행하지 않았습니다. 처음에는 복제한 테스트 데이터로 확인하세요.

## 구현한 기능

- IMKV 세이브 인덱스 읽기 전용 탐색: 게임 Title ID, 사용자 ID, SaveDataId와 실제 경로 연결.
- 게임 메타데이터에서 제목 표시, 게임·프로필별 동기화 대상 선택.
- WebDAV 실제 연결 테스트, 원격 게임 목록, 전체·게임별 양방향 동기화.
- 내용 해시와 부모 revision을 이용한 변경·동시 업로드 충돌 판별.
- 충돌 시 양쪽 보존 및 사용할 버전 선택. 파일별 바이너리 병합은 하지 않습니다.
- ZIP·Manifest 무결성 검증, 경로 탈출·링크·Windows 예약 이름·크기 제한 검사.
- 로컬 적용 전 세이브 루트 전체 백업과 디렉터리 교체 저널.
- SQLite 설정·기준 버전·작업·실행 세션·미전송 스냅샷 기록.
- 같은 백엔드를 사용하는 웹 수동 동기화와 실행 전·후 동기화 런처.
- 루프백 전용 API, 일회성 설정 링크, HttpOnly 세션 및 Host/Origin 검사.

## 개발 실행

.NET 10 SDK, Node.js 20.19+ 또는 22.12+, npm이 필요합니다. Linux의 자동 통합 테스트에는 Python 3.10+가 필요합니다.

저장소 루트에서 실행합니다.

```sh
npm --prefix frontend ci
npm --prefix frontend run build
dotnet run --project backend/RyuSync.Api -- serve
```

콘솔의 `http://127.0.0.1:17843/#token=...` 전체 링크를 브라우저로 여세요. 이 링크는 로컬 설정 권한을 포함하므로 공유하지 마세요. 최초 접속 후 토큰은 주소에서 제거됩니다.

다른 데이터 디렉터리나 포트를 사용하려면:

```sh
dotnet run --project backend/RyuSync.Api -- serve --data-dir ./data --port 17843
```

브라우저 창을 닫아도 백엔드는 계속 실행됩니다. `Ctrl+C`로 백엔드를 종료할 수 있습니다. 실행 중인 백엔드의 설정 페이지를 다시 열려면:

```sh
dotnet run --project backend/RyuSync.Api -- open
```

`frontend`의 `npm run dev`는 화면 개발용입니다. 인증·쿠키·Origin 검증까지 확인하려면 위의 빌드 후 백엔드 제공 화면을 사용하세요.

## 최초 설정

1. 각 기기의 **Ryujinx 데이터 루트**를 지정해 저장하고 게임 탐색을 실행합니다. `bis/user/save` 자체가 아니라 `bis`, `games`, `system`을 포함하는 상위 폴더입니다.
2. 동기화할 게임을 등록합니다. 같은 사용자의 두 기기에서는 같은 **프로필 연결 이름**(예: `main`)을 지정합니다. 다른 사용자는 다른 이름을 사용하세요.
3. 두 기기에 같은 WebDAV 주소·계정·동기화 공간 이름을 설정합니다. 서버의 `ryusync/<공간 이름>` 아래에 버전별 데이터를 보관합니다.
4. 비밀번호를 입력하고 저장 후 연결 테스트를 수행합니다. 연결 테스트는 전용 테스트 파일을 생성하고 삭제합니다.
5. 전체 또는 게임별 동기화를 실행합니다. 처음 연결한 로컬·서버 내용이 다르면 작업 기록에서 사용할 버전을 선택합니다.

비밀번호는 **현재 백엔드 프로세스의 메모리에만** 보관합니다. 재시작하면 웹에서 다시 입력해야 합니다. OS 자격 증명 저장은 아직 미구현입니다. 자동화 환경에서는 `RYUSYNC_WEBDAV_PASSWORD` 환경변수를 사용할 수 있지만 명령행 인수·Git에 비밀번호를 넣지 마세요.

서버에만 세이브가 있고 로컬 인덱스가 없으면 Ryujinx에서 게임을 한 번 실행해 저장 위치를 생성한 뒤 다시 탐색합니다. 양쪽 데이터가 다르면 서버 버전을 명시적으로 선택하세요. 폴더 번호를 추측해서 만들거나 Ryujinx 내부 인덱스를 수정하지 않습니다.

## Steam 비 Steam 게임 등록

먼저 프론트엔드를 빌드하고 앱을 배포 폴더로 출력합니다.

```sh
npm --prefix frontend run build
dotnet publish backend/RyuSync.Api -c Release -o artifacts/local
```

이 명령은 .NET 10 ASP.NET Core 런타임이 설치된 PC용입니다. 런타임을 포함한 배포는 [개발 및 배포 문서](docs/development.md)를 참고하세요.

Steam에서 다음을 등록합니다.

- 대상: Windows의 `artifacts/local/ryusync.exe`, Linux의 `artifacts/local/ryusync`.
- 시작 위치: 같은 배포 디렉터리.
- 실행 옵션: `launch`.
- 앱의 웹 설정: 실제 Ryujinx 실행 파일·작업 디렉터리·인수를 지정합니다. 인수는 한 줄에 하나씩 입력합니다.

Linux용 앱은 Linux 네이티브 실행 환경을 사용합니다. Windows용 `.exe`와 혼동하지 마세요.

런처는 백엔드가 없으면 시작하고, 등록 게임 전체를 동기화한 뒤 Ryujinx를 실행합니다. **Ryujinx 프로세스를 종료하면** 변경된 세이브를 동기화합니다. 게임에서 메뉴로 돌아오는 것만으로는 종료로 처리하지 않습니다.

웹에서 플레이 중 요청한 동기화는 종료 후 작업에 포함됩니다. 실행 전 충돌·서버 오류가 있으면 게임 실행을 보류하고 설정 페이지를 엽니다. 현재는 오프라인 실행 우회와 Gaming Mode 전용 컨트롤러 창이 미구현입니다. 이 개발판은 직접 실행한 네이티브 프로세스를 추적하며, 분리 실행하는 셸 래퍼·Flatpak·일부 AppImage의 종료 추적은 미검증입니다.

## 테스트

```sh
dotnet test backend/RyuSync.Tests
npm --prefix frontend run build
dotnet build backend/RyuSync.Api
```

Linux에서 실제 HTTP·WebDAV·실행 전후 통합 테스트:

```sh
python3 tests/integration.py
```

기본 `dotnet` 대신 특정 SDK 실행 파일을 사용하려면 `DOTNET` 환경변수로 지정할 수 있습니다. 통합 테스트는 임시 폴더에 합성 인덱스·세이브를 만들며 실제 Ryujinx 데이터를 읽지 않습니다.

## 현재 제한

- Google Drive, OS 자격 증명 저장, 주기적 자동 동기화, 컨트롤러 전용 진행 창은 후속 구현입니다.
- 자동 탐색은 LibHac 0.19.0의 64바이트 키·값 인덱스 구조만 허용하며 다른 형식은 중지합니다.
- 동기화는 등록된 게임과 프로필만 대상으로 합니다. 프로필 짝을 자동 확정하지 않습니다.
- 백업·원격 이력을 자동 삭제하지 않습니다. 디스크 사용량을 확인하세요.
- 백업 복원 버튼은 아직 없으며, [복구 문서](docs/recovery.md)에 수동 복구 절차가 있습니다.
- 재시작 후 미전송 스냅샷은 다음 동기화 요청에서 다시 게시합니다. 비밀번호가 다시 입력되어야 합니다.
- 미완료 디렉터리 교체가 있으면 동기화를 차단하고 복사본을 보존합니다. 자동으로 알 수 없는 파일을 덮어쓰지 않습니다.
- 강제 전원 종료·디스크 장애·외부에서 동시에 실행한 에뮬레이터까지 완전히 통제할 수는 없습니다.

## 구조

```text
backend/RyuSync.Core/      탐색·스냅샷·버전·WebDAV·작업·SQLite
backend/RyuSync.Api/       로컬 API·정적 화면 제공·런처 CLI
backend/RyuSync.Tests/     안전성·충돌·전송 테스트
frontend/                 실제 백엔드에 연결한 React/TypeScript UI
settings-preview/         초기 디자인 초안 (시뮬레이션, 기존 보존)
tests/integration.py      합성 데이터 기반 HTTP 통합 테스트
PLAN.md                   최종 목표 기획
```

[구현 현황](docs/implementation-status.md) · [복구 절차](docs/recovery.md) · [라이선스](LICENSE)
